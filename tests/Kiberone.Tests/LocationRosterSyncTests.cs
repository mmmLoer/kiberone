using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Tests;

public sealed class LocationRosterSyncTests
{
    [Fact]
    public async Task Focus_rules_follow_group_through_roster_export_and_import()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"kiberone-focus-source-{Guid.NewGuid():N}.db");
        var secondPath = Path.Combine(Path.GetTempPath(), $"kiberone-focus-target-{Guid.NewGuid():N}.db");
        try
        {
            var first = ClassroomDatabase.CreateOptions(firstPath);
            var second = ClassroomDatabase.CreateOptions(secondPath);
            await ClassroomDatabase.InitializeAsync(first);
            await ClassroomDatabase.InitializeAsync(second);
            var source = new ClassroomService(first);
            var group = await source.CreateGroupAsync(new GroupDraft("Python 01", "Python", "", "ШБ"));
            await source.UpdateGroupFocusPolicyAsync(group.Id, "Яндекс Игры; Roblox", "chrome.exe; code.exe");
            var snapshot = await source.ExportLocationRosterAsync("ШБ");
            await new ClassroomService(second).ReplaceLocationRosterAsync(snapshot);
            var received = Assert.Single(await new ClassroomService(second).ListGroupsAsync("ШБ"));
            Assert.Equal("Яндекс Игры; Roblox", received.FocusBlockedTitles);
            Assert.Equal("chrome.exe; code.exe", received.FocusAllowedApps);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task DownloadingRoster_AdoptsServerGroupIdForEmptyLocalGroup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiberone-roster-group-{Guid.NewGuid():N}.db");
        var options = ClassroomDatabase.CreateOptions(path);
        await ClassroomDatabase.InitializeAsync(options);
        var classroom = new ClassroomService(options);
        var local = await classroom.CreateGroupAsync(new GroupDraft("Мл3Сб10", "Figma", "", "ШБ"));
        var remoteId = Guid.NewGuid();
        var snapshot = new LocationRosterSnapshot(
            "ШБ", DateTimeOffset.UtcNow,
            [new LocationGroupSnapshot(remoteId, local.Name, "Python", "", "ШБ", [])],
            [new LocationStudentSnapshot(Guid.NewGuid(), "Иванов", "Артём", 12, null, remoteId, "", "", "", 0, 0)]);

        await classroom.ReplaceLocationRosterAsync(snapshot);

        await using (var verify = new ClassroomDbContext(options))
        {
            Assert.Equal(remoteId, (await verify.Groups.SingleAsync()).Id);
            Assert.Equal(remoteId, (await verify.Students.SingleAsync()).GroupId);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public async Task DownloadingSameRoster_PreservesTypingHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiberone-roster-history-{Guid.NewGuid():N}.db");
        var options = ClassroomDatabase.CreateOptions(path);
        await ClassroomDatabase.InitializeAsync(options);
        var classroom = new ClassroomService(options);
        var group = await classroom.CreateGroupAsync(new GroupDraft("Мл3Сб10", "Figma", "", "ШБ"));
        var student = await classroom.CreateStudentAsync(new StudentDraft("Иванов", "Артём", 12, group.Id, "", "", ""));
        await using (var db = new ClassroomDbContext(options))
        {
            var lesson = new TypingLessonTemplate { Name = "Проверка истории" };
            db.TypingLessons.Add(lesson);
            db.TypingSessions.Add(new TypingSession
            {
                Lesson = lesson,
                GroupId = group.Id,
                Participants = [new TypingParticipant { StudentId = student.Id }]
            });
            await db.SaveChangesAsync();
        }

        var snapshot = await classroom.ExportLocationRosterAsync("ШБ");
        var changed = snapshot with { Students = [snapshot.Students[0] with { Kiberons = 15 }] };
        await classroom.ReplaceLocationRosterAsync(changed);
        await classroom.ReplaceLocationRosterAsync(changed);

        await using (var verify = new ClassroomDbContext(options))
        {
            Assert.Single(await verify.Students.ToListAsync());
            Assert.Equal(15, await verify.Students.Where(x => x.Id == student.Id).Select(x => x.Kiberons).SingleAsync());
            Assert.Single(await verify.TypingParticipants.Where(x => x.StudentId == student.Id).ToListAsync());
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

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
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.DownloadAsync("ШБ", "wrong"));
        var empty = await client.DownloadAsync("ШБ", "Shb-Test-4821");
        Assert.NotNull(empty);
        Assert.Empty(empty!.Students);

        var groupId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var snapshot = new LocationRosterSnapshot(
            "ШБ",
            DateTimeOffset.UtcNow,
            [new LocationGroupSnapshot(groupId, "Мл3Сб10", "Figma", "", "ШБ", [])],
            [new LocationStudentSnapshot(studentId,"Иванов","Артём",12,null,groupId,"","","crm-test-1",5,10)]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.UploadAsync("ШБ", "wrong", snapshot));
        await client.UploadAsync("ШБ", "Shb-Test-4821", snapshot);
        var loaded = await client.DownloadAsync("ШБ", "Shb-Test-4821");
        Assert.Single(loaded!.Groups);
        Assert.Equal("Мл3Сб10", loaded.Groups[0].Name);
        var student = Assert.Single(loaded.Students);
        Assert.Equal(groupId, student.GroupId);
        Assert.Equal(studentId, student.Id);
        Assert.Equal("Артём", student.FirstName);
        Assert.Equal("crm-test-1", student.CrmId);
        var databasePath = Path.Combine(data, "downloaded.db");
        var options = ClassroomDatabase.CreateOptions(databasePath);
        await ClassroomDatabase.InitializeAsync(options);
        var classroom = new ClassroomService(options);
        await classroom.ReplaceLocationRosterAsync(loaded);
        Assert.Single(await classroom.ListStudentsAsync(location: "ШБ"));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await app.StopAsync();
        Directory.Delete(data, true);
    }
}
