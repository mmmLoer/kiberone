using System.Net.Http.Json;
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
    private readonly string watchFolder;
    private readonly string cachePath;
    private Dictionary<string, FileFingerprint> accepted = new(StringComparer.OrdinalIgnoreCase);
    private PendingBatch? pending;

    public StudentFileSyncClient(string clientId, string watchFolder)
    {
        this.clientId = clientId;
        this.watchFolder = Path.GetFullPath(watchFolder);
        Directory.CreateDirectory(this.watchFolder);
        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom");
        Directory.CreateDirectory(stateDirectory);
        cachePath = Path.Combine(stateDirectory, $"sync-cache-{SafeKey(clientId)}.json");
        accepted = LoadCache();
    }

    public event Action<StudentSyncState>? StateChanged;

    public async Task SyncOnceAsync(HttpClient http, CancellationToken ct = default)
    {
        var current = Scan();
        if (pending is null)
        {
            var changes = BuildChanges(accepted, current);
            if (changes.Count == 0)
            {
                Raise("Актуально", 0);
                return;
            }
            var request = new SyncPrepareRequest(clientId, changes, accepted.Count > 0, current.Count == 0, 5);
            using var response = await http.PostAsJsonAsync("/sync/prepare", request, JsonOptions, ct);
            response.EnsureSuccessStatusCode();
            var prepared = await response.Content.ReadFromJsonAsync<SyncPrepareResult>(JsonOptions, ct)
                ?? throw new JsonException("Сервер не вернул состояние синхронизации.");
            pending = new PendingBatch(prepared.Id, changes, current);
            Raise(prepared.Status == SyncApprovalStatus.Pending ? "Ожидается подтверждение тьютора" : "Синхронизация", changes.Count);
            if (prepared.Status == SyncApprovalStatus.Pending) return;
        }
        else
        {
            var approval = await http.GetFromJsonAsync<SyncPrepareResult>($"/sync/approval?client_id={Uri.EscapeDataString(clientId)}", JsonOptions, ct);
            if (approval?.Id != pending.ApprovalId || approval.Status == SyncApprovalStatus.Pending) return;
            if (approval.Status == SyncApprovalStatus.Rejected)
            {
                accepted = current;
                SaveCache();
                pending = null;
                Raise("Синхронизация отклонена тьютором", 0);
                return;
            }
            if (approval.Status is not (SyncApprovalStatus.Approved or SyncApprovalStatus.NotRequired)) return;
        }

        var batch = pending;
        if (batch is null) return;
        foreach (var change in batch.Changes)
        {
            ct.ThrowIfCancellationRequested();
            if (change.Kind == SyncChangeKind.Deleted)
            {
                using var deleteResponse = await http.PostAsJsonAsync("/delete", new DeleteFileRequest(clientId, change.Path), JsonOptions, ct);
                deleteResponse.EnsureSuccessStatusCode();
                continue;
            }
            var localPath = ResolveLocal(change.Path);
            if (!File.Exists(localPath)) continue;
            await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            using var content = new StreamContent(stream);
            content.Headers.Add("X-Client-Id", clientId);
            content.Headers.Add("X-Relative-Path", change.Path);
            using var uploadResponse = await http.PostAsync("/upload", content, ct);
            uploadResponse.EnsureSuccessStatusCode();
        }
        using var completeResponse = await http.PostAsJsonAsync("/sync/complete", new SyncCompleteRequest(clientId), JsonOptions, ct);
        completeResponse.EnsureSuccessStatusCode();
        accepted = Scan();
        SaveCache();
        pending = null;
        Raise("Файлы синхронизированы", 0);
    }

    private Dictionary<string, FileFingerprint> Scan()
    {
        var result = new Dictionary<string, FileFingerprint>(StringComparer.OrdinalIgnoreCase);
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
                    result[relative] = new FileFingerprint(info.LastWriteTimeUtc.Ticks, info.Length);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }

    private static List<SyncChange> BuildChanges(IReadOnlyDictionary<string, FileFingerprint> oldState, IReadOnlyDictionary<string, FileFingerprint> newState)
    {
        var changes = new List<SyncChange>();
        foreach (var (path, fingerprint) in newState)
        {
            if (!oldState.TryGetValue(path, out var old)) changes.Add(new SyncChange(path, SyncChangeKind.Created, fingerprint.Size));
            else if (old != fingerprint) changes.Add(new SyncChange(path, SyncChangeKind.Modified, fingerprint.Size));
        }
        foreach (var path in oldState.Keys.Where(path => !newState.ContainsKey(path))) changes.Add(new SyncChange(path, SyncChangeKind.Deleted, 0));
        return changes.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private string ResolveLocal(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(watchFolder, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = watchFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Некорректный локальный путь синхронизации.");
        return full;
    }

    private Dictionary<string, FileFingerprint> LoadCache()
    {
        try
        {
            return File.Exists(cachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, FileFingerprint>>(File.ReadAllText(cachePath), JsonOptions) ?? new(StringComparer.OrdinalIgnoreCase)
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
    private static string SafeKey(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
    private sealed record PendingBatch(Guid ApprovalId, IReadOnlyList<SyncChange> Changes, IReadOnlyDictionary<string, FileFingerprint> Snapshot);
    private sealed record FileFingerprint(long ModifiedTicks, long Size);
}
