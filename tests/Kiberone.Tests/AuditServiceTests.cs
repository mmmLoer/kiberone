using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class AuditServiceTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kiberone-audit-{Guid.NewGuid():N}.db");
    private AuditService service = null!;

    public async Task InitializeAsync()
    {
        var options = ClassroomDatabase.CreateOptions(databasePath);
        await ClassroomDatabase.InitializeAsync(options);
        service = new AuditService(options);
    }

    [Fact]
    public async Task Audit_CanBeFilteredWithoutStoringSensitiveBodies()
    {
        await service.WriteAsync("Команды", "POST", "tutor", "/command", "", 200, 12);
        await service.WriteAsync("Синхронизация", "POST", "pc-01", "/upload", "", 200, 45);

        var commands = await service.ListAsync(new("Команды", null));
        var searched = await service.ListAsync(new(null, "pc-01"));

        Assert.Single(commands);
        Assert.Equal("/command", commands[0].Target);
        Assert.Single(searched);
        Assert.Equal("Синхронизация", searched[0].Category);
    }

    public async Task DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" }) if (File.Exists(path)) File.Delete(path);
    }
}
