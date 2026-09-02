using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Kiberone.Tests;

public sealed class LocationRosterSyncTests
{
    [Fact]
    public async Task ExportReplace_KeepsOtherLocationsIntact()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiberone-roster-{Guid.NewGuid():N}.db");
        var options = ClassroomDatabase.CreateOptions(path);
        await ClassroomDatabase.InitializeAsync(options);
        var classroom = new ClassroomService(options);

        var home = await classroom.CreateGroupAsync(new GroupDraft("Python 01", "Python", "", "ШБ"));
        var other = await classroom.CreateGroupAsync(new GroupDraft("Дизайн 01", "Figma", "", "АРТЕША"));
        await classroom.CreateStudentAsync(new StudentDraft("Иванов", "Артём", 12, home.Id, "", "", ""));
        await classroom.CreateStudentAsync(new StudentDraft("Петров", "Олег", 11, other.Id, "", "", ""));

        var snapshot = await classroom.ExportLocationRosterAsync("ШБ");
        Assert.Single(snapshot.Groups);
        Assert.Single(snapshot.Students);

        var remote = snapshot with
        {
            Students =
            [
                snapshot.Students[0] with { FirstName = "Артём", Kiberons = 15 },
                new LocationStudentSnapshot(Guid.NewGuid(), "Сидорова", "Мила", 10, null, home.Id, "", "", "", 0, 0)
            ]
        };
        await classroom.ReplaceLocationRosterAsync(remote);

        var shb = await classroom.ListStudentsAsync(location: "ШБ");
        var artesha = await classroom.ListStudentsAsync(location: "АРТЕША");
        Assert.Equal(2, shb.Count);
        Assert.Contains(shb, x => x.LastName == "Сидорова");
        Assert.Single(artesha);
        Assert.Equal("Петров Олег", artesha[0].DisplayName);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public async Task Hub_RejectsWrongPassword_AndStoresRoster()
    {
        var data = Path.Combine(Path.GetTempPath(), $"kiberone-hub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(data);
        var created = LocationPassword.Create("Shb-Test-4821");
        var store = new ClassroomHubStore(data, [new LocationSecretRecord("ШБ", created.Salt, created.Hash)]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        ClassroomHubApi.Map(app, store);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var client = new ClassroomHubClient(address);
        var empty = await client.DownloadAsync("ШБ");
        Assert.NotNull(empty);
        Assert.Empty(empty!.Students);

        var snapshot = new LocationRosterSnapshot(
            "ШБ",
            DateTimeOffset.UtcNow,
            [new LocationGroupSnapshot(Guid.NewGuid(), "Мл3Сб10", "Figma", "", "ШБ", [])],
            []);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.UploadAsync("ШБ", "wrong", snapshot));
        await client.UploadAsync("ШБ", "Shb-Test-4821", snapshot);
        var loaded = await client.DownloadAsync("ШБ");
        Assert.Single(loaded!.Groups);
        Assert.Equal("Мл3Сб10", loaded.Groups[0].Name);

        await app.StopAsync();
        Directory.Delete(data, true);
    }
}
