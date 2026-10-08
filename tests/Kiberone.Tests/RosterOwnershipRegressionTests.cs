using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Kiberone.Tests;

public sealed class RosterOwnershipRegressionTests : IDisposable
{
    private const string Password = "test-only-location-password";
    private readonly string root = Path.Combine(Path.GetTempPath(), "kiberone-roster-owner-" + Guid.NewGuid().ToString("N"));
    private readonly ClassroomHubStore store;

    public RosterOwnershipRegressionTests()
    {
        var secret = LocationPassword.Create(Password);
        store = new ClassroomHubStore(root,
            [new LocationSecretRecord("A", secret.Salt, secret.Hash), new LocationSecretRecord("B", secret.Salt, secret.Hash)]);
    }

    [Theory]
    [InlineData("student")]
    [InlineData("group")]
    [InlineData("group-reference")]
    public void PutRejectsForeignUuidWithoutChangingEitherRoster(string collision)
    {
        var original = Roster("A");
        store.Put("A", Password, original);
        var previous = store.Put("B", Password, Roster("B"));
        var incoming = Roster("B");
        incoming = collision switch
        {
            "student" => incoming with { Students = [incoming.Students[0] with { Id = original.Students[0].Id }] },
            "group" => incoming with
            {
                Groups = [incoming.Groups[0] with { Id = original.Groups[0].Id }],
                Students = [incoming.Students[0] with { GroupId = original.Groups[0].Id }]
            },
            _ => incoming with { Students = [incoming.Students[0] with { GroupId = original.Groups[0].Id }] }
        };
        var beforeA = File.ReadAllText(Path.Combine(root, "rosters", "A.json"));
        var beforeB = File.ReadAllText(Path.Combine(root, "rosters", "B.json"));
        Assert.Throws<UnauthorizedAccessException>(() => store.Put("B", Password, incoming));
        Assert.Equal(beforeA, File.ReadAllText(Path.Combine(root, "rosters", "A.json")));
        Assert.Equal(beforeB, File.ReadAllText(Path.Combine(root, "rosters", "B.json")));
        Assert.Equal(previous.Students[0].Id, store.GetAuthorized("B", Password).Students[0].Id);
    }

    [Fact]
    public void SameLocationCanUpdateItsExistingUuids()
    {
        var original = Roster("A");
        store.Put("A", Password, original);
        var updated = original with { Students = [original.Students[0] with { FirstName = "Updated" }] };
        store.Put("A", Password, updated);
        Assert.Equal("Updated", store.GetAuthorized("A", Password).Students[0].FirstName);
        store.Put("B", Password, Roster("B"));
        Assert.Single(store.GetAuthorized("B", Password).Students);
    }

    [Fact]
    public async Task ConcurrentLocationsCannotClaimSameStudentUuid()
    {
        var studentId = Guid.NewGuid();
        var outcomes = await Task.WhenAll(new[] { "A", "B" }.Select(location => Task.Run(() =>
        {
            var roster = Roster(location);
            roster = roster with { Students = [roster.Students[0] with { Id = studentId }] };
            try { store.Put(location, Password, roster); return true; }
            catch (UnauthorizedAccessException) { return false; }
        })));
        Assert.Single(outcomes, success => success);
        Assert.Equal(1, store.Get("A").Students.Count + store.Get("B").Students.Count);
    }

    [Fact]
    public void EnrollmentAlsoRejectsForeignStudentAndGroupUuids()
    {
        var original = Roster("A");
        store.Put("A", Password, original);
        var incoming = Roster("B");
        Assert.Throws<UnauthorizedAccessException>(() => store.EnrollMailStudent("B", Password,
            incoming.Students[0] with { Id = original.Students[0].Id }, incoming.Groups[0]));
        Assert.Throws<UnauthorizedAccessException>(() => store.EnrollMailStudent("B", Password,
            incoming.Students[0] with { GroupId = original.Groups[0].Id },
            incoming.Groups[0] with { Id = original.Groups[0].Id }));
        Assert.Empty(store.Get("B").Students);
    }

    [Fact]
    public async Task ForeignRosterUploadCannotExposeExistingMailboxCredentials()
    {
        var original = Roster("A");
        store.Put("A", Password, original);
        var mailboxes = new StudentMailboxStore(root, Path.Combine(root, "mail")) { ProvisionMailbox = _ => { } };
        var account = mailboxes.GetOrCreate(original.Students[0].Id);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        ClassroomHubApi.Map(app, store, mailboxes);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var incoming = Roster("B");
            incoming = incoming with { Students = [incoming.Students[0] with { Id = account.StudentId }] };
            using var upload = await http.PutAsJsonAsync("/api/locations/B/roster", new { password = Password, snapshot = incoming });
            Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
            using var denied = await http.PostAsJsonAsync("/api/mail/accounts", new StudentMailProvisionRequest("B", Password, account.StudentId));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            // Old contaminated rosters must also fail closed when retrieving mail accounts.
            File.WriteAllText(Path.Combine(root, "rosters", "B.json"),
                JsonSerializer.Serialize(incoming, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            using var legacyDenied = await http.PostAsJsonAsync("/api/mail/accounts", new StudentMailProvisionRequest("B", Password, account.StudentId));
            Assert.Equal(HttpStatusCode.Unauthorized, legacyDenied.StatusCode);
            Assert.DoesNotContain(account.Password, await legacyDenied.Content.ReadAsStringAsync());
        }
        finally { await app.StopAsync(); }
    }

    private static LocationRosterSnapshot Roster(string location)
    {
        var groupId = Guid.NewGuid();
        return new LocationRosterSnapshot(location, DateTimeOffset.UtcNow,
            [new LocationGroupSnapshot(groupId, "Group", "Python", "", location, [])],
            [new LocationStudentSnapshot(Guid.NewGuid(), "Last", "First", 12, null, groupId, "", "", "", 0, 0)]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
