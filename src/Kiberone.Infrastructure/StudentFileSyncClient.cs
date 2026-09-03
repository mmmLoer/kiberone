using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed record StudentSyncState(string Status, int PendingChanges, DateTimeOffset ChangedAt);

public sealed class StudentFileSyncClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".venv", "__pycache__", ".git", "node_modules", "$RECYCLE.BIN", ".history" };
    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
        { "Thumbs.db", "desktop.ini", ".DS_Store" };
    private readonly string clientId;
    private readonly string stateDirectory;
    private string watchFolder;
    private string cachePath;
    private Dictionary<string, CachedFile> accepted = new(StringComparer.OrdinalIgnoreCase);
    private PendingBatch? pending;

    public StudentFileSyncClient(string clientId, string watchFolder)
    {
        this.clientId = clientId;
        this.watchFolder = Path.GetFullPath(watchFolder);
        Directory.CreateDirectory(this.watchFolder);
        stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom");
        Directory.CreateDirectory(stateDirectory);
        cachePath = CachePathFor(this.watchFolder);
        accepted = LoadCache();
    }

    public Guid? StudentId { get; set; }
    public string WatchFolder => watchFolder;
    public event Action<StudentSyncState>? StateChanged;

    public void SetWorkspace(string folder)
    {
        var next = Path.GetFullPath(folder);
        if (string.Equals(watchFolder, next, StringComparison.OrdinalIgnoreCase)) return;
        watchFolder = next;
        Directory.CreateDirectory(watchFolder);
        cachePath = CachePathFor(watchFolder);
        accepted = LoadCache();
        pending = null;
    }

    public async Task SyncOnceAsync(HttpClient http, CancellationToken ct = default)
    {
        if (StudentId is null)
        {
            Raise("Войдите в класс, чтобы синхронизировать сохранения", 0);
            return;
        }

        if (pending is null)
        {
            var local = await ScanHashedAsync(ct);
            var fingerprints = local.Select(x => new SyncFileFingerprint(x.Key, x.Value.Size, x.Value.Sha256)).ToList();
            var changes = BuildChanges(accepted, local);
            using var response = await http.PostAsJsonAsync("/sync/prepare", new SyncPrepareRequest(
                clientId, changes, accepted.Count > 0, local.Count == 0, 5, StudentId, fingerprints), JsonOptions, ct);
            response.EnsureSuccessStatusCode();
            var prepared = await response.Content.ReadFromJsonAsync<SyncPrepareResult>(JsonOptions, ct)
                ?? throw new JsonException("Сервер не вернул состояние синхронизации.");
            pending = new PendingBatch(prepared.Id, local, prepared);
            var pendingCount = (prepared.UploadPaths?.Count ?? 0) + (prepared.DownloadPaths?.Count ?? 0);
            if (prepared.Status == SyncApprovalStatus.Pending)
            {
                Raise("Версии различаются — ждём решение тьютора", pendingCount);
                return;
            }
        }
        else
        {
            var approval = await http.GetFromJsonAsync<SyncPrepareResult>($"/sync/approval?client_id={Uri.EscapeDataString(clientId)}", JsonOptions, ct);
            if (approval is null || approval.Id != pending.ApprovalId || approval.Status == SyncApprovalStatus.Pending) return;
            if (approval.Status == SyncApprovalStatus.Rejected)
            {
                pending = null;
                Raise("Синхронизация отклонена тьютором", 0);
                return;
            }
            pending = pending with { Prepared = approval };
        }

        var batch = pending;
        if (batch is null) return;
        foreach (var path in batch.Prepared.UploadPaths ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var localPath = ResolveLocal(path);
            if (!File.Exists(localPath)) continue;
            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            using var content = new StreamContent(stream);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/upload") { Content = content };
            // Custom headers belong on the request — putting them on HttpContent throws
            // "Misused header name. Make sure request headers are used with HttpRequestMessage…".
            // X-Client-Id is already on DefaultRequestHeaders from StudentAgent.
            request.Headers.TryAddWithoutValidation("X-Relative-Path", path);
            using var uploadResponse = await http.SendAsync(request, ct);
            uploadResponse.EnsureSuccessStatusCode();
        }

        foreach (var path in batch.Prepared.DownloadPaths ?? [])
        {
            ct.ThrowIfCancellationRequested();
            using var download = await http.GetAsync($"/download?client_id={Uri.EscapeDataString(clientId)}&path={Uri.EscapeDataString(path)}", ct);
            if (!download.IsSuccessStatusCode) continue;
            var localPath = ResolveLocal(path);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await using var input = await download.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct);
        }

        using var completeResponse = await http.PostAsJsonAsync("/sync/complete", new SyncCompleteRequest(clientId), JsonOptions, ct);
        completeResponse.EnsureSuccessStatusCode();
        accepted = await ScanHashedAsync(ct);
        SaveCache();
        pending = null;
        Raise("Сохранения синхронизированы", 0);
    }

    private async Task<Dictionary<string, CachedFile>> ScanHashedAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(watchFolder)) return result;
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(watchFolder);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (!ExcludedDirectories.Contains(Path.GetFileName(child))) pendingDirectories.Push(child);
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (ExcludedFiles.Contains(Path.GetFileName(file))) continue;
                    var info = new FileInfo(file);
                    var relative = Path.GetRelativePath(watchFolder, file).Replace('\\', '/');
                    if (accepted.TryGetValue(relative, out var cached)
                        && cached.ModifiedTicks == info.LastWriteTimeUtc.Ticks
                        && cached.Size == info.Length
                        && !string.IsNullOrEmpty(cached.Sha256))
                    {
                        result[relative] = cached;
                        continue;
                    }
                    result[relative] = new CachedFile(info.LastWriteTimeUtc.Ticks, info.Length, await HashFileAsync(file, ct));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }

    private static List<SyncChange> BuildChanges(IReadOnlyDictionary<string, CachedFile> oldState, IReadOnlyDictionary<string, CachedFile> newState)
    {
        var changes = new List<SyncChange>();
        foreach (var (path, fingerprint) in newState)
        {
            if (!oldState.TryGetValue(path, out var old)) changes.Add(new SyncChange(path, SyncChangeKind.Created, fingerprint.Size));
            else if (old.Size != fingerprint.Size || old.ModifiedTicks != fingerprint.ModifiedTicks || !string.Equals(old.Sha256, fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
                changes.Add(new SyncChange(path, SyncChangeKind.Modified, fingerprint.Size));
        }
        foreach (var path in oldState.Keys.Where(path => !newState.ContainsKey(path)))
            changes.Add(new SyncChange(path, SyncChangeKind.Deleted, 0));
        return changes.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string ResolveLocal(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(watchFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = watchFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Некорректный локальный путь синхронизации.");
        return full;
    }

    private string CachePathFor(string folder) =>
        Path.Combine(stateDirectory, $"sync-cache-{SafeKey(clientId + ":" + folder)}.json");

    private Dictionary<string, CachedFile> LoadCache()
    {
        try
        {
            return File.Exists(cachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, CachedFile>>(File.ReadAllText(cachePath), JsonOptions) ?? new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void SaveCache()
    {
        var temporary = cachePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(accepted, JsonOptions));
        File.Move(temporary, cachePath, true);
    }

    private void Raise(string status, int changes) => StateChanged?.Invoke(new StudentSyncState(status, changes, DateTimeOffset.UtcNow));
    private static string SafeKey(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private sealed record PendingBatch(Guid ApprovalId, IReadOnlyDictionary<string, CachedFile> Snapshot, SyncPrepareResult Prepared);
    private sealed record CachedFile(long ModifiedTicks, long Size, string Sha256);
}
