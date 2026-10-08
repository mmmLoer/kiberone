using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class DeviceIdentityRegressionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public async Task ProductionServerRejectsImpersonationAndKeepsPinsAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-device-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = ClassroomDatabase.CreateOptions(Path.Combine(root, "classroom.db"));
            await ClassroomDatabase.InitializeAsync(options);
            var classroom = new ClassroomService(options);
            var sync = new FileSyncService(options, Path.Combine(root, "sync"));
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var token = DeviceCredentials.NewSecret();
            var tutorToken = DeviceCredentials.NewSecret();
            var victimSecret = DeviceCredentials.NewSecret();
            var attackerSecret = DeviceCredentials.NewSecret();
            ClassroomServer Server()
            {
                var registry = new ClientRegistry();
                var commands = new ReliableCommandQueue(registry);
                return new ClassroomServer(new ClassroomServerOptions(token, tutorToken, port),
                    new TypingLessonService(options), classroom, sync, new AssetDistributionService(root, root),
                    new QuizService(options, registry, commands), new AuditService(options), registry, commands);
            }
            HttpClient Http(string id, string? secret)
            {
                var http = new HttpClient(new HttpClientHandler { UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                http.DefaultRequestHeaders.Add("X-Sync-Token", token);
                http.DefaultRequestHeaders.Add("X-Client-Id", id);
                if (secret is not null) http.DefaultRequestHeaders.Add("X-Client-Secret", secret);
                return http;
            }
            HeartbeatRequest Beat(string id) => new(id, "1", "host", root, BuildInfo.Version,
                null, null, new ClientRuntimeInfo(false, false, "", null));
            using var victim = Http("victim", victimSecret);
            using var attacker = Http("attacker", attackerSecret);
            using var impersonator = Http("victim", attackerSecret);
            using var legacy = Http("victim", null);
            await using (var server = Server())
            {
                await server.StartAsync();
                Assert.Equal(HttpStatusCode.OK, (await victim.PostAsJsonAsync("/heartbeat", Beat("victim"), Json)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await attacker.PostAsJsonAsync("/heartbeat", Beat("attacker"), Json)).StatusCode);
                foreach (var route in new[] { "/mail/account", "/list?client_id=victim", "/download?client_id=victim&path=secret.txt", "/commands?client_id=victim", "/sync/approval?client_id=victim" })
                {
                    Assert.Equal(HttpStatusCode.Unauthorized, (await impersonator.GetAsync(route)).StatusCode);
                    Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(route)).StatusCode);
                    if (route.Contains("client_id=")) Assert.Equal(HttpStatusCode.Forbidden, (await attacker.GetAsync(route)).StatusCode);
                }
                Assert.Equal(HttpStatusCode.Unauthorized, (await impersonator.PostAsJsonAsync("/heartbeat", Beat("victim"), Json)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await attacker.PostAsJsonAsync("/heartbeat", Beat("victim"), Json)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await attacker.PostAsJsonAsync("/sync/prepare", new SyncPrepareRequest("victim", [], false, false, 5), Json)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await attacker.PostAsJsonAsync("/sync/complete", new { client_id = "victim" }, Json)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await attacker.PostAsJsonAsync("/sync/prepare",
                    new SyncPrepareRequest("attacker", [], false, false, 5, Guid.NewGuid()), Json)).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await attacker.PostAsync($"/commands/{Guid.NewGuid()}/ack?client_id=victim",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                using var validSocket = new ClientWebSocket();
                validSocket.Options.SetRequestHeader("X-Sync-Token", token);
                validSocket.Options.SetRequestHeader("X-Client-Id", "victim");
                validSocket.Options.SetRequestHeader("X-Client-Secret", victimSecret);
                await validSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?client_id=victim"), CancellationToken.None);
                Assert.Equal(WebSocketState.Open, validSocket.State);
                await validSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                using var querySocket = new ClientWebSocket();
                await querySocket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?token={token}&client_id=victim&client_secret={victimSecret}"), CancellationToken.None);
                Assert.Equal(WebSocketState.Open, querySocket.State);
                await querySocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                using var rejectedQuerySocket = new ClientWebSocket();
                await Assert.ThrowsAsync<WebSocketException>(() => rejectedQuerySocket.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{port}/ws?token={token}&client_id=victim&client_secret={attackerSecret}"), CancellationToken.None));
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("X-Sync-Token", token);
                socket.Options.SetRequestHeader("X-Client-Id", "victim");
                socket.Options.SetRequestHeader("X-Client-Secret", attackerSecret);
                await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?client_id=victim"), CancellationToken.None));
                using var tutor = Http("internal", null);
                tutor.DefaultRequestHeaders.Add("X-Tutor", "1");
                tutor.DefaultRequestHeaders.Add("X-Tutor-Token", tutorToken);
                Assert.Equal(HttpStatusCode.OK, (await tutor.GetAsync("/list?client_id=victim")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await victim.PostAsJsonAsync("/heartbeat", Beat("victim"), Json)).StatusCode);
            }
            await using (var restarted = Server())
            {
                await restarted.StartAsync();
                Assert.Equal(HttpStatusCode.Unauthorized, (await impersonator.PostAsJsonAsync("/heartbeat", Beat("victim"), Json)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await victim.PostAsJsonAsync("/heartbeat", Beat("victim"), Json)).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await victim.PostAsync("/presence", null)).StatusCode);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PersistedSecretsAndPinsNeverRotateImplicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-pins-" + Guid.NewGuid().ToString("N"));
        try
        {
            var secrets = new DeviceCredentials(Path.Combine(root, "secrets"));
            var secret = secrets.GetOrCreateSecret("pc");
            Assert.Equal(secret, new DeviceCredentials(Path.Combine(root, "secrets")).GetOrCreateSecret("PC"));
            var pins = new DeviceCredentials(Path.Combine(root, "pins"));
            Assert.True(pins.AuthenticateOrEnroll("pc", secret));
            Assert.False(new DeviceCredentials(Path.Combine(root, "pins")).AuthenticateOrEnroll("PC", DeviceCredentials.NewSecret()));
            Assert.False(pins.AuthenticateOrEnroll("pc", ""));
            var challengers = Enumerable.Range(0, 16).Select(_ => DeviceCredentials.NewSecret()).ToArray();
            var outcomes = await Task.WhenAll(challengers.Select(value => Task.Run(() =>
                new DeviceCredentials(Path.Combine(root, "pins")).AuthenticateOrEnroll("race", value))));
            Assert.Single(outcomes, value => value);
            var winningSecret = challengers[Array.IndexOf(outcomes, true)];
            Assert.True(new DeviceCredentials(Path.Combine(root, "pins")).AuthenticateOrEnroll("race", winningSecret));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
