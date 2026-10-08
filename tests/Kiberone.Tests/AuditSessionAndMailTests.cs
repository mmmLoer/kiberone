using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TutorModel = Kiberone.Tutor.ViewModels.MainViewModel;

namespace Kiberone.Tests;

public sealed class AuditSessionAndMailTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_PublishesClearedIdentity_EvenWhenSaveUploadFails(bool rejectLogoutHeartbeat)
    {
        var root = NewRoot();
        var registry = new ClientRegistry();
        var heartbeats = new List<HeartbeatRequest>();
        var uploadAttempts = 0;
        await using var app = CreateServer();
        app.MapPost("/heartbeat", async (HttpContext context) =>
        {
            var request = (await context.Request.ReadFromJsonAsync<HeartbeatRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }))!;
            heartbeats.Add(request);
            if (request.StudentId is null && rejectLogoutHeartbeat) return Results.StatusCode(503);
            registry.Heartbeat(request);
            return Results.Text("null", "application/json");
        });
        // Deliberately fail the save upload: logout must still clear identity.
        app.MapPost("/sync/prepare", () => { uploadAttempts++; return Results.StatusCode(503); });
        app.MapPost("/typing/records", () => Results.Json(Array.Empty<TypingPersonalBest>()));
        await app.StartAsync();
        try
        {
            await using var agent = new StudentAgent(watchFolder: root);
            var owner = Guid.NewGuid();
            agent.AssignStudent(owner);
            var address = new Uri(Address(app));
            typeof(StudentAgent).GetField("currentBeacon", PrivateInstance)!.SetValue(agent,
                new DiscoveryBeacon("test", "test-token", address.Host, address.Port, "test", "test"));

            await agent.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(2, heartbeats.Count);
            Assert.Equal(1, uploadAttempts);
            Assert.Equal(owner, heartbeats[0].StudentId);
            Assert.Null(heartbeats[1].StudentId);
            Assert.Equal(heartbeats[0].ClientId, heartbeats[1].ClientId);
            Assert.Null(typeof(StudentAgent).GetField("studentId", PrivateInstance)!.GetValue(agent));
            var sync = (StudentFileSyncClient)typeof(StudentAgent).GetField("fileSync", PrivateInstance)!.GetValue(agent)!;
            Assert.Null(sync.StudentId);
            if (!rejectLogoutHeartbeat)
            {
                Assert.True(registry.Touch(heartbeats[1].ClientId));
                Assert.Null(Assert.Single(registry.GetAll()).StudentId);
            }
        }
        finally { await app.StopAsync(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Provisioning_KeepsOriginalServer_WhenUrlChangesDuringRequest()
    {
        var root = NewRoot();
        var received = new TaskCompletionSource<StudentMailProvisionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = CreateServer();
        app.MapPost("/api/mail/accounts", async (StudentMailProvisionRequest request) =>
        {
            received.TrySetResult(request);
            await release.Task;
            return Results.Ok(new StudentMailAccount(request.StudentId, "test@students.nshub.pro", "test-password"));
        });
        await app.StartAsync();
        Task<StudentMailAccount>? pending = null;
        try
        {
            var options = ClassroomDatabase.CreateOptions(Path.Combine(root, "classroom.db"));
            await ClassroomDatabase.InitializeAsync(options);
            var classroom = new ClassroomService(options);
            var group = await classroom.CreateGroupAsync(new GroupDraft("Audit", "Python", "", "AUDIT"));
            var student = await classroom.CreateStudentAsync(new StudentDraft("Audit", "Student", 10, group.Id, "", "", ""));
            var clients = new ClientRegistry();
            var commands = new ReliableCommandQueue(clients);
            var model = new TutorModel(new TypingLessonService(options), classroom, new FileSyncService(options, root),
                new AssetDistributionService(root, root), clients, commands, new QuizService(options, clients, commands),
                new AuditService(options), Path.Combine(root, "quizzes"), Path.Combine(root, "settings"),
                new LocationCredentialStore(Path.Combine(root, "credentials")));
            typeof(TutorModel).GetField("loadingSettings", PrivateInstance)!.SetValue(model, true);
            model.LocationName = "AUDIT";
            model.LocationUploadPassword = "audit-password";
            var originalServer = Address(app);
            model.MailServerUrl = originalServer;
            pending = (Task<StudentMailAccount>)typeof(TutorModel).GetMethod("ProvideMailAccountAsync", PrivateInstance)!
                .Invoke(model, [student.Id, CancellationToken.None])!;
            var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(student.Id, request.StudentId);
            model.MailServerUrl = "http://127.0.0.1:1/";
            release.TrySetResult();
            var account = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(originalServer, account.ServerUrl);
            Assert.Equal(student.Id, account.StudentId);
            Assert.Equal("test-password", account.Password);
        }
        finally
        {
            release.TrySetResult();
            if (pending is not null) { try { await pending; } catch { } }
            await app.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaildirRead_SkipsAnEnumeratedFileDeletedBeforeLoading(bool removeDirectory)
    {
        var root = NewRoot();
        try
        {
            var folder = Path.Combine(root, "new");
            Directory.CreateDirectory(folder);
            var removed = Path.Combine(folder, "removed.eml");
            var surviving = Path.Combine(root, "surviving.eml");
            const string message = "From: sender@example.com\r\nTo: student@example.com\r\nSubject: Surviving message\r\nDate: Thu, 08 Oct 2026 12:00:00 +0000\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello\r\n";
            await File.WriteAllTextAsync(removed, message);
            await File.WriteAllTextAsync(surviving, message);
            var enumerated = Directory.GetFiles(folder).Single();
            if (removeDirectory) Directory.Delete(folder, true);
            else File.Delete(removed);

            Assert.Null(await ReadMailFile(enumerated));
            Assert.Equal("Surviving message", (await ReadMailFile(surviving))!.Subject);
            // An unrelated I/O failure must not be swallowed as a disappearing file.
            using var locked = new FileStream(surviving, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.ThrowsAsync<IOException>(() => ReadMailFile(surviving));
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<StudentMailMessage?> ReadMailFile(string file) =>
        (Task<StudentMailMessage?>)typeof(StudentMailboxStore).GetMethod("ReadMessageAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [file, CancellationToken.None])!;

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static WebApplication CreateServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    private static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
