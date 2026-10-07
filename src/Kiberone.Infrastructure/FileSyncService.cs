using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Infrastructure;

public sealed class FileSyncService
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;
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
    public bool AutoApproveSafeFiles { get; set; } = true;

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
        return new StudentSaveHome(name, target.Module, target.ModuleFolders);
    }

    public static string StudentDesktopFolder(string studentDisplayName, string? module = null)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        // Module stays on the tutor PC. The student watches a flat desktop folder.
        _ = module;
        return Path.Combine(desktop, SanitizeFolderName(studentDisplayName));
    }

    public static void PromoteLegacyModuleFolder(string studentHome, string? module)
    {
        if (string.IsNullOrWhiteSpace(studentHome) || string.IsNullOrWhiteSpace(module)) return;
        var home = Path.GetFullPath(studentHome);
        Directory.CreateDirectory(home);
        var legacy = Path.GetFullPath(Path.Combine(home, SanitizeFolderName(module)));
        var homePrefix = home.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(legacy) || !legacy.StartsWith(homePrefix, StringComparison.OrdinalIgnoreCase)) return;

        foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(legacy, file);
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains("..", StringComparison.Ordinal)) continue;
            var dest = Path.GetFullPath(Path.Combine(home, relative));
            if (!dest.StartsWith(homePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (!File.Exists(dest))
                File.Move(file, dest);
        }
        TryDeleteEmptyDirectories(legacy);
    }

    public async Task<SyncPrepareResult> PrepareAsync(SyncPrepareRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId)) throw new LessonValidationException(["client_id обязателен."]);
        if (request.StudentId is Guid studentId)
            BindClient(request.ClientId, studentId);
        foreach (var change in request.Changes) ValidateRelativePath(change.Path);
        if (request.LocalFiles is not null)
            foreach (var file in request.LocalFiles) ValidateRelativePath(file.Path);

        StoredPlan plan;
        var reasons = new List<string>();
        // Create/edit sync without tutor approval. Only deletions need confirmation.
        if (request.LocalFiles is { Count: >= 0 } local)
        {
            plan = await BuildPlanAsync(request.ClientId, local, ct);
            var deleted = request.Changes
                .Where(x => x.Kind == SyncChangeKind.Deleted)
                .Select(x => Normalize(x.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Missing local files that the student intentionally deleted are not downloads.
            plan = new StoredPlan(
                plan.Upload,
                plan.Download.Where(path => !deleted.Contains(path, StringComparer.OrdinalIgnoreCase)).ToList(),
                plan.Conflicts,
                deleted.Select(path => new SyncChange(path, SyncChangeKind.Deleted, 0)).ToList());
            if (deleted.Count > 0)
                reasons.Add(deleted.Count == 1
                    ? $"удаление файла: {deleted[0]}"
                    : $"удаление {deleted.Count} файлов");
            if (request.AcceptedWasNonempty && request.ResultingIsEmpty)
                reasons.Add("рабочая папка стала пустой");
        }
        else
        {
            foreach (var change in request.Changes) ValidateRelativePath(change.Path);
            var uploads = request.Changes.Where(x => x.Kind != SyncChangeKind.Deleted).Select(x => Normalize(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var deletes = request.Changes.Where(x => x.Kind == SyncChangeKind.Deleted).Select(x => Normalize(x.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plan = new StoredPlan(uploads, [], [], deletes.Select(path => new SyncChange(path, SyncChangeKind.Deleted, 0)).ToList());
            if (deletes.Count > 0)
                reasons.Add(deletes.Count == 1
                    ? $"удаление файла: {deletes[0]}"
                    : $"удаление {deletes.Count} файлов");
            if (request.AcceptedWasNonempty && request.ResultingIsEmpty)
                reasons.Add("рабочая папка стала пустой");
        }

        plan = await ApplyRestoreIntentsAsync(request.ClientId, plan, ct);
        if (plan.RestoreFromServer)
            reasons.RemoveAll(x => x.StartsWith("удаление ", StringComparison.Ordinal) || x == "рабочая папка стала пустой");
        if (!AutoApproveSafeFiles && plan.Upload.Count + plan.Conflicts.Count > 0)
            reasons.Add($"создание или изменение {plan.Upload.Count + plan.Conflicts.Count} файлов");

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
        var deletes = (plan.Changes ?? [])
            .Where(x => x.Kind == SyncChangeKind.Deleted)
            .Select(x => Normalize(x.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!takeStudent && deletes.Count > 0)
        {
            // Keep tutor copies: push deleted files back to the student.
            plan = plan with
            {
                Download = plan.Download.Concat(deletes).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Changes = []
            };
        }
        approval.ChangesJson = JsonSerializer.Serialize(plan, PlanJson);
        approval.Status = takeStudent ? SyncApprovalStatus.Approved : SyncApprovalStatus.Restore;
        approval.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (takeStudent)
        {
            foreach (var path in deletes)
                await DeleteAsync(approval.ClientId, path, ct);
        }
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
        var plan = ReadPlan(latest.ChangesJson);
        if (plan.Upload.Count > 0 || plan.Changes.Count > 0)
            await CaptureProjectSnapshotAsync(clientId, "Автосохранение", ct);
        var rootPath = GetStorageRoot(clientId);
        var intents = await db.SyncRestoreIntents.Where(x => x.ClientId == clientId && x.RootPath == rootPath).ToListAsync(ct);
        db.SyncRestoreIntents.RemoveRange(intents);
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
        await SetRestoreIntentsAsync(request.ClientId, [(Normalize(request.Path), false)], ct);
        return ToFileInfo(request.Path, destination, version.Sha256);
    }

    public async Task<IReadOnlyList<ProjectSnapshotInfo>> ListProjectSnapshotsAsync(string clientId, CancellationToken ct = default)
    {
        var rootPath = GetStorageRoot(clientId);
        await using var db = new ClassroomDbContext(options);
        var snapshots = await db.SyncedProjectSnapshots.AsNoTracking()
            .Where(x => x.ClientId == clientId && x.RootPath == rootPath)
            .ToListAsync(ct);
        return snapshots.OrderByDescending(x => x.CreatedAt)
            .Select(x => new ProjectSnapshotInfo(x.Id.ToString("N"), x.CreatedAt, x.Label, x.FileCount, x.TotalBytes)).ToList();
    }

    public async Task<ProjectSnapshotInfo?> CaptureProjectSnapshotAsync(string clientId, string label = "Автосохранение", CancellationToken ct = default)
    {
        var rootPath = GetStorageRoot(clientId);
        var files = await ListFilesAsync(clientId, ct);
        var entries = files.Select(x => new ProjectEntry(x.Path, x.Sha256, x.Size)).ToList();
        var manifest = JsonSerializer.Serialize(entries, PlanJson);
        await using var db = new ClassroomDbContext(options);
        var existingSnapshots = await db.SyncedProjectSnapshots.AsNoTracking()
            .Where(x => x.ClientId == clientId && x.RootPath == rootPath)
            .ToListAsync(ct);
        var latest = existingSnapshots.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (latest?.ManifestJson == manifest) return null;

        var objects = Path.Combine(rootPath, ".history", "objects");
        Directory.CreateDirectory(objects);
        foreach (var entry in entries)
        {
            var destination = Path.Combine(objects, entry.Sha256 + ".bin");
            if (!File.Exists(destination)) File.Copy(ResolvePath(clientId, entry.Path), destination);
        }
        var snapshot = new SyncedProjectSnapshot
        {
            ClientId = clientId, RootPath = rootPath, ManifestJson = manifest,
            Label = label, FileCount = entries.Count, TotalBytes = entries.Sum(x => x.Size)
        };
        db.SyncedProjectSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        var stale = (await db.SyncedProjectSnapshots.Where(x => x.ClientId == clientId && x.RootPath == rootPath)
            .ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).Skip(30).ToList();
        db.SyncedProjectSnapshots.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return new ProjectSnapshotInfo(snapshot.Id.ToString("N"), snapshot.CreatedAt, snapshot.Label, snapshot.FileCount, snapshot.TotalBytes);
    }

    public async Task<ProjectSnapshotInfo?> RestoreProjectSnapshotAsync(RestoreProjectSnapshotRequest request, CancellationToken ct = default)
    {
        if (!Guid.TryParse(request.SnapshotId, out var id)) throw new LessonValidationException(["Некорректный ID контрольной точки."]);
        var rootPath = GetStorageRoot(request.ClientId);
        await using var db = new ClassroomDbContext(options);
        var snapshot = await db.SyncedProjectSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.ClientId == request.ClientId && x.RootPath == rootPath, ct)
            ?? throw new KeyNotFoundException("Контрольная точка не найдена.");
        var entries = JsonSerializer.Deserialize<List<ProjectEntry>>(snapshot.ManifestJson, PlanJson)
            ?? throw new InvalidDataException("Пустой манифест контрольной точки.");
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            ValidateRelativePath(entry.Path);
            if (!desired.Add(entry.Path) || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Некорректный манифест контрольной точки.");
            if (!File.Exists(Path.Combine(rootPath, ".history", "objects", entry.Sha256 + ".bin")))
                throw new FileNotFoundException("Объект контрольной точки отсутствует.", entry.Path);
        }
        var current = await ListFilesAsync(request.ClientId, ct);
        await CaptureProjectSnapshotAsync(request.ClientId, "Перед восстановлением", ct);
        foreach (var entry in entries)
        {
            var destination = ResolvePath(request.ClientId, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(rootPath, ".history", "objects", entry.Sha256 + ".bin"), destination, true);
        }
        var removed = current.Where(x => !desired.Contains(x.Path)).Select(x => x.Path).ToList();
        foreach (var path in removed) File.Delete(ResolvePath(request.ClientId, path));
        await SetRestoreIntentsAsync(request.ClientId,
            entries.Select(x => (x.Path, false)).Concat(removed.Select(x => (x, true))), ct);
        return await CaptureProjectSnapshotAsync(request.ClientId, $"Восстановлено: {snapshot.Label}", ct);
    }

    private async Task<StoredPlan> ApplyRestoreIntentsAsync(string clientId, StoredPlan plan, CancellationToken ct)
    {
        var rootPath = GetStorageRoot(clientId);
        await using var db = new ClassroomDbContext(options);
        var intents = await db.SyncRestoreIntents.AsNoTracking()
            .Where(x => x.ClientId == clientId && x.RootPath == rootPath).ToListAsync(ct);
        if (intents.Count == 0) return plan;
        var paths = intents.Select(x => x.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan with
        {
            Upload = plan.Upload.Where(x => !paths.Contains(x)).ToList(),
            Download = plan.Download.Where(x => !paths.Contains(x))
                .Concat(intents.Where(x => !x.DeleteLocal).Select(x => x.RelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Conflicts = plan.Conflicts.Where(x => !paths.Contains(x)).ToList(),
            DeleteLocal = intents.Where(x => x.DeleteLocal).Select(x => x.RelativePath).ToList(),
            RestoreFromServer = true
        };
    }

    private async Task SetRestoreIntentsAsync(string clientId, IEnumerable<(string Path, bool DeleteLocal)> changes, CancellationToken ct)
    {
        var rootPath = GetStorageRoot(clientId);
        await using var db = new ClassroomDbContext(options);
        var existing = await db.SyncRestoreIntents.Where(x => x.ClientId == clientId && x.RootPath == rootPath).ToListAsync(ct);
        foreach (var (path, deleteLocal) in changes)
        {
            var normalized = Normalize(path);
            var intent = existing.FirstOrDefault(x => string.Equals(x.RelativePath, normalized, StringComparison.OrdinalIgnoreCase));
            if (intent is not null)
                intent.DeleteLocal = deleteLocal;
            else db.SyncRestoreIntents.Add(new SyncRestoreIntent
            {
                ClientId = clientId, RootPath = rootPath, RelativePath = normalized, DeleteLocal = deleteLocal
            });
        }
        await db.SaveChangesAsync(ct);
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
        var folders = allowed
            .Concat(string.IsNullOrWhiteSpace(student.Group?.Module) ? [] : [student.Group!.Module])
            .Concat(string.IsNullOrWhiteSpace(module) ? [] : [module])
            .Select(SanitizeFolderName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new StudentSyncTarget(student.LastName, student.FirstName, student.Group?.Name ?? "группа", module, folders);
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
                return new StoredPlan(plan.Upload ?? [], plan.Download ?? [], plan.Conflicts ?? [], plan.Changes ?? [], plan.DeleteLocal ?? [], plan.RestoreFromServer);
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
        new(approval.Id, required, approval.Status, approval.Reason, approval.CreatedAt, plan.Upload, plan.Download, plan.DeleteLocal, plan.RestoreFromServer);

    private sealed record StoredPlan(List<string> Upload, List<string> Download, List<string> Conflicts, List<SyncChange> Changes,
        List<string>? DeleteLocal = null, bool RestoreFromServer = false);
    private sealed record ProjectEntry(string Path, string Sha256, long Size);
    private sealed record StudentSyncTarget(string LastName, string FirstName, string GroupName, string Module, IReadOnlyList<string> ModuleFolders);

    private static void TryDeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var child in Directory.EnumerateDirectories(root))
            TryDeleteEmptyDirectories(child);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch
        {
        }
    }
}
