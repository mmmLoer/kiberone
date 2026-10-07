using System.Net;
using System.Net.Http.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Kiberone.Tests;

public sealed class StudentMailApiTests
{
    [Fact]
    public async Task Mail_api_requires_location_membership_and_private_mailbox_password()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-mail-api-" + Guid.NewGuid().ToString("N"));
        var secret = LocationPassword.Create("test-only-password");
        var store = new ClassroomHubStore(root, [new LocationSecretRecord("Тест", secret.Salt, secret.Hash)]);
        var studentId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        store.Put("Тест", "test-only-password", new LocationRosterSnapshot("Тест", DateTimeOffset.UtcNow,
            [new LocationGroupSnapshot(groupId, "Группа", "Figma", "", "Тест", [])],
            [new LocationStudentSnapshot(studentId, "Тест", "Ученик", 12, null, groupId, "", "", "", 0, 0)]));
        var mail = new StudentMailboxStore(root, Path.Combine(root, "mail")) { ProvisionMailbox = _ => { } };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        StudentMailApi.Map(app, store, mail);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var wrongLocation = await http.PostAsJsonAsync("/api/mail/accounts", new StudentMailProvisionRequest("Тест", "wrong", studentId));
            Assert.Equal(HttpStatusCode.Unauthorized, wrongLocation.StatusCode);
            using var unknown = await http.PostAsJsonAsync("/api/mail/accounts", new StudentMailProvisionRequest("Тест", "test-only-password", Guid.NewGuid()));
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            using var provision = await http.PostAsJsonAsync("/api/mail/accounts", new StudentMailProvisionRequest("Тест", "test-only-password", studentId));
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);
            Assert.True(provision.Headers.CacheControl!.NoStore);
            var account = (await provision.Content.ReadFromJsonAsync<StudentMailAccount>())!;
            var addedId = Guid.NewGuid();
            var addedStudent = new LocationStudentSnapshot(addedId, "Новый", "Ученик", 10, null, groupId, "", "", "", 0, 0);
            var group = store.Get("Тест").Groups.Single();
            using var deniedEnrollment = await http.PostAsJsonAsync("/api/mail/accounts",
                new StudentMailProvisionRequest("Тест", "wrong", addedId, addedStudent, group));
            Assert.Equal(HttpStatusCode.Unauthorized, deniedEnrollment.StatusCode);
            Assert.Single(store.Get("Тест").Students);
            using var added = await http.PostAsJsonAsync("/api/mail/accounts",
                new StudentMailProvisionRequest("Тест", "test-only-password", addedId, addedStudent, group));
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var addedAccount = (await added.Content.ReadFromJsonAsync<StudentMailAccount>())!;
            using var repeated = await http.PostAsJsonAsync("/api/mail/accounts",
                new StudentMailProvisionRequest("Тест", "test-only-password", addedId, addedStudent, group));
            var repeatedAccount = (await repeated.Content.ReadFromJsonAsync<StudentMailAccount>())!;
            Assert.Equal(addedAccount, repeatedAccount);
            Assert.Equal(2, store.Get("Тест").Students.Count);
            Assert.Contains(store.Get("Тест").Students, x => x.Id == studentId);
            using var invalidGroup = await http.PostAsJsonAsync("/api/mail/accounts",
                new StudentMailProvisionRequest("Тест", "test-only-password", Guid.NewGuid(), addedStudent, group));
            Assert.Equal(HttpStatusCode.BadRequest, invalidGroup.StatusCode);
            using var unauthenticated = await http.GetAsync("/api/mail/inbox");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            using var mailboxClient = new StudentMailboxClient(address);
            Assert.Empty(await mailboxClient.ReadAsync(account));
            await Assert.ThrowsAsync<HttpRequestException>(() => mailboxClient.ReadAsync(account with { Password = "wrong" }));
        }
        finally { await app.StopAsync(); Directory.Delete(root, true); }
    }
}
