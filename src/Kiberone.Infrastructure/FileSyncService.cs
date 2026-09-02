using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Infrastructure;

public sealed class FileSyncService
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".scr", ".dll", ".com", ".zip", ".msi" };
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".venv", "__pycache__", ".git", "node_modules", "$RECYCLE.BIN", ".history" };
    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
        { "Thumbs.db", "desktop.ini", ".DS_Store" };
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);

    private readonly DbContextOptions<ClassroomDbContext> options;
    private readonly string root;
    private string rosterRoot;
    private readonly ConcurrentDictionary<string, Guid> clientStudents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> lessonModules = new();

    public FileSyncService(DbContextOptions<ClassroomDbContext> options, string root)
    {
        this.options = options;
        this.root = Path.GetFullPath(root);
        rosterRoot = DefaultRosterRoot(this.root);
        Directory.CreateDirectory(this.root);
        Directory.CreateDirectory(rosterRoot);
    }

    public string RosterRoot => rosterRoot;

    public static string DefaultRosterRoot(string syncRoot) =>
        Path.GetFullPath(Path.Combine(Path.GetFullPath(syncRoot), "..", "groups"));

    public void SetRosterRoot(string? path)
    {
        rosterRoot = string.IsNullOrWhiteSpace(path)
            ? DefaultRosterRoot(root)
            : Path.GetFullPath(path.Trim());
        Directory.CreateDirectory(rosterRoot);
    }

    public void BindClient(string clientId, Guid? studentId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return;
        if (studentId is null || studentId == Guid.Empty)
            clientStudents.TryRemove(clientId.Trim(), out _);
        else
            clientStudents[clientId.Trim()] = studentId.Value;
    }

    public void SetLessonModule(Guid studentId, string? module)
    {
        if (studentId == Guid.Empty) return;
        if (string.IsNullOrWhiteSpace(module))
            lessonModules.TryRemove(studentId, out _);
        else
            lessonModules[studentId] = module.Trim();
    }

    public string? GetLessonModule(Guid studentId) =>
        lessonModules.TryGetValue(studentId, out var module) ? module : null;

    public async Task<string?> ResolveSaveModuleAsync(Guid studentId, CancellationToken ct = default) =>
        (await ResolveStudentHomeAsync(studentId, ct))?.Module;

    public async Task<StudentSaveHome?> ResolveStudentHomeAsync(Guid studentId, CancellationToken ct = default)
    {
        var target = await ResolveStudentTargetAsync(studentId, ct);
        if (target is null) return null;
        var name = $"{target.LastName} {target.FirstName}".Trim();
        return new StudentSaveHome(name, target.Module);
    }

    public static string StudentDesktopFolder(string studentDisplayName, string? module = null)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        var home = Path.Combine(desktop, SanitizeFolderName(studentDisplayName));
        return string.IsNullOrWhiteSpace(module) ? home : Path.Combine(home, SanitizeFolderName(module));
    }

    public async Task<SyncPrepareResult> PrepareAsync(SyncPrepareRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId)) throw new LessonValidationException(["client_id обязателен."]);
        if (request.StudentId is Guid studentId)
            BindClient(request.ClientId, studentId);
        foreach (var change in request.Changes) ValidateRelativePath(change.Path);
        if (request.LocalFiles is not null)
            foreach (var file in request.LocalFiles) ValidateRelativePath(file.Path);

        var threshold = Math.Clamp(request.Threshold ?? 5, 1, 1000);
        StoredPlan plan;
        var reasons = new List<string>();
        if (request.LocalFiles is { Count: >= 0 } local)
        {
            plan = await BuildPlanAsync(request.ClientId, local, ct);
            if (plan.Conflicts.Count > 0)
                reasons.Add($"версии различаются: {string.Join(", ", plan.Conflicts.Take(5))}");
            var dangerous = plan.Upload.Concat(plan.Conflicts)
                .Where(path => DangerousExtensions.Contains(Path.GetExtension(path)));
            if (dangerous.Any()) reasons.Add("опасный тип файла");
        }
        else
        {
            foreach (var change in request.Changes) ValidateRelativePath(change.Path);
            var uploads = request.Changes.Where(x => x.Kind != SyncChangeKind.Deleted).Select(x => Normalize(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var deletes = request.Changes.Where(x => x.Kind == SyncChangeKind.Deleted).Select(x => Normalize(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plan = new StoredPlan(uploads, [], [], request.Changes.ToList());
            if (request.Changes.Any(x => x.Kind != SyncChangeKind.Deleted && DangerousExtensions.Contains(Path.GetExtension(x.Path))))
                reasons.Add("опасный тип файла");
            if (request.Changes.Count(x => x.Kind is SyncChangeKind.Modified or SyncChangeKind.Deleted) > threshold)
                reasons.Add($"изменено или удалено больше {threshold} файлов");
            if (request.AcceptedWasNonempty && request.ResultingIsEmpty) reasons.Add("рабочая папка стала пустой");
            _ = deletes;
        }

        var required = reasons.Count > 0;
        if (!required)
            plan = ResolveDecision(plan, takeStudent: true);

        var approval = new SyncApproval
        {
            ClientId = request.ClientId.Trim(),
            ChangesJson = JsonSerializer.Serialize(plan, PlanJson),
            Reason = string.Join("; ", reasons),
            Status = required ? SyncApprovalStatus.Pending : SyncApprovalStatus.NotRequired
        };
        await using var db = new ClassroomDbContext(options);
        db.SyncApprovals.Add(approval);
        await db.SaveChangesAsync(ct);
        return ToResult(approval, required, plan);
    }

    public async Task<SyncPrepareResult?> GetApprovalAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var all = await db.SyncApprovals.AsNoTracking().Where(x => x.ClientId == clientId).ToListAsync(ct);
        var approval = all.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        return approval is null ? null : ToResult(approval, approval.Status is not SyncApprovalStatus.NotRequired, ReadPlan(approval.ChangesJson));
    }

    public async Task<IReadOnlyList<SyncApproval>> ListPendingApprovalsAsync(CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var approvals = await db.SyncApprovals.AsNoTracking().Where(x => x.Status == SyncApprovalStatus.Pending).ToListAsync(ct);
        return approvals.OrderBy(x => x.CreatedAt).ToList();
    }

    public Task<SyncPrepareResult?> DecideAsync(Guid id, bool approved, CancellationToken ct = default) =>
        DecideAsync(id, approved ? "update" : "restore", ct);

    public async Task<SyncPrepareResult?> DecideAsync(Guid id, string action, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var approval = await db.SyncApprovals.FindAsync([id], ct);
        if (approval is null) return null;
        if (approval.Status != SyncApprovalStatus.Pending) throw new InvalidOperationException("Решение по этому запросу уже принято.");
        var takeStudent = !string.Equals(action, "restore", StringComparison.OrdinalIgnoreCase);
        var plan = ResolveDecision(ReadPlan(approval.ChangesJson), takeStudent);
        approval.ChangesJson = JsonSerializer.Serialize(plan, PlanJson);
        approval.Status = takeStudent ? SyncApprovalStatus.Approved : SyncApprovalStatus.Restore;
        approval.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToResult(approval, true, plan);
    }

    public async Task CompleteAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var candidates = await db.SyncApprovals.Where(x => x.ClientId == clientId &&
            (x.Status == SyncApprovalStatus.Approved
             || x.Status == SyncApprovalStatus.NotRequired
             || x.Status == SyncApprovalStatus.Restore)).ToListAsync(ct);
        var latest = candidates.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (latest is null) throw new InvalidOperationException("Нет активной синхронизации.");
        latest.Status = SyncApprovalStatus.Completed;
        latest.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<SyncedFileInfo> UploadAsync(string clientId, string relativePath, Stream content, CancellationToken ct = default)
    {
        await EnsureCanSyncAsync(clientId, ct);
        var destination = ResolvePath(clientId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".upload-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxUploadBytes) throw new LessonValidationException(["Файл превышает лимит 50 МБ."]);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            var newHash = await HashFileAsync(temporary, ct);
            if (File.Exists(destination))
            {
                var oldHash = await HashFileAsync(destination, ct);
                if (oldHash == newHash) return ToFileInfo(relativePath, destination, newHash);
                await ArchiveAsync(clientId, relativePath, destination, oldHash, "До изменения", ct);
            }
            File.Move(temporary, destination, true);
            if (!await HasVersionsAsync(clientId, relativePath, ct))
                await ArchiveAsync(clientId, relativePath, destination, newHash, "Первая загрузка", ct);
            return ToFileInfo(relativePath, destination, newHash);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task DeleteAsync(string clientId, string relativePath, CancellationToken ct = default)
    {
        await EnsureCanSyncAsync(clientId, ct);
        var path = ResolvePath(clientId, relativePath);
        if (!File.Exists(path)) return;
        await ArchiveAsync(clientId, relativePath, path, await HashFileAsync(path, ct), "Перед удалением", ct);
        File.Delete(path);
    }

    public Task<Stream?> OpenDownloadAsync(string clientId, string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var path = ResolvePath(clientId, relativePath);
        Stream? stream = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        return Task.FromResult(stream);
    }

    public async Task<IReadOnlyList<SyncedFileInfo>> ListFilesAsync(string clientId, CancellationToken ct = default)
    {
        var clientRoot = GetStorageRoot(clientId);
        if (!Directory.Exists(clientRoot)) return [];
        var result = new List<SyncedFileInfo>();
        foreach (var path in Directory.EnumerateFiles(clientRoot, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(clientRoot, path).Replace('\\', '/');
            if (relative.Split('/').Any(ExcludedDirectories.Contains) || ExcludedFiles.Contains(Path.GetFileName(relative))) continue;
            result.Add(ToFileInfo(relative, path, await HashFileAsync(path, ct)));
        }
        return result.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<FileVersionInfo>> ListVersionsAsync(string clientId, string relativePath, CancellationToken ct = default)
    {
        ValidateRelativePath(relativePath);
        await using var db = new ClassroomDbContext(options);
        var versions = await db.SyncedFileVersions.AsNoTracking().Where(x => x.ClientId == clientId && x.RelativePath == Normalize(relativePath)).ToListAsync(ct);
        return versions.OrderByDescending(x => x.CreatedAt).Select(x => new FileVersionInfo(x.Id.ToString("N"), x.RelativePath, x.Size, x.Sha256, x.CreatedAt, x.Label)).ToList();
    }

    public async Task<SyncedFileInfo> RestoreVersionAsync(RestoreVersionRequest request, CancellationToken ct = default)
    {
        ValidateRelativePath(request.Path);
        if (!Guid.TryParse(request.VersionId, out var versionId)) throw new LessonValidationException(["Некорректный ID версии."]);
        await using var db = new ClassroomDbContext(options);
        var version = await db.SyncedFileVersions.SingleOrDefaultAsync(x => x.Id == versionId && x.ClientId == request.ClientId && x.RelativePath == Normalize(request.Path), ct)
            ?? throw new KeyNotFoundException("Версия не найдена.");
        if (!File.Exists(version.StoragePath)) throw new KeyNotFoundException("Файл версии отсутствует.");
        var destination = ResolvePath(request.ClientId, request.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) await ArchiveAsync(request.ClientId, request.Path, destination, await HashFileAsync(destination, ct), "Перед восстановлением", ct);
        File.Copy(version.StoragePath, destination, true);
        return ToFileInfo(request.Path, destination, version.Sha256);
    }

    private async Task EnsureCanSyncAsync(string clientId, CancellationToken ct)
    {
        await using var db = new ClassroomDbContext(options);
        var approvals = await db.SyncApprovals.AsNoTracking().Where(x => x.ClientId == clientId).ToListAsync(ct);
        var latest = approvals.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (latest is null || DateTimeOffset.UtcNow - latest.CreatedAt > TimeSpan.FromHours(1)) throw new InvalidOperationException("Сначала подготовьте синхронизацию.");
        if (latest.Status == SyncApprovalStatus.Pending) throw new InvalidOperationException("Ожидается решение тьютора.");
        if (latest.Status == SyncApprovalStatus.Rejected) throw new InvalidOperationException("Синхронизация отклонена тьютором.");
        if (latest.Status == SyncApprovalStatus.Completed) throw new InvalidOperationException("Синхронизация уже завершена.");
    }

    private async Task ArchiveAsync(string clientId, string relativePath, string source, string hash, string label, CancellationToken ct)
    {
        var historyRoot = Path.Combine(GetStorageRoot(clientId), ".history", Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(relativePath)))).ToLowerInvariant());
        Directory.CreateDirectory(historyRoot);
        var version = new SyncedFileVersion { ClientId = clientId, RelativePath = Normalize(relativePath), Sha256 = hash, Size = new FileInfo(source).Length, Label = label, StoragePath = Path.Combine(historyRoot, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.bin") };
        File.Copy(source, version.StoragePath, false);
        await using var db = new ClassroomDbContext(options);
        db.SyncedFileVersions.Add(version);
        await db.SaveChangesAsync(ct);
        var all = await db.SyncedFileVersions.Where(x => x.ClientId == clientId && x.RelativePath == version.RelativePath).ToListAsync(ct);
        foreach (var stale in all.OrderByDescending(x => x.CreatedAt).Skip(30))
        {
            if (File.Exists(stale.StoragePath)) File.Delete(stale.StoragePath);
            db.SyncedFileVersions.Remove(stale);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> HasVersionsAsync(string clientId, string relativePath, CancellationToken ct)
    {
        await using var db = new ClassroomDbContext(options);
        var normalized = Normalize(relativePath);
        return await db.SyncedFileVersions.AnyAsync(x => x.ClientId == clientId && x.RelativePath == normalized, ct);
    }

    private string ResolvePath(string clientId, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var clientRoot = GetStorageRoot(clientId);
        var full = Path.GetFullPath(Path.Combine(clientRoot, Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        var prefix = clientRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new LessonValidationException(["Путь выходит за рабочую папку ученика."]);
        return full;
    }

    private string GetStorageRoot(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new LessonValidationException(["client_id обязателен."]);
        if (clientStudents.TryGetValue(clientId.Trim(), out var studentId))
        {
            var target = ResolveStudentTargetAsync(studentId).GetAwaiter().GetResult();
            if (target is not null)
                return EnsureStudentModuleFolder(target.GroupName, target.LastName, target.FirstName, target.Module);
        }
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientId.Trim())))[..24].ToLowerInvariant();
        return Path.Combine(root, key);
    }

    public string GetClientFolderPath(string clientId)
    {
        var path = GetStorageRoot(clientId);
        Directory.CreateDirectory(path);
        return path;
    }

    public string EnsureGroupFolder(string groupName)
    {
        var path = Path.Combine(rosterRoot, SanitizeFolderName(groupName));
        Directory.CreateDirectory(path);
        return path;
    }

    public string EnsureStudentFolder(string groupName, string lastName, string firstName)
    {
        var studentName = $"{lastName} {firstName}".Trim();
        var path = Path.Combine(EnsureGroupFolder(groupName), SanitizeFolderName(studentName));
        Directory.CreateDirectory(path);
        return path;
    }

    public string EnsureStudentModuleFolder(string groupName, string lastName, string firstName, string module)
    {
        var path = Path.Combine(EnsureStudentFolder(groupName, lastName, firstName), SanitizeFolderName(string.IsNullOrWhiteSpace(module) ? "модуль" : module));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string SanitizeFolderName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "без имени";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(trimmed.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' ? '_' : ch).ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "без имени" : cleaned;
    }

    private async Task<StudentSyncTarget?> ResolveStudentTargetAsync(Guid studentId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var student = await db.Students.AsNoTracking().Include(x => x.Group).SingleOrDefaultAsync(x => x.Id == studentId, ct);
        if (student is null) return null;
        var allowed = await db.GroupProgramModules.AsNoTracking()
            .Where(x => x.GroupId == student.GroupId)
            .Select(x => x.Name)
            .ToListAsync(ct);
        var module = GetLessonModule(studentId);
        if (string.IsNullOrWhiteSpace(module)
            || allowed.All(name => !string.Equals(name, module, StringComparison.OrdinalIgnoreCase)))
            module = student.Group?.Module ?? "";
        return new StudentSyncTarget(student.LastName, student.FirstName, student.Group?.Name ?? "группа", module);
    }

    private async Task<StoredPlan> BuildPlanAsync(string clientId, IReadOnlyList<SyncFileFingerprint> localFiles, CancellationToken ct)
    {
        var local = localFiles
            .GroupBy(x => Normalize(x.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var remote = (await ListFilesAsync(clientId, ct)).ToDictionary(x => x.Path, x => x, StringComparer.OrdinalIgnoreCase);
        var upload = new List<string>();
        var download = new List<string>();
        var conflicts = new List<string>();
        foreach (var (path, file) in local)
        {
            if (!remote.TryGetValue(path, out var server))
                upload.Add(path);
            else if (!string.Equals(server.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                conflicts.Add(path);
        }
        foreach (var path in remote.Keys)
        {
            if (!local.ContainsKey(path))
                download.Add(path);
        }
        return new StoredPlan(upload.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            download.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            conflicts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            []);
    }

    private static StoredPlan ResolveDecision(StoredPlan plan, bool takeStudent)
    {
        if (plan.Conflicts.Count == 0) return plan;
        if (takeStudent)
            return plan with
            {
                Upload = plan.Upload.Concat(plan.Conflicts).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Download = plan.Download.Where(path => !plan.Conflicts.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList(),
                Conflicts = []
            };
        return plan with
        {
            Download = plan.Download.Concat(plan.Conflicts).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Upload = plan.Upload.Where(path => !plan.Conflicts.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList(),
            Conflicts = []
        };
    }

    private static StoredPlan ReadPlan(string json)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<StoredPlan>(json, PlanJson);
            if (plan is not null)
                return new StoredPlan(plan.Upload ?? [], plan.Download ?? [], plan.Conflicts ?? [], plan.Changes ?? []);
        }
        catch (JsonException)
        {
        }
        return new StoredPlan([], [], [], []);
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || IsAbsoluteSyncPath(path)) throw new LessonValidationException(["Требуется относительный путь."]);
        var normalized = Normalize(path);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(x => x is "." or "..") || parts.Any(ExcludedDirectories.Contains) || ExcludedFiles.Contains(parts[^1]))
            throw new LessonValidationException(["Путь запрещён для синхронизации."]);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    private static bool IsAbsoluteSyncPath(string path)
    {
        if (Path.IsPathRooted(path)) return true;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal)) return true;
        return normalized.Length >= 2
            && char.IsAsciiLetter(normalized[0])
            && normalized[1] == ':'
            && (normalized.Length == 2 || normalized[2] is '/' or '\\');
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private static SyncedFileInfo ToFileInfo(string relativePath, string path, string hash) =>
        new(Normalize(relativePath), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), hash);

    private static SyncPrepareResult ToResult(SyncApproval approval, bool required, StoredPlan plan) =>
        new(approval.Id, required, approval.Status, approval.Reason, approval.CreatedAt, plan.Upload, plan.Download);

    private sealed record StoredPlan(List<string> Upload, List<string> Download, List<string> Conflicts, List<SyncChange> Changes);
    private sealed record StudentSyncTarget(string LastName, string FirstName, string GroupName, string Module);
}
