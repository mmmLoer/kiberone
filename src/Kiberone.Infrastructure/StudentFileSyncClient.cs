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
    private readonly HashSet<string> ignoredModuleFolders = new(StringComparer.OrdinalIgnoreCase);
    private string? activeModule;
    private bool replaceFromServer;
    private string? lastSyncedModule;

    public StudentFileSyncClient(string clientId, string watchFolder)
    {
        this.clientId = clientId;
        this.watchFolder = Path.GetFullPath(watchFolder);
        Directory.CreateDirectory(this.watchFolder);
        stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom");
        Directory.CreateDirectory(stateDirectory);
        cachePath = CachePathFor(this.watchFolder);
        accepted = LoadCache();
        lastSyncedModule = LoadLastSyncedModule();
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
        lastSyncedModule = LoadLastSyncedModule();
        pending = null;
    }

    public void SetIgnoredFolders(IReadOnlyList<string>? names)
    {
        if (names is null) return;
        ignoredModuleFolders.Clear();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            ignoredModuleFolders.Add(FileSyncService.SanitizeFolderName(name));
        }
        if (!string.IsNullOrWhiteSpace(activeModule))
            ignoredModuleFolders.Add(FileSyncService.SanitizeFolderName(activeModule));
    }

    public bool SetActiveModule(string? module)
    {
        var next = string.IsNullOrWhiteSpace(module) ? null : module.Trim();
        if (string.Equals(activeModule, next, StringComparison.OrdinalIgnoreCase)) return false;
        var previous = activeModule ?? lastSyncedModule;
        var switched = previous is not null;
        activeModule = next;
        pending = null;
        if (!string.IsNullOrWhiteSpace(next))
            ignoredModuleFolders.Add(FileSyncService.SanitizeFolderName(next));
        if (switched && !string.Equals(previous, next, StringComparison.OrdinalIgnoreCase))
            replaceFromServer = true;
        return true;
    }

    public void EndSession() { StudentId = null; pending = null; }
    public void RestoreForNewStudent() { pending = null; replaceFromServer = true; }
    public async Task<string?> SaveSessionCheckpointAsync(Guid owner, string? backupRoot = null, CancellationToken ct = default)
    {
        if (StudentId != owner) throw new InvalidOperationException("Ученик уже изменился.");
        var snapshot = await ScanHashedAsync(ct, forceHash: true);
        var changes = BuildChanges(accepted, snapshot);
        if (changes.Count == 0) return null;
        var root = backupRoot ?? Path.Combine(stateDirectory, "session-backups");
        var directory = Path.Combine(root, owner.ToString("N"), DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        var staging = directory + ".tmp";
        Directory.CreateDirectory(staging);
        foreach (var change in changes.Where(c => snapshot.ContainsKey(c.Path)))
        {
            var source = ResolveLocal(change.Path);
            var destination = Path.Combine(staging, "files", change.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, ct);
            if (await HashFileAsync(destination, ct) != snapshot[change.Path].Sha256)
                throw new IOException("Файл изменился во время сохранения. Повторите смену ученика.");
        }
        await File.WriteAllTextAsync(Path.Combine(staging, "session.json"), JsonSerializer.Serialize(new { StudentId = owner, Workspace = watchFolder, Module = activeModule, Changes = changes }), ct);
        Directory.Move(staging, directory);
        return directory;
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
            var replacing = replaceFromServer;
            var local = replacing
                ? new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase)
                : await ScanHashedAsync(ct);
            var fingerprints = local.Select(x => new SyncFileFingerprint(x.Key, x.Value.Size, x.Value.Sha256)).ToList();
            var changes = replacing ? [] : BuildChanges(accepted, local);
            using var response = await http.PostAsJsonAsync("/sync/prepare", new SyncPrepareRequest(
                clientId, changes, !replacing && accepted.Count > 0, local.Count == 0, 5, StudentId, fingerprints), JsonOptions, ct);
            response.EnsureSuccessStatusCode();
            var prepared = await response.Content.ReadFromJsonAsync<SyncPrepareResult>(JsonOptions, ct)
                ?? throw new JsonException("Сервер не вернул состояние синхронизации.");
            pending = new PendingBatch(prepared.Id, local, prepared);
            var pendingCount = (prepared.UploadPaths?.Count ?? 0) + (prepared.DownloadPaths?.Count ?? 0);
            if (prepared.Status == SyncApprovalStatus.Pending)
            {
                Raise("Сохранения ждут решения тьютора", pendingCount);
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
        if (replaceFromServer)
            await BackupLocalFilesAsync((await ScanHashedAsync(ct)).Keys, ct);
        else if (batch.Prepared.RestoreFromServer)
            await BackupLocalFilesAsync((batch.Prepared.DownloadPaths ?? []).Concat(batch.Prepared.DeleteLocalPaths ?? []), ct);
        foreach (var path in batch.Prepared.UploadPaths ?? [])
        {
            ct.ThrowIfCancellationRequested();
            var localPath = ResolveLocal(path);
            if (!File.Exists(localPath)) continue;
            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            using var content = new StreamContent(stream);
            // Paths often contain Cyrillic; raw values in HTTP headers throw
            // "Request headers must contain only ASCII characters". Prefer query (like /download)
            // and keep a percent-encoded header for older tutors.
            var escaped = Uri.EscapeDataString(path);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/upload?path={escaped}") { Content = content };
            request.Headers.TryAddWithoutValidation("X-Relative-Path", escaped);
            using var uploadResponse = await http.SendAsync(request, ct);
            uploadResponse.EnsureSuccessStatusCode();
        }

        foreach (var path in batch.Prepared.DownloadPaths ?? [])
        {
            ct.ThrowIfCancellationRequested();
            using var download = await http.GetAsync($"/download?client_id={Uri.EscapeDataString(clientId)}&path={Uri.EscapeDataString(path)}", ct);
            download.EnsureSuccessStatusCode();
            var localPath = ResolveLocal(path);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await using var input = await download.Content.ReadAsStreamAsync(ct);
            var temporary = localPath + ".sync-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, ct);
                File.Move(temporary, localPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        foreach (var path in batch.Prepared.DeleteLocalPaths ?? [])
        {
            var localPath = ResolveLocal(path);
            if (File.Exists(localPath)) File.Delete(localPath);
        }

        using var completeResponse = await http.PostAsJsonAsync("/sync/complete", new SyncCompleteRequest(clientId), JsonOptions, ct);
        completeResponse.EnsureSuccessStatusCode();
        if (replaceFromServer)
        {
            PruneLocalFilesNotIn(batch.Prepared.DownloadPaths ?? []);
            replaceFromServer = false;
        }
        accepted = await ScanHashedAsync(ct);
        SaveCache();
        if (activeModule is not null) SaveLastSyncedModule(activeModule);
        pending = null;
        Raise("Сохранения синхронизированы", 0);
    }

    private async Task<Dictionary<string, CachedFile>> ScanHashedAsync(CancellationToken ct, bool forceHash = false)
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
                {
                    var name = Path.GetFileName(child);
                    if (ExcludedDirectories.Contains(name)) continue;
                    if (IsIgnoredModuleFolder(directory, name)) continue;
                    pendingDirectories.Push(child);
                }
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (ExcludedFiles.Contains(Path.GetFileName(file))) continue;
                    var info = new FileInfo(file);
                    var relative = Path.GetRelativePath(watchFolder, file).Replace('\\', '/');
                    if (!forceHash && accepted.TryGetValue(relative, out var cached)
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
            catch (UnauthorizedAccessException) when (!forceHash) { }
            catch (DirectoryNotFoundException) when (!forceHash) { }
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

    private void PruneLocalFilesNotIn(IReadOnlyList<string> keep)
    {
        var keepSet = new HashSet<string>(keep.Select(path => path.Replace('\\', '/').Trim('/')), StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(watchFolder)) return;
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(watchFolder);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(child);
                    if (ExcludedDirectories.Contains(name) || IsIgnoredModuleFolder(directory, name)) continue;
                    pendingDirectories.Push(child);
                }
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if (ExcludedFiles.Contains(Path.GetFileName(file))) continue;
                    var relative = Path.GetRelativePath(watchFolder, file).Replace('\\', '/');
                    if (keepSet.Contains(relative)) continue;
                    try { File.Delete(file); }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private bool IsIgnoredModuleFolder(string parentDirectory, string name) =>
        string.Equals(Path.GetFullPath(parentDirectory), watchFolder, StringComparison.OrdinalIgnoreCase)
        && ignoredModuleFolders.Contains(name);

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

    private string ModulePath => cachePath + ".module";
    private string? LoadLastSyncedModule()
    {
        try { return File.Exists(ModulePath) ? File.ReadAllText(ModulePath) : null; }
        catch { return null; }
    }
    private void SaveLastSyncedModule(string module)
    {
        File.WriteAllText(ModulePath + ".tmp", module);
        File.Move(ModulePath + ".tmp", ModulePath, true);
        lastSyncedModule = module;
    }

    private async Task BackupLocalFilesAsync(IEnumerable<string> paths, CancellationToken ct)
    {
        var existing = paths.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path: path, Source: ResolveLocal(path)))
            .Where(x => File.Exists(x.Source)).ToList();
        if (existing.Count == 0) return;
        var backupRoot = Path.Combine(stateDirectory, "sync-backups", SafeKey(clientId + ":" + watchFolder), DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
        foreach (var (path, source) in existing)
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(Path.Combine(backupRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = backupRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Некорректный путь резервной копии.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, ct);
        }
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
