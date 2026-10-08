using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class StudentUpdateChannelTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    // Existing signed public test fixture; no private signing key is used by tests.
    private const string Signature = "Gh2/yvyfllQPb+uw3Vs6kYE7l3VYaQIEStWY7JebgE6gQvF+CpoQ38GPZxhWiTe7gVVbI2JJCrgZ96+Ibq7wgjTGHhXqYEC7jgUx5TEaevb7aV0pBvECNWwgSQ1MsHw9DMImp1zYYadEWrKVtn9sMqInx2A486VHKEW46BL46KCIKAIuyRkGAZ1AJe1iZ6jANAJcXWE7XmogQm76lxH+t/re+C8bvC90rt/bU9J0sUjQIk3ZBAJP60l+XTXqfmXsncdNHmBxBqc93nWNy1JINodcRVWHaqwiP6Z9TTiR2+qVtuLRKw/3jkQ1ABe7y+iefq0hr82d4xRmkL4acBZDLFlc9BA9uLjAR65nIjITCA+AZOhJM2MqlQCXL0o700Nc2FFiLGimtWSOeMUvwMpSC0fXybUV1nRfV/8McyHjfQJ7D+v94Qw15zEzc4Zv+1ztaMw1WaTxBfgfw+TQir/qOxyM5qIBsCXVIY1J98fTY01oXi7kel5oQEEhsS8pRbER";
    private static readonly byte[] Binary = Encoding.UTF8.GetBytes("student-binary");
    private static AppUpdateManifest Release => new("9.1.0", "KIBERoneStudent.exe", Binary.LongLength,
        Convert.ToHexString(SHA256.HashData(Binary)), DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Signature);

    [Fact]
    public void ImportedRelease_IsVersionKeyedAndSurvivesRestart_WithoutBetaFallback()
    {
        var root = NewRoot();
        try
        {
            var assets = new AssetDistributionService(root, root);
            assets.ImportStudentRelease(Release, Binary);
            var directory = Path.Combine(root, "updates", "student", "release", Release.Version);
            Assert.Equal(Binary, File.ReadAllBytes(Path.Combine(directory, Release.Filename)));
            Assert.True(File.Exists(Path.Combine(directory, "student_manifest.json")));
            var restarted = new AssetDistributionService(root, root);
            Assert.Equal(Release.Version, restarted.GetStudentRelease("release")!.Version);
            Assert.NotNull(restarted.GetUpdateFor("9.0.0"));
            Assert.Null(restarted.GetUpdateFor("9.1.0"));
            Assert.Null(restarted.GetUpdateFor("9.2.0"));
            Assert.Null(restarted.GetUpdateFor("9.0.0b"));
            Assert.Null(restarted.GetUpdateFor("not-a-version"));
            Assert.Null(restarted.GetStudentRelease("beta"));
            Assert.Null(restarted.OpenStudentUpdate("beta"));
            Assert.Null(restarted.OpenStudentUpdate("release", "9.1.0b"));
            Assert.Null(restarted.OpenStudentUpdate("release", "9.0.0"));
            using var pinned = restarted.OpenStudentUpdate("release", "9.1.0");
            Assert.NotNull(pinned);
            using var copy = new MemoryStream();
            pinned!.CopyTo(copy);
            Assert.Equal(Binary, copy.ToArray());
            // Reusing a release signature for a beta must not replace any stored package.
            Assert.Throws<LessonValidationException>(() => assets.ImportStudentRelease(Release with { Version = "9.1.0b" }, Binary));
            Assert.Equal(Release.Version, restarted.GetStudentRelease("release")!.Version);
            Assert.False(Directory.Exists(Path.Combine(root, "updates", "student", "beta")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyManifest_IsStillServedOnlyToItsOwnChannel()
    {
        var root = NewRoot();
        try
        {
            var updates = Path.Combine(root, "updates");
            Directory.CreateDirectory(updates);
            File.WriteAllBytes(Path.Combine(updates, Release.Filename), Binary);
            File.WriteAllText(Path.Combine(updates, "student_manifest.json"), JsonSerializer.Serialize(Release, Json));
            var assets = new AssetDistributionService(root, root);
            Assert.NotNull(assets.GetUpdateFor("9.0.0"));
            Assert.Null(assets.GetUpdateFor("9.0.0b"));
            using var release = assets.OpenStudentUpdate("release", "9.1.0");
            Assert.NotNull(release);
            Assert.Null(assets.OpenStudentUpdate("beta", "9.1.0b"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BundledChannelManifest_UsesAdjacentExecutable_ForLatestAndPinnedDownloads()
    {
        var root = NewRoot();
        try
        {
            var updates = Path.Combine(root, "updates");
            var bundled = Path.Combine(updates, "release");
            Directory.CreateDirectory(bundled);
            File.WriteAllText(Path.Combine(updates, Release.Filename), "wrong root executable");
            File.WriteAllBytes(Path.Combine(bundled, Release.Filename), Binary);
            File.WriteAllText(Path.Combine(bundled, "student_manifest.json"), JsonSerializer.Serialize(Release, Json));
            var assets = new AssetDistributionService(root, root);
            Assert.Equal("9.1.0", assets.GetStudentRelease("release")!.Version);
            Assert.Equal("9.1.0", assets.GetUpdateFor("9.0.0")!.Version);
            foreach (var version in new string?[] { null, "9.1.0" })
            {
                using var stream = assets.OpenStudentUpdate("release", version);
                Assert.NotNull(stream);
                using var copy = new MemoryStream();
                stream!.CopyTo(copy);
                Assert.Equal(Binary, copy.ToArray());
            }
            Assert.Null(assets.OpenStudentUpdate("release", "9.0.0"));
            // A release manifest in the beta bundle must not cross channels.
            var beta = Path.Combine(updates, "beta");
            Directory.CreateDirectory(beta);
            File.WriteAllBytes(Path.Combine(beta, Release.Filename), Binary);
            File.WriteAllText(Path.Combine(beta, "student_manifest.json"), JsonSerializer.Serialize(Release, Json));
            Assert.Null(assets.GetStudentRelease("beta"));
            Assert.Null(assets.OpenStudentUpdate("beta"));
            Assert.Null(assets.GetUpdateFor("9.0.0b"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Relay_UsesAuthenticatedHeartbeatChannel_AndRejectsCrossChannelDownloads()
    {
        var root = NewRoot();
        try
        {
            var options = ClassroomDatabase.CreateOptions(Path.Combine(root, "classroom.db"));
            await ClassroomDatabase.InitializeAsync(options);
            var clients = new ClientRegistry();
            var commands = new ReliableCommandQueue(clients);
            var assets = new AssetDistributionService(root, root);
            assets.ImportStudentRelease(Release, Binary);
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var syncToken = DeviceCredentials.NewSecret();
            var tutorToken = DeviceCredentials.NewSecret();
            await using var server = new ClassroomServer(new ClassroomServerOptions(syncToken, tutorToken, port),
                new TypingLessonService(options), new ClassroomService(options), new FileSyncService(options, root), assets,
                new QuizService(options, clients, commands), new AuditService(options), clients, commands);
            await server.StartAsync();
            HttpClient Http(string id, bool authenticate = true)
            {
                var http = new HttpClient(new HttpClientHandler { UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                http.DefaultRequestHeaders.Add("X-Sync-Token", syncToken);
                http.DefaultRequestHeaders.Add("X-Client-Id", id);
                if (authenticate) http.DefaultRequestHeaders.Add("X-Client-Secret", DeviceCredentials.NewSecret());
                return http;
            }
            using var release = Http("release-pc");
            using var beta = Http("beta-pc");
            using var fresh = Http("fresh-pc");
            using var unauthenticated = Http("unknown-pc", false);
            using var tutor = Http("tutor");
            tutor.DefaultRequestHeaders.Add("X-Tutor", "1");
            tutor.DefaultRequestHeaders.Add("X-Tutor-Token", tutorToken);
            HeartbeatRequest Beat(string id, string version) => new(id, "1", id, root, version, null, null,
                new ClientRuntimeInfo(false, false, "", null));
            using var releaseBeat = await release.PostAsJsonAsync("/heartbeat", Beat("release-pc", "9.0.0"), Json);
            releaseBeat.EnsureSuccessStatusCode();
            var releaseSettings = (await releaseBeat.Content.ReadFromJsonAsync<HeartbeatResponse>(Json))!;
            Assert.Equal("9.1.0", releaseSettings.StudentUpdate!.Version);
            using var betaBeat = await beta.PostAsJsonAsync("/heartbeat", Beat("beta-pc", "9.0.0b"), Json);
            betaBeat.EnsureSuccessStatusCode();
            Assert.Null((await betaBeat.Content.ReadFromJsonAsync<HeartbeatResponse>(Json))!.StudentUpdate);
            Assert.Equal(Binary, await release.GetByteArrayAsync("/update/student/file?version=9.1.0"));
            using var manifest = await release.GetAsync("/update/student");
            Assert.Equal("9.1.0", (await manifest.Content.ReadFromJsonAsync<StudentReleaseManifest>(Json))!.Version);
            foreach (var route in new[] { "/update/student", "/update/student/file" })
            {
                using var wrongReleaseChannel = await release.GetAsync(route + "?channel=beta");
                Assert.Equal(HttpStatusCode.Forbidden, wrongReleaseChannel.StatusCode);
                using var wrongBetaChannel = await beta.GetAsync(route + "?channel=release");
                Assert.Equal(HttpStatusCode.Forbidden, wrongBetaChannel.StatusCode);
                using var betaDefault = await beta.GetAsync(route);
                Assert.Equal(HttpStatusCode.NotFound, betaDefault.StatusCode);
                using var missingIdentity = await unauthenticated.GetAsync(route + "?channel=release");
                Assert.Equal(HttpStatusCode.Unauthorized, missingIdentity.StatusCode);
                using var noHeartbeat = await fresh.GetAsync(route);
                Assert.Equal(HttpStatusCode.BadRequest, noHeartbeat.StatusCode);
                using var explicitChannel = await fresh.GetAsync(route + "?channel=release");
                Assert.Equal(HttpStatusCode.OK, explicitChannel.StatusCode);
                using var tutorRelease = await tutor.GetAsync(route + "?channel=release");
                Assert.Equal(HttpStatusCode.OK, tutorRelease.StatusCode);
                using var tutorBeta = await tutor.GetAsync(route + "?channel=beta");
                Assert.Equal(HttpStatusCode.NotFound, tutorBeta.StatusCode);
                using var invalidChannel = await tutor.GetAsync(route + "?channel=../../release");
                Assert.Equal(HttpStatusCode.BadRequest, invalidChannel.StatusCode);
            }
            using var wrongVersion = await release.GetAsync("/update/student/file?version=9.1.0b");
            Assert.Equal(HttpStatusCode.BadRequest, wrongVersion.StatusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Student_DiscardsWrongChannelHeartbeat_AndRejectsStagingBeforeDownloading()
    {
        var root = NewRoot();
        try
        {
            await using var agent = new StudentAgent(watchFolder: root);
            var wrongVersion = BuildInfo.Channel == "release" ? "99.0.0b" : "99.0.0";
            var update = new StudentUpdateInfo(wrongVersion, Release.Sha256, Release.Size, Signature);
            using var handler = new HeartbeatReply(new HeartbeatResponse(true, DateTimeOffset.UtcNow, 3, 300, update));
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
            var availableEvents = 0;
            agent.UpdateAvailable += _ => availableEvents++;
            typeof(StudentAgent).GetField("availableUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(agent, update);
            await Invoke(agent, "SendHeartbeatAsync", http, CancellationToken.None);
            Assert.Equal(0, availableEvents);
            Assert.Null(typeof(StudentAgent).GetField("availableUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent));
            var failures = 0;
            var restarts = 0;
            string? failureMessage = null;
            agent.UpdateFailed += () => failures++;
            agent.UpdateStateChanged += message => failureMessage = message;
            agent.UpdateRestartRequested += () => restarts++;
            await Invoke(agent, "StageUpdateAsync", http, update, CancellationToken.None);
            Assert.Equal(1, failures);
            Assert.Contains("другому каналу", failureMessage!);
            Assert.Equal(0, restarts);
            Assert.Equal(0, handler.DownloadCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Student_AdvertisesNewerSameChannelOffer_ButNotEqualOrInvalidVersions()
    {
        var root = NewRoot();
        try
        {
            await using var agent = new StudentAgent(watchFolder: root);
            var candidate = "99.0.0" + (BuildInfo.Channel == "beta" ? "b" : "");
            var update = new StudentUpdateInfo(candidate, Release.Sha256, Release.Size, Signature);
            var events = new List<StudentUpdateInfo>();
            agent.UpdateAvailable += events.Add;
            foreach (var version in new[] { candidate, BuildInfo.Version, "invalid" })
            {
                using var handler = new HeartbeatReply(new HeartbeatResponse(true, DateTimeOffset.UtcNow, 3, 300, update with { Version = version }));
                using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
                await Invoke(agent, "SendHeartbeatAsync", http, CancellationToken.None);
            }
            Assert.Equal(candidate, Assert.Single(events).Version);
            Assert.Null(typeof(StudentAgent).GetField("availableUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(agent));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class HeartbeatReply(HeartbeatResponse response) : HttpMessageHandler
    {
        public int DownloadCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/heartbeat") DownloadCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: Json) });
        }
    }

    private static Task Invoke(StudentAgent agent, string method, params object[] args) =>
        (Task)typeof(StudentAgent).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(agent, args)!;

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-channel-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
