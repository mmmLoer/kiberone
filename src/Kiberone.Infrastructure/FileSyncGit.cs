using Kiberone.Core;
namespace Kiberone.Infrastructure;
public sealed partial class FileSyncService
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ProjectGates = new(StringComparer.OrdinalIgnoreCase);
    private async Task<T> WithProjectGateAsync<T>(string clientId, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = ProjectGates.GetOrAdd(GetStorageRoot(clientId), _ => new SemaphoreSlim(1,1));
        await gate.WaitAsync(ct); try { return await action(); } finally { gate.Release(); }
    }
    private async Task WithProjectGateAsync(string clientId, Func<Task> action, CancellationToken ct)
        => await WithProjectGateAsync(clientId, async () => { await action(); return true; }, ct);
    public Task<SyncedFileInfo> UploadAsync(string clientId, string relativePath, Stream content, CancellationToken ct = default)
        => WithProjectGateAsync(clientId, () => UploadCoreAsync(clientId, relativePath, content, ct), ct);
    public Task DeleteAsync(string clientId, string relativePath, CancellationToken ct = default)
        => WithProjectGateAsync(clientId, () => DeleteCoreAsync(clientId, relativePath, ct), ct);
    public Task CompleteAsync(string clientId, CancellationToken ct = default)
        => WithProjectGateAsync(clientId, () => CompleteCoreAsync(clientId, ct), ct);

    public Task<IReadOnlyList<ProjectCommitInfo>> GetGitHistoryAsync(string clientId, CancellationToken ct = default)
        => WithProjectGateAsync(clientId, () => Task.Run(() => { var store = new ProjectGitStore(GetClientFolderPath(clientId)); store.Capture("Текущие сохранения"); return store.History(); }, ct), ct);
    public Task<IReadOnlyList<string>> GetGitBranchesAsync(string clientId, CancellationToken ct = default)
        => Task.Run(() => new ProjectGitStore(GetClientFolderPath(clientId)).Branches(), ct);
    public Task<string> GetGitDiffAsync(string clientId, string sha, CancellationToken ct = default)
        => Task.Run(() => new ProjectGitStore(GetClientFolderPath(clientId)).Diff(sha), ct);
    public Task CreateGitBranchAsync(string clientId, string branch, CancellationToken ct = default)
        => WithProjectGateAsync(clientId, () => Task.Run(() => new ProjectGitStore(GetClientFolderPath(clientId)).CreateBranch(branch), ct), ct);
    public Task ChangeGitProjectAsync(string clientId, string operation, string value, CancellationToken ct = default)
        => WithProjectGateAsync(clientId, () => ChangeGitProjectCoreAsync(clientId,operation,value,ct), ct);
    private async Task ChangeGitProjectCoreAsync(string clientId, string operation, string value, CancellationToken ct)
    {
        var before = await ListFilesAsync(clientId, ct);
        await CaptureProjectSnapshotAsync(clientId, "Перед изменением Git", ct);
        await Task.Run(() => { var store = new ProjectGitStore(GetClientFolderPath(clientId)); switch (operation) { case "checkout": store.Checkout(value); break; case "restore": store.Restore(value); break; case "merge": store.Merge(value); break; default: throw new ArgumentException("Неизвестная операция."); } }, ct);
        var after = await ListFilesAsync(clientId, ct);
        var remaining = after.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await SetRestoreIntentsAsync(clientId, after.Select(f => (f.Path, false)).Concat(before.Where(f => !remaining.Contains(f.Path)).Select(f => (f.Path, true))), ct);
        await CaptureProjectSnapshotAsync(clientId, "Изменение Git: " + operation, ct);
        // An in-flight upload plan must not overwrite the branch just selected by the tutor.
        var projectRoot = GetStorageRoot(clientId);
        var related = clientStudents.Keys.Where(id => GetStorageRoot(id) == projectRoot).Append(clientId).Distinct().ToArray();
        await using var db = new ClassroomDbContext(options);
        var approvals = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.SyncApprovals.Where(a => related.Contains(a.ClientId)), ct);
        foreach (var approval in approvals.Where(a => a.Status != SyncApprovalStatus.Completed))
        { approval.Status = SyncApprovalStatus.Completed; approval.CompletedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
    }
}
