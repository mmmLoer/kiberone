using System.Text;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Tests;

public sealed class FileSyncServiceTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"kiberone-sync-{Guid.NewGuid():N}");
    private DbContextOptions<ClassroomDbContext> options = null!;
    private FileSyncService service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(testRoot);
        options = ClassroomDatabase.CreateOptions(Path.Combine(testRoot, "test.db"));
        await ClassroomDatabase.InitializeAsync(options);
        service = new FileSyncService(options, Path.Combine(testRoot, "files"));
    }

    [Fact]
    public async Task DangerousFile_RequiresTutorApproval()
    {
        var prepared = await service.PrepareAsync(new SyncPrepareRequest("pc-01", [new SyncChange("build/game.exe", SyncChangeKind.Created, 4)], false, false, 5));
        Assert.True(prepared.Required);
        Assert.Equal(SyncApprovalStatus.Pending, prepared.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload("pc-01", "build/game.exe", "MZ00"));

        await service.DecideAsync(prepared.Id, true);
        var uploaded = await Upload("pc-01", "build/game.exe", "MZ00");
        await service.CompleteAsync("pc-01");

        Assert.Equal("build/game.exe", uploaded.Path);
        Assert.Equal(SyncApprovalStatus.Completed, (await service.GetApprovalAsync("pc-01"))?.Status);
    }

    [Fact]
    public async Task ModifiedFile_IsVersionedAndCanBeRestored()
    {
        await PrepareSafe("pc-02", "project/main.cs", SyncChangeKind.Created);
        await Upload("pc-02", "project/main.cs", "version one");
        await service.CompleteAsync("pc-02");
        await PrepareSafe("pc-02", "project/main.cs", SyncChangeKind.Modified);
        await Upload("pc-02", "project/main.cs", "version two");

        var versions = await service.ListVersionsAsync("pc-02", "project/main.cs");
        var first = versions.Single(x => x.Label == "Первая загрузка");
        await service.RestoreVersionAsync(new RestoreVersionRequest("pc-02", "project/main.cs", first.Id));
        await using var restored = await service.OpenDownloadAsync("pc-02", "project/main.cs");
        using var reader = new StreamReader(restored!, Encoding.UTF8);

        Assert.Equal("version one", await reader.ReadToEndAsync());
        Assert.Contains(versions, x => x.Label == "До изменения");
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData(".git/config")]
    [InlineData("node_modules/pkg.js")]
    [InlineData("C:/Windows/win.ini")]
    public async Task UnsafeOrExcludedPaths_AreRejected(string path)
    {
        await Assert.ThrowsAsync<LessonValidationException>(() => service.PrepareAsync(
            new SyncPrepareRequest("pc-03", [new SyncChange(path, SyncChangeKind.Created, 1)], false, false, 5)));
    }

    [Fact]
    public async Task MassChangesAndEmptyFolder_RequireApproval()
    {
        var changes = Enumerable.Range(0, 6).Select(i => new SyncChange($"file-{i}.txt", SyncChangeKind.Deleted, 0)).ToList();
        var prepared = await service.PrepareAsync(new SyncPrepareRequest("pc-04", changes, true, true, 5));

        Assert.True(prepared.Required);
        Assert.Contains("больше 5", prepared.Reason);
        Assert.Contains("стала пустой", prepared.Reason);
    }

    private async Task PrepareSafe(string clientId, string path, SyncChangeKind kind) =>
        await service.PrepareAsync(new SyncPrepareRequest(clientId, [new SyncChange(path, kind, 10)], false, false, 5));

    private async Task<SyncedFileInfo> Upload(string clientId, string path, string text)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await service.UploadAsync(clientId, path, stream);
    }

    public async Task DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
    }
}
