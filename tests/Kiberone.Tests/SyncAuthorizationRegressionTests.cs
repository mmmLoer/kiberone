using System.Diagnostics;
using System.Text;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Tests;

public sealed class SyncAuthorizationRegressionTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kiberone-sync-auth-" + Guid.NewGuid().ToString("N"));
    private DbContextOptions<ClassroomDbContext> options = null!;
    private FileSyncService service = null!;
    private readonly List<string> links = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        options = ClassroomDatabase.CreateOptions(Path.Combine(root, "test.db"));
        await ClassroomDatabase.InitializeAsync(options);
        service = new FileSyncService(options, Path.Combine(root, "files"));
    }

    [Fact]
    public async Task EmptyPlanCannotAuthorizeUploadsOrDeletes()
    {
        var folder = service.GetClientFolderPath("pc");
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "keep");
        await Prepare("pc");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload("pc", "new.txt", "new"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("pc", "keep.txt"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "new.txt")));
    }

    [Fact]
    public async Task UploadPermissionIsSpecificToPathAndOperation()
    {
        await Prepare("pc", new SyncChange("src/main.txt", SyncChangeKind.Created, 1));
        await Upload("pc", "src\\main.txt", "allowed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload("pc", "src/other.txt", "denied"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("pc", "src/main.txt"));
        await service.CompleteAsync("pc");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload("pc", "src/main.txt", "late"));
    }

    [Fact]
    public async Task OnlyExplicitlyApprovedDeletionIsAllowed()
    {
        var folder = service.GetClientFolderPath("pc");
        File.WriteAllText(Path.Combine(folder, "delete.txt"), "delete");
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "keep");
        var plan = await Prepare("pc", new SyncChange("delete.txt", SyncChangeKind.Deleted, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("pc", "delete.txt"));
        await service.DecideAsync(plan.Id, "update");
        Assert.False(File.Exists(Path.Combine(folder, "delete.txt")));
        await service.DeleteAsync("pc", "delete.txt"); // Retries of an approved deletion remain idempotent.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("pc", "keep.txt"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload("pc", "delete.txt", "replacement"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "keep.txt")));
    }

    [Fact]
    public async Task RestoreDecisionRejectsAllStudentWrites()
    {
        service.AutoApproveSafeFiles = false;
        var folder = service.GetClientFolderPath("pc");
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "keep");
        var plan = await Prepare("pc", new SyncChange("new.txt", SyncChangeKind.Created, 1),
            new SyncChange("keep.txt", SyncChangeKind.Deleted, 0));
        var restored = await service.DecideAsync(plan.Id, "restore");
        Assert.Empty(restored!.UploadPaths!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload("pc", "new.txt", "new"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("pc", "keep.txt"));
        await service.CompleteAsync("pc");
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "keep.txt")));
    }

    [Fact]
    public async Task CompletionDoesNotFallBackToOlderActivePlan()
    {
        await Prepare("pc");
        var pending = await Prepare("pc", new SyncChange("keep.txt", SyncChangeKind.Deleted, 0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync("pc"));
        Assert.Equal(SyncApprovalStatus.Pending, (await service.GetApprovalAsync("pc"))!.Status);
        await service.DecideAsync(pending.Id, "restore");
        await service.CompleteAsync("pc");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync("pc"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreInvalidatesEveryClientSharingProjectAndPreservesRestoreIntents(bool snapshotRestore)
    {
        var classroom = new ClassroomService(options);
        var group = await classroom.CreateGroupAsync(new GroupDraft("Group", "Python", ""));
        var student = await classroom.CreateStudentAsync(new StudentDraft("Last", "First", 12, group.Id, "", "", ""));
        service.BindClient("pc-a", student.Id);
        service.BindClient("pc-b", student.Id);
        await Prepare("pc-a", new SyncChange("main.txt", SyncChangeKind.Created, 5));
        await Upload("pc-a", "main.txt", "first");
        await service.CompleteAsync("pc-a");
        var snapshot = Assert.Single(await service.ListProjectSnapshotsAsync("pc-a"));
        var version = Assert.Single(await service.ListVersionsAsync("pc-a", "main.txt"));
        await Prepare("pc-a", new SyncChange("main.txt", SyncChangeKind.Modified, 6));
        await Upload("pc-a", "main.txt", "second");
        await Prepare("pc-b", new SyncChange("main.txt", SyncChangeKind.Modified, 6));

        if (snapshotRestore)
            await service.RestoreProjectSnapshotAsync(new RestoreProjectSnapshotRequest("pc-a", snapshot.Id));
        else
            await service.RestoreVersionAsync(new RestoreVersionRequest("pc-a", "main.txt", version.Id));

        foreach (var client in new[] { "pc-a", "pc-b" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Upload(client, "main.txt", "late"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync(client));
            var next = await Prepare(client, new SyncChange("main.txt", SyncChangeKind.Deleted, 0));
            Assert.False(next.Required);
            Assert.True(next.RestoreFromServer);
            Assert.Contains("main.txt", next.DownloadPaths!);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(client, "main.txt"));
            await service.CompleteAsync(client);
        }
        Assert.Equal("first", File.ReadAllText(Path.Combine(service.GetClientFolderPath("pc-a"), "main.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreWaitsForInFlightUploadThenRejectsStaleWrites(bool snapshotRestore)
    {
        await Prepare("pc", new SyncChange("main.txt", SyncChangeKind.Created, 5));
        await Upload("pc", "main.txt", "first");
        await service.CompleteAsync("pc");
        var version = Assert.Single(await service.ListVersionsAsync("pc", "main.txt"));
        var snapshot = Assert.Single(await service.ListProjectSnapshotsAsync("pc"));
        await Prepare("pc", new SyncChange("main.txt", SyncChangeKind.Modified, 6));
        using var stream = new PausedStream("second");
        var upload = service.UploadAsync("pc", "main.txt", stream);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task restore = snapshotRestore
            ? service.RestoreProjectSnapshotAsync(new RestoreProjectSnapshotRequest("pc", snapshot.Id))
            : service.RestoreVersionAsync(new RestoreVersionRequest("pc", "main.txt", version.Id));
        try
        {
            Assert.False(restore.IsCompleted);
            Assert.Equal("first", File.ReadAllText(Path.Combine(service.GetClientFolderPath("pc"), "main.txt")));
        }
        finally { stream.Release.TrySetResult(true); }
        await Task.WhenAll(upload, restore).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload("pc", "main.txt", "late"));
        Assert.Equal("first", File.ReadAllText(Path.Combine(service.GetClientFolderPath("pc"), "main.txt")));
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("root")]
    [InlineData("ancestor")]
    public async Task DirectoryLinksCannotEscapeThroughSyncOperations(string placement)
    {
        var folder = service.GetClientFolderPath("pc");
        await Prepare("pc", new SyncChange("linked/secret.txt", SyncChangeKind.Created, 6),
            new SyncChange("secret.txt", SyncChangeKind.Created, 6));
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        string link;
        string relative;
        if (placement == "nested")
        {
            link = Path.Combine(folder, "linked");
            relative = "linked/secret.txt";
        }
        else if (placement == "root")
        {
            Directory.Delete(folder);
            link = folder;
            relative = "secret.txt";
        }
        else
        {
            Directory.Delete(folder);
            link = Path.GetDirectoryName(folder)!;
            Directory.Delete(link);
            var outsideClient = Path.Combine(outside, Path.GetFileName(folder));
            Directory.CreateDirectory(outsideClient);
            File.WriteAllText(Path.Combine(outsideClient, "secret.txt"), "secret");
            relative = "secret.txt";
        }
        CreateDirectoryLink(link, outside);
        await Assert.ThrowsAsync<LessonValidationException>(() => service.OpenDownloadAsync("pc", relative));
        await Assert.ThrowsAsync<LessonValidationException>(() => service.ListFilesAsync("pc"));
        await Assert.ThrowsAsync<LessonValidationException>(() => Upload("pc", relative, "overwrite"));
        var deletion = await Prepare("pc", new SyncChange(relative, SyncChangeKind.Deleted, 0));
        await Assert.ThrowsAsync<LessonValidationException>(() => service.DecideAsync(deletion.Id, "update"));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(outside, "secret.txt")));
        if (placement == "ancestor")
            Assert.Equal("secret", File.ReadAllText(Path.Combine(outside, Path.GetFileName(folder), "secret.txt")));
    }

    [Fact]
    public async Task RestoreIntentDoesNotAutoApproveDeletionOfUnrelatedFile()
    {
        await Prepare("pc", new SyncChange("restored.txt", SyncChangeKind.Created, 5),
            new SyncChange("keep.txt", SyncChangeKind.Created, 4));
        await Upload("pc", "restored.txt", "first");
        await Upload("pc", "keep.txt", "keep");
        await service.CompleteAsync("pc");
        var version = Assert.Single(await service.ListVersionsAsync("pc", "restored.txt"));
        await service.RestoreVersionAsync(new RestoreVersionRequest("pc", "restored.txt", version.Id));
        var next = await Prepare("pc", new SyncChange("restored.txt", SyncChangeKind.Deleted, 0),
            new SyncChange("keep.txt", SyncChangeKind.Deleted, 0));
        Assert.True(next.Required);
        Assert.Equal(SyncApprovalStatus.Pending, next.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync("pc", "keep.txt"));
        await service.DecideAsync(next.Id, "update");
        var folder = service.GetClientFolderPath("pc");
        Assert.Equal("first", File.ReadAllText(Path.Combine(folder, "restored.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "keep.txt")));
    }

    [Fact]
    public async Task LinkedHistoryDirectoryCannotBeUsedForArchivesOrSnapshots()
    {
        var folder = service.GetClientFolderPath("pc");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "outside");
        CreateDirectoryLink(Path.Combine(folder, ".history"), outside);
        File.WriteAllText(Path.Combine(folder, "main.txt"), "original");
        await Prepare("pc", new SyncChange("main.txt", SyncChangeKind.Modified, 3));
        await Assert.ThrowsAsync<LessonValidationException>(() => Upload("pc", "main.txt", "new"));
        await Assert.ThrowsAsync<LessonValidationException>(() => service.CaptureProjectSnapshotAsync("pc"));
        Assert.Equal("original", File.ReadAllText(Path.Combine(folder, "main.txt")));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreRejectsLinkedDestinationBeforeChangingOutsideFiles(bool snapshotRestore)
    {
        await Prepare("pc", new SyncChange("nested/main.txt", SyncChangeKind.Created, 5));
        await Upload("pc", "nested/main.txt", "first");
        await service.CompleteAsync("pc");
        var version = Assert.Single(await service.ListVersionsAsync("pc", "nested/main.txt"));
        var snapshot = Assert.Single(await service.ListProjectSnapshotsAsync("pc"));
        var nested = Path.Combine(service.GetClientFolderPath("pc"), "nested");
        Directory.Delete(nested, true);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "main.txt"), "outside");
        CreateDirectoryLink(nested, outside);
        if (snapshotRestore)
            await Assert.ThrowsAsync<LessonValidationException>(() => service.RestoreProjectSnapshotAsync(new RestoreProjectSnapshotRequest("pc", snapshot.Id)));
        else
            await Assert.ThrowsAsync<LessonValidationException>(() => service.RestoreVersionAsync(new RestoreVersionRequest("pc", "nested/main.txt", version.Id)));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "main.txt")));
    }

    [Theory]
    [InlineData("file.txt:stream")]
    [InlineData(".history /secret.txt")]
    [InlineData(".history./secret.txt")]
    public async Task WindowsPathAliasesCannotBypassPlanOrHistoryExclusions(string path)
    {
        await Assert.ThrowsAsync<LessonValidationException>(() => Prepare("pc", new SyncChange(path, SyncChangeKind.Created, 1)));
    }

    private Task<SyncPrepareResult> Prepare(string client, params SyncChange[] changes) =>
        service.PrepareAsync(new SyncPrepareRequest(client, changes, false, false, 5));

    private async Task<SyncedFileInfo> Upload(string client, string path, string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await service.UploadAsync(client, path, stream);
    }

    private void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start)!;
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        links.Add(link);
    }

    public Task DisposeAsync()
    {
        foreach (var link in links.AsEnumerable().Reverse()) Directory.Delete(link);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            // libgit2 marks loose objects read-only on Windows. Remove links first so
            // cleanup only changes attributes inside this fixture's temporary tree.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
        return Task.CompletedTask;
    }

    private sealed class PausedStream(string text) : MemoryStream(Encoding.UTF8.GetBytes(text))
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
