namespace Kiberone.Core;

public enum SyncChangeKind { Created, Modified, Deleted }
public enum SyncApprovalStatus { NotRequired, Pending, Approved, Rejected, Completed }

public sealed record SyncChange(string Path, SyncChangeKind Kind, long Size);
public sealed record SyncPrepareRequest(string ClientId, IReadOnlyList<SyncChange> Changes, bool AcceptedWasNonempty, bool ResultingIsEmpty, int? Threshold);
public sealed record SyncPrepareResult(Guid Id, bool Required, SyncApprovalStatus Status, string Reason, DateTimeOffset CreatedAt);
public sealed record SyncDecisionRequest(bool Approved);
public sealed record SyncCompleteRequest(string ClientId);
public sealed record DeleteFileRequest(string ClientId, string Path);
public sealed record RestoreVersionRequest(string ClientId, string Path, string VersionId);
public sealed record SyncedFileInfo(string Path, long Size, DateTimeOffset ModifiedAt, string Sha256);
public sealed record FileVersionInfo(string Id, string Path, long Size, string Sha256, DateTimeOffset CreatedAt, string Label);

public sealed class SyncApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ClientId { get; set; }
    public string ChangesJson { get; set; } = "[]";
    public string Reason { get; set; } = string.Empty;
    public SyncApprovalStatus Status { get; set; } = SyncApprovalStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class SyncedFileVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ClientId { get; set; }
    public required string RelativePath { get; set; }
    public required string StoragePath { get; set; }
    public required string Sha256 { get; set; }
    public long Size { get; set; }
    public string Label { get; set; } = "Изменение";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
