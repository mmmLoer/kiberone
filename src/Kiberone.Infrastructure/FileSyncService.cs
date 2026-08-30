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

    private readonly DbContextOptions<ClassroomDbContext> options;
    private readonly string root;

    public FileSyncService(DbContextOptions<ClassroomDbContext> options, string root)
    {
        this.options = options;
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
    }

    public async Task<SyncPrepareResult> PrepareAsync(SyncPrepareRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId)) throw new LessonValidationException(["client_id обязателен."]);
        foreach (var change in request.Changes) ValidateRelativePath(change.Path);
        var threshold = Math.Clamp(request.Threshold ?? 5, 1, 1000);
        var reasons = new List<string>();
        if (request.Changes.Any(x => x.Kind != SyncChangeKind.Deleted && DangerousExtensions.Contains(Path.GetExtension(x.Path))))
            reasons.Add("опасный тип файла");
        if (request.Changes.Count(x => x.Kind is SyncChangeKind.Modified or SyncChangeKind.Deleted) > threshold)
            reasons.Add($"изменено или удалено больше {threshold} файлов");
        if (request.AcceptedWasNonempty && request.ResultingIsEmpty) reasons.Add("рабочая папка стала пустой");
        var required = reasons.Count > 0;
        var approval = new SyncApproval
        {
            ClientId = request.ClientId.Trim(),
            ChangesJson = JsonSerializer.Serialize(request.Changes),
            Reason = string.Join("; ", reasons),
            Status = required ? SyncApprovalStatus.Pending : SyncApprovalStatus.NotRequired
        };
        await using var db = new ClassroomDbContext(options);
        db.SyncApprovals.Add(approval);
        await db.SaveChangesAsync(ct);
        return ToResult(approval, required);
    }

    public async Task<SyncPrepareResult?> GetApprovalAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var all = await db.SyncApprovals.AsNoTracking().Where(x => x.ClientId == clientId).ToListAsync(ct);
        var approval = all.OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        return approval is null ? null : ToResult(approval, approval.Status is not SyncApprovalStatus.NotRequired);
    }

    public async Task<IReadOnlyList<SyncApproval>> ListPendingApprovalsAsync(CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var approvals = await db.SyncApprovals.AsNoTracking().Where(x => x.Status == SyncApprovalStatus.Pending).ToListAsync(ct);
        return approvals.OrderBy(x => x.CreatedAt).ToList();
    }

    public async Task<SyncPrepareResult?> DecideAsync(Guid id, bool approved, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var approval = await db.SyncApprovals.FindAsync([id], ct);
        if (approval is null) return null;
        if (approval.Status != SyncApprovalStatus.Pending) throw new InvalidOperationException("Решение по этому запросу уже принято.");
        approval.Status = approved ? SyncApprovalStatus.Approved : SyncApprovalStatus.Rejected;
        approval.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToResult(approval, true);
    }

    public async Task CompleteAsync(string clientId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var candidates = await db.SyncApprovals.Where(x => x.ClientId == clientId &&
            (x.Status == SyncApprovalStatus.Approved || x.Status == SyncApprovalStatus.NotRequired)).ToListAsync(ct);
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
        var clientRoot = GetClientRoot(clientId);
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
        var historyRoot = Path.Combine(GetClientRoot(clientId), ".history", Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(relativePath)))).ToLowerInvariant());
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
        var clientRoot = GetClientRoot(clientId);
        var full = Path.GetFullPath(Path.Combine(clientRoot, Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        var prefix = clientRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new LessonValidationException(["Путь выходит за рабочую папку ученика."]);
        return full;
    }

    private string GetClientRoot(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new LessonValidationException(["client_id обязателен."]);
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clientId.Trim())))[..24].ToLowerInvariant();
        return Path.Combine(root, key);
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new LessonValidationException(["Требуется относительный путь."]);
        var normalized = Normalize(path);
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(x => x is "." or "..") || parts.Any(ExcludedDirectories.Contains) || ExcludedFiles.Contains(parts[^1]))
            throw new LessonValidationException(["Путь запрещён для синхронизации."]);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    private static SyncedFileInfo ToFileInfo(string relativePath, string path, string hash) =>
        new(Normalize(relativePath), new FileInfo(path).Length, File.GetLastWriteTimeUtc(path), hash);
    private static SyncPrepareResult ToResult(SyncApproval approval, bool required) =>
        new(approval.Id, required, approval.Status, approval.Reason, approval.CreatedAt);
}
