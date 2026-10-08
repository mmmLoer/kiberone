using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

var hub = new ClassroomHubClient(args.Length > 0 ? args[0] : null);
var root = Path.Combine(Path.GetTempPath(), "kiberone-update-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var assets = new AssetDistributionService(root, root);
var results = new List<object>();
var channels = args.Length > 1 ? new[] { args[1] } : new[] { "release", "beta" };
if (channels.Any(x => x is not ("release" or "beta"))) throw new ArgumentException("Unknown channel.");
try
{
    foreach (var channel in channels)
    foreach (var app in new[] { "student", "tutor" })
    {
        var manifest = await hub.GetAppUpdateAsync(app, channel)
            ?? throw new InvalidOperationException($"Missing {app}/{channel} manifest.");
        var content = await hub.DownloadAppUpdateFileAsync(app, channel);
        if (!StudentUpdateSignature.VerifyApp(app, manifest.Version, manifest.Size, manifest.Sha256, manifest.Signature)
            || StudentUpdateSignature.VerifyApp(app == "tutor" ? "student" : "tutor", manifest.Version, manifest.Size, manifest.Sha256, manifest.Signature)
            || AppReleaseVersion.ChannelFor(manifest.Version) != channel
            || content.LongLength != manifest.Size
            || !Convert.ToHexString(SHA256.HashData(content)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid {app}/{channel} package.");
        var oldVersion = channel == "beta" ? "2.0.0b" : "2.0.0";
        var stagedPackage = await AppUpdateInstaller.StageAsync(app, manifest, content, oldVersion, root);
        var targetFolder = Path.Combine(root, app, channel);
        Directory.CreateDirectory(targetFolder);
        var target = Path.Combine(targetFolder, app == "tutor" ? "Kiberone.Tutor.exe" : "Kiberone.Student.exe");
        await File.WriteAllTextAsync(target, "old isolated update probe");
        await AppUpdateInstaller.ApplyVerifiedPackageAsync(app, manifest, oldVersion, stagedPackage, target);
        using (var installed = File.OpenRead(target))
            if (!Convert.ToHexString(SHA256.HashData(installed)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Portable update application corrupted the package.");
        if (await File.ReadAllTextAsync(target + ".previous") != "old isolated update probe")
            throw new InvalidDataException("Portable update lost the previous package.");
        if (app == "student")
        {
            assets.ImportStudentRelease(manifest, content);
            var current = channel == "beta" ? "2.0.0b" : "2.0.0";
            var update = assets.GetUpdateFor(current);
            if (update?.Version != manifest.Version || assets.GetUpdateFor(manifest.Version) is not null)
                throw new InvalidDataException("Student relay selection failed.");
            var counter = typeof(AssetDistributionService).GetField("studentFileVerificationCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var before = counter.GetValue(assets);
            for (var i = 0; i < 100; i++)
            {
                if (assets.GetUpdateFor(current)?.Version != manifest.Version || assets.GetUpdateFor(manifest.Version) is not null)
                    throw new InvalidDataException("Warm heartbeat selected the wrong update.");
            }
            if (!Equals(before, counter.GetValue(assets))) throw new InvalidDataException("Warm heartbeat rehashed the executable.");
            Console.WriteLine($"STUDENT_RELAY_WARM_PASS {channel} 200 checks without executable rehash");
            using var relayed = assets.OpenStudentUpdate(channel, manifest.Version)
                ?? throw new InvalidDataException("Student relay download missing.");
            if (!Convert.ToHexString(SHA256.HashData(relayed)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Student relay corrupted the package.");
            if (channel == BuildInfo.Channel && AppReleaseVersion.IsNewer(manifest.Version, BuildInfo.Version))
            {
                await using var agent = new StudentAgent("UPDATE-SMOKE", Path.Combine(root, "projects"));
                var ready = false;
                agent.UpdateRestartRequested += () => ready = true;
                using var http = new HttpClient(new PackageHandler(content)) { BaseAddress = new Uri("http://127.0.0.1/") };
                var stage = typeof(StudentAgent).GetMethod("StageUpdateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                await (Task)stage.Invoke(agent, [http, new StudentUpdateInfo(manifest.Version, manifest.Sha256, manifest.Size, manifest.Signature), CancellationToken.None])!;
                var staged = (string?)typeof(StudentAgent).GetField("stagedUpdatePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(agent);
                if (!ready || staged is null) throw new InvalidDataException("Production Student staging did not request restart.");
                try
                {
                    using var file = File.OpenRead(staged);
                    if (!Convert.ToHexString(SHA256.HashData(file)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Production Student staging corrupted the package.");
                }
                finally { File.Delete(staged); }
                Console.WriteLine($"STUDENT_STAGING_PASS {channel} {manifest.Version}");
            }
        }
        results.Add(new { App = app, Channel = channel, manifest.Version, manifest.Size, Signature = true, Hash = true, PortableApply = true });
    }
    if (channels.Length == 2 && (assets.GetUpdateFor("2.0.0")?.Version.EndsWith('b') != false
        || assets.GetUpdateFor("2.0.0b")?.Version.EndsWith('b') != true))
        throw new InvalidDataException("Mixed classroom channels leaked.");
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, Packages = results, StudentRelay = true, MixedChannels = channels.Length == 2 }));
}

finally
{
    // Only the unique directory created by this invocation is removed.
    Directory.Delete(root, true);
}

sealed class PackageHandler(byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
}
