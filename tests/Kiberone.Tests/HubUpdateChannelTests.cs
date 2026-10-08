using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Kiberone.Tests;

public sealed class HubUpdateChannelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kiberone-update-channels-" + Guid.NewGuid().ToString("N"));
    private readonly ClassroomHubStore store;
    private static readonly byte[] Binary = Encoding.UTF8.GetBytes("student-binary");
    // Existing production-public-key fixture; no private signing material is needed.
    private const string Signature = "Gh2/yvyfllQPb+uw3Vs6kYE7l3VYaQIEStWY7JebgE6gQvF+CpoQ38GPZxhWiTe7gVVbI2JJCrgZ96+Ibq7wgjTGHhXqYEC7jgUx5TEaevb7aV0pBvECNWwgSQ1MsHw9DMImp1zYYadEWrKVtn9sMqInx2A486VHKEW46BL46KCIKAIuyRkGAZ1AJe1iZ6jANAJcXWE7XmogQm76lxH+t/re+C8bvC90rt/bU9J0sUjQIk3ZBAJP60l+XTXqfmXsncdNHmBxBqc93nWNy1JINodcRVWHaqwiP6Z9TTiR2+qVtuLRKw/3jkQ1ABe7y+iefq0hr82d4xRmkL4acBZDLFlc9BA9uLjAR65nIjITCA+AZOhJM2MqlQCXL0o700Nc2FFiLGimtWSOeMUvwMpSC0fXybUV1nRfV/8McyHjfQJ7D+v94Qw15zEzc4Zv+1ztaMw1WaTxBfgfw+TQir/qOxyM5qIBsCXVIY1J98fTY01oXi7kel5oQEEhsS8pRbER";
    private static AppUpdateManifest Manifest => new("9.1.0", "KIBERoneStudent.exe", Binary.LongLength,
        Convert.ToHexString(SHA256.HashData(Binary)), DateTimeOffset.UtcNow, Signature);

    public HubUpdateChannelTests() => store = new ClassroomHubStore(root, []);

    [Fact]
    public void ReleaseUsesCanonicalDirectoryAndDoesNotFallBackWhenInvalid()
    {
        Write("", "student", Manifest);
        Assert.Equal("9.1.0", store.GetAppUpdate("student", "release")!.Version);
        Write("release", "student", Manifest with { Signature = "invalid" });
        Assert.Null(store.GetAppUpdate("student", "release"));
        Write("release", "student", Manifest);
        Assert.Equal("9.1.0", store.GetStudentUpdate()!.Version);
        using var content = store.OpenAppUpdate("student", "release")!;
        using var memory = new MemoryStream();
        content.CopyTo(memory);
        Assert.Equal(Binary, memory.ToArray());
        Assert.Equal(0, content.Position - content.Length);
    }

    [Fact]
    public void LegacyTestIsSeparateFromBetaAndRelease()
    {
        Write("test", "student", Manifest);
        Assert.Equal("9.1.0", store.GetStudentUpdate(testChannel: true)!.Version);
        Assert.Null(store.GetAppUpdate("student", "beta"));
        Assert.Null(store.GetAppUpdate("student", "release"));
        using var legacy = store.OpenStudentUpdate(testChannel: true);
        Assert.NotNull(legacy);
    }

    [Theory]
    [InlineData("student", "beta", "9.1.0")]
    [InlineData("tutor", "beta", "9.1.0")]
    [InlineData("student", "release", "9.1.0b")]
    [InlineData("tutor", "release", "9.1.0b")]
    [InlineData("student", "release", "9.1.0-preview")]
    public void StoreRejectsVersionsFromWrongOrInvalidChannel(string app, string channel, string version)
    {
        Write(channel, app, Manifest with { Version = version });
        Assert.Null(store.GetAppUpdate(app, channel));
        Assert.Null(store.OpenAppUpdate(app, channel));
    }

    [Fact]
    public void StudentSignatureCannotAuthorizeTutorEvenWhenBinaryIsRenamed()
    {
        Write("release", "tutor", Manifest with { Filename = "KIBERoneTutor.exe" });
        Assert.Null(store.GetAppUpdate("tutor", "release"));
        Assert.Null(store.OpenAppUpdate("tutor", "release"));
        var source = Path.Combine(root, "student.exe");
        File.WriteAllBytes(source, Binary);
        Assert.Throws<InvalidOperationException>(() => store.PublishAppUpdate("tutor", source, Manifest.Version, Signature));
    }

    [Fact]
    public void CorruptManifestAndTamperedBinaryAreNotServed()
    {
        Write("release", "student", Manifest);
        File.WriteAllBytes(Path.Combine(store.UpdatesDirectory, "release", Manifest.Filename), Encoding.UTF8.GetBytes("changed-binary"));
        Assert.Null(store.GetAppUpdate("student", "release"));
        Assert.Null(store.OpenAppUpdate("student", "release"));
        File.WriteAllText(Path.Combine(store.UpdatesDirectory, "release", "student_manifest.json"), "{");
        Assert.Null(store.GetAppUpdate("student", "release"));
    }

    [Theory]
    [InlineData("unknown", "release")]
    [InlineData("student", "test")]
    [InlineData("tutor", "../release")]
    [InlineData("../student", "beta")]
    [InlineData("Student", "release")]
    public async Task GenericStoreAndClientRejectUnknownSelectors(string app, string channel)
    {
        Assert.Throws<ArgumentException>(() => store.GetAppUpdate(app, channel));
        Assert.Throws<ArgumentException>(() => store.OpenAppUpdate(app, channel));
        var client = new ClassroomHubClient("http://127.0.0.1:1/");
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAppUpdateAsync(app, channel));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadAppUpdateFileAsync(app, channel));
    }

    [Fact]
    public void LegacyPublisherWritesReleaseChannel()
    {
        var source = Path.Combine(root, "source.exe");
        File.WriteAllBytes(source, Binary);
        var published = store.PublishStudentUpdate(source, Manifest.Version, Signature);
        Assert.True(File.Exists(Path.Combine(store.UpdatesDirectory, "release", "student_manifest.json")));
        Assert.False(File.Exists(Path.Combine(store.UpdatesDirectory, "student_manifest.json")));
        Assert.Equal($"KIBERoneStudent-{Manifest.Sha256.ToLowerInvariant()}.exe", published.Filename);
        Assert.Equal(Manifest.Version, store.GetAppUpdate("student", "release")!.Version);
    }

    [Fact]
    public void IdenticalPublicationIsIdempotentAcrossStoreRestart()
    {
        var source = Path.Combine(root, "source.exe");
        File.WriteAllBytes(source, Binary);
        var first = store.PublishAppUpdate("student", source, Manifest.Version, Signature);
        var restarted = new ClassroomHubStore(root, []);
        Assert.Equal(first, restarted.PublishStudentUpdate(source, Manifest.Version, Signature));
        var directory = Path.Combine(store.UpdatesDirectory, "release");
        Assert.Single(Directory.EnumerateFiles(directory, "*.exe"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(directory, ".versions", "student"), "*.json"));
    }

    [Theory]
    [InlineData("9.1.0", true)]
    [InlineData("9.2.0", false)]
    public void PersistentPublicationHistoryRejectsVersionReuseAndDowngrade(string recordedVersion, bool differentHash)
    {
        var source = Path.Combine(root, "source.exe");
        File.WriteAllBytes(source, Binary);
        var directory = Path.Combine(store.UpdatesDirectory, "release");
        var history = Path.Combine(directory, ".versions", "student");
        Directory.CreateDirectory(history);
        // Seed durable publication policy state, while the candidate always uses
        // the real valid signed fixture. Policy must survive a missing current pointer.
        var record = Manifest with { Version = recordedVersion, Sha256 = differentHash ? new string('0', 64) : Manifest.Sha256 };
        var recordPath = Path.Combine(history, recordedVersion + ".json");
        var recordJson = JsonSerializer.Serialize(record, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        File.WriteAllText(recordPath, recordJson);
        var error = Assert.Throws<InvalidOperationException>(() => store.PublishStudentUpdate(source, Manifest.Version, Signature));
        Assert.Contains(differentHash ? "версию нельзя" : "старой версии", error.Message);
        Assert.Equal(recordJson, File.ReadAllText(recordPath));
        Assert.False(File.Exists(Path.Combine(directory, "student_manifest.json")));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.exe"));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public void PinnedArchiveServesOriginalArtifactAfterCurrentPointerChanges()
    {
        var source = Path.Combine(root, "source.exe");
        File.WriteAllBytes(source, Binary);
        var published = store.PublishStudentUpdate(source, Manifest.Version, Signature);
        Write("release", "student", Manifest with { Version = "9.2.0" });
        using var pinned = store.OpenAppUpdate("student", "release", published.Version, published.Sha256);
        Assert.NotNull(pinned);
        using var memory = new MemoryStream();
        pinned.CopyTo(memory);
        Assert.Equal(Binary, memory.ToArray());
        Assert.Null(store.OpenAppUpdate("student", "release", published.Version, new string('0', 64)));
    }

    [Fact]
    public async Task ApiAndClientServeReleaseAndKeepBothAppChannelRoutesIsolated()
    {
        Write("release", "student", Manifest);
        await using var host = CreateHost();
        ClassroomHubApi.Map(host, store);
        await host.StartAsync();
        try
        {
            var address = Address(host);
            var client = new ClassroomHubClient(address);
            Assert.Equal("9.1.0", (await client.GetAppUpdateAsync("student", "release"))!.Version);
            Assert.Equal(Binary, await client.DownloadAppUpdateFileAsync("student", "release"));
            Assert.Equal("9.1.0", (await client.GetStudentUpdateAsync())!.Version);
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            foreach (var app in new[] { "student", "tutor" })
                foreach (var channel in new[] { "release", "beta" })
                {
                    if (app == "student" && channel == "release") continue;
                    Assert.Null(await client.GetAppUpdateAsync(app, channel));
                    using var metadata = await http.GetAsync($"api/update/{app}?channel={channel}");
                    using var binary = await http.GetAsync($"api/update/{app}/file?channel={channel}");
                    Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
                    Assert.Equal(HttpStatusCode.NotFound, binary.StatusCode);
                }
            foreach (var suffix in new[] { "", "/file" })
            {
                using var unknownApp = await http.GetAsync($"api/update/unknown{suffix}?channel=release");
                using var unknownChannel = await http.GetAsync($"api/update/tutor{suffix}?channel=test");
                Assert.Equal(HttpStatusCode.BadRequest, unknownApp.StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, unknownChannel.StatusCode);
            }
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "api/update/student/file?channel=release");
            rangeRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
            using var range = await http.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
            Assert.Equal(Binary[..4], await range.Content.ReadAsByteArrayAsync());
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task ClientRejectsWrongChannelProductAndChangedDownloadFromServer()
    {
        await using var host = CreateHost();
        host.MapGet("/api/update/student", () => Microsoft.AspNetCore.Http.Results.Json(Manifest));
        host.MapGet("/api/update/tutor", () => Microsoft.AspNetCore.Http.Results.Json(Manifest));
        host.MapGet("/api/update/student/file", () => Microsoft.AspNetCore.Http.Results.Bytes(Encoding.UTF8.GetBytes("tampered")));
        await host.StartAsync();
        try
        {
            var client = new ClassroomHubClient(Address(host));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAppUpdateAsync("student", "beta"));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAppUpdateAsync("tutor", "release"));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAppUpdateFileAsync("student", "release"));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task PinnedDownloadRejectsChangedVersionOrHashAndMalformedPins()
    {
        Write("release", "student", Manifest);
        await using var host = CreateHost();
        ClassroomHubApi.Map(host, store);
        await host.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Address(host)) };
            var query = $"api/update/student/file?channel=release&version={Manifest.Version}&sha256={Manifest.Sha256}";
            using var correct = await http.GetAsync(query);
            Assert.Equal(HttpStatusCode.OK, correct.StatusCode);
            Assert.Equal(Binary, await correct.Content.ReadAsByteArrayAsync());
            using var wrongVersion = await http.GetAsync(query.Replace("version=9.1.0", "version=9.0.0", StringComparison.Ordinal));
            using var wrongHash = await http.GetAsync($"api/update/student/file?channel=release&version=9.1.0&sha256={new string('0', 64)}");
            using var missingHash = await http.GetAsync("api/update/student/file?channel=release&version=9.1.0");
            using var malformedHash = await http.GetAsync("api/update/student/file?channel=release&version=9.1.0&sha256=../file");
            Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, wrongHash.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, missingHash.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, malformedHash.StatusCode);
            Write("release", "student", Manifest with { Version = "9.2.0" });
            using var changed = await http.GetAsync(query);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task ExpectedManifestDownloadPinsRequestWithoutRefetchingMetadata()
    {
        var metadataRequests = 0;
        string? version = null;
        string? hash = null;
        await using var host = CreateHost();
        host.MapGet("/api/update/student", () =>
        {
            metadataRequests++;
            return Microsoft.AspNetCore.Http.Results.StatusCode(500);
        });
        host.MapGet("/api/update/student/file", (Microsoft.AspNetCore.Http.HttpRequest request) =>
        {
            version = request.Query["version"].ToString();
            hash = request.Query["sha256"].ToString();
            return Microsoft.AspNetCore.Http.Results.Bytes(Binary);
        });
        await host.StartAsync();
        try
        {
            var client = new ClassroomHubClient(Address(host));
            Assert.Equal(Binary, await client.DownloadAppUpdateFileAsync("student", "release", Manifest));
            Assert.Equal(0, metadataRequests);
            Assert.Equal(Manifest.Version, version);
            Assert.Equal(Manifest.Sha256, hash);
        }
        finally { await host.StopAsync(); }
    }

    private void Write(string channel, string app, AppUpdateManifest manifest)
    {
        var directory = Path.Combine(store.UpdatesDirectory, channel);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, manifest.Filename), Binary);
        File.WriteAllText(Path.Combine(directory, app + "_manifest.json"), JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
    }

    private static WebApplication CreateHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    private static string Address(WebApplication host) => host.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";

    public void Dispose() => Directory.Delete(root, true);
}
