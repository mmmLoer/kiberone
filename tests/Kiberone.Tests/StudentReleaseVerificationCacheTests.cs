using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class StudentReleaseVerificationCacheTests : IDisposable
{
    private const string Signature = "Gh2/yvyfllQPb+uw3Vs6kYE7l3VYaQIEStWY7JebgE6gQvF+CpoQ38GPZxhWiTe7gVVbI2JJCrgZ96+Ibq7wgjTGHhXqYEC7jgUx5TEaevb7aV0pBvECNWwgSQ1MsHw9DMImp1zYYadEWrKVtn9sMqInx2A486VHKEW46BL46KCIKAIuyRkGAZ1AJe1iZ6jANAJcXWE7XmogQm76lxH+t/re+C8bvC90rt/bU9J0sUjQIk3ZBAJP60l+XTXqfmXsncdNHmBxBqc93nWNy1JINodcRVWHaqwiP6Z9TTiR2+qVtuLRKw/3jkQ1ABe7y+iefq0hr82d4xRmkL4acBZDLFlc9BA9uLjAR65nIjITCA+AZOhJM2MqlQCXL0o700Nc2FFiLGimtWSOeMUvwMpSC0fXybUV1nRfV/8McyHjfQJ7D+v94Qw15zEzc4Zv+1ztaMw1WaTxBfgfw+TQir/qOxyM5qIBsCXVIY1J98fTY01oXi7kel5oQEEhsS8pRbER";
    private static readonly byte[] Binary = Encoding.UTF8.GetBytes("student-binary");
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly string root = Path.Combine(Path.GetTempPath(), "kiberone-release-cache-" + Guid.NewGuid().ToString("N"));
    private readonly AssetDistributionService assets;
    private readonly string executablePath;
    private readonly string manifestPath;
    private readonly AppUpdateManifest release;

    public StudentReleaseVerificationCacheTests()
    {
        var bundle = Path.Combine(root, "updates", "release");
        Directory.CreateDirectory(bundle);
        executablePath = Path.Combine(bundle, "KIBERoneStudent.exe");
        manifestPath = Path.Combine(bundle, "student_manifest.json");
        release = new AppUpdateManifest("9.1.0", "KIBERoneStudent.exe", Binary.LongLength,
            Convert.ToHexString(SHA256.HashData(Binary)), DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Signature);
        File.WriteAllBytes(executablePath, Binary);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(release, Json));
        assets = new AssetDistributionService(root, root);
    }

    [Fact]
    public void OlderClientsShareOneVerificationAcrossWarmHeartbeats()
    {
        for (var client = 0; client < 20; client++)
            Assert.Equal(release.Version, assets.GetUpdateFor("9.0.0")!.Version);
        Assert.Equal(1L, VerificationCount);
        Assert.NotNull(assets.GetStudentRelease("release"));
        Assert.Equal(1L, VerificationCount);
    }

    [Theory]
    [InlineData("9.1.0")]
    [InlineData("9.2.0")]
    [InlineData("9.0.0b")]
    [InlineData("invalid")]
    public void CurrentNewerOrOtherChannelClientsNeverHashExecutable(string currentVersion)
    {
        for (var request = 0; request < 5; request++) Assert.Null(assets.GetUpdateFor(currentVersion));
        Assert.Equal(0L, VerificationCount);
    }

    [Fact]
    public void ManifestContentsInvalidateCacheEvenWhenMetadataIsUnchanged()
    {
        var original = File.ReadAllText(manifestPath);
        // Use a leading/trailing whitespace swap for a stable metadata fingerprint.
        File.WriteAllText(manifestPath, " " + original);
        Assert.NotNull(assets.GetUpdateFor("9.0.0"));
        Assert.Equal(1L, VerificationCount);
        var timestamp = File.GetLastWriteTimeUtc(manifestPath);
        var length = new FileInfo(manifestPath).Length;
        File.WriteAllText(manifestPath, original + " ");
        File.SetLastWriteTimeUtc(manifestPath, timestamp);
        Assert.Equal(length, new FileInfo(manifestPath).Length);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(manifestPath));
        Assert.NotNull(assets.GetUpdateFor("9.0.0"));
        Assert.Equal(2L, VerificationCount);
    }

    [Fact]
    public void ExecutableTimestampChangeInvalidatesWarmVerification()
    {
        Assert.NotNull(assets.GetUpdateFor("9.0.0"));
        File.SetLastWriteTimeUtc(executablePath, File.GetLastWriteTimeUtc(executablePath).AddMinutes(1));
        Assert.NotNull(assets.GetUpdateFor("9.0.0"));
        Assert.Equal(2L, VerificationCount);
    }

    [Fact]
    public void DownloadsAlwaysHashOnceAndReturnRewoundFileHandle()
    {
        using (var cold = assets.OpenStudentUpdate("release", release.Version))
        {
            Assert.IsType<FileStream>(cold);
            Assert.Equal(1L, VerificationCount);
            Assert.Equal(0L, cold!.Position);
            using var copy = new MemoryStream();
            cold.CopyTo(copy);
            Assert.Equal(Binary, copy.ToArray());
        }
        Assert.NotNull(assets.GetUpdateFor("9.0.0"));
        var count = VerificationCount;
        using var warm = assets.OpenStudentUpdate("release", release.Version);
        Assert.NotNull(warm);
        Assert.Equal(count + 1, VerificationCount);
        Assert.Equal(0L, warm!.Position);
    }

    [Fact]
    public void DownloadRejectsTamperingWithStableSizeAndTimestampAndEvictsCachedVerification()
    {
        Assert.NotNull(assets.GetUpdateFor("9.0.0"));
        var timestamp = File.GetLastWriteTimeUtc(executablePath);
        var tampered = Binary.ToArray();
        tampered[0] ^= 1;
        File.WriteAllBytes(executablePath, tampered);
        File.SetLastWriteTimeUtc(executablePath, timestamp);
        Assert.Equal(Binary.LongLength, new FileInfo(executablePath).Length);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(executablePath));
        Assert.Null(assets.OpenStudentUpdate("release", release.Version));
        Assert.Equal(2L, VerificationCount);
        Assert.Null(assets.GetUpdateFor("9.0.0"));
        Assert.Equal(3L, VerificationCount);
        // A failed verification must also release its file handle.
        File.WriteAllBytes(executablePath, Binary);
    }

    [Fact]
    public void ImportInvalidatesPriorVerifiedEntries()
    {
        Assert.NotNull(assets.GetUpdateFor("9.0.0"));
        Assert.Equal(1L, VerificationCount);
        var cache = (System.Collections.IDictionary)typeof(AssetDistributionService)
            .GetField("verifiedStudentReleases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(assets)!;
        Assert.Single(cache);
        assets.ImportStudentRelease(release, Binary);
        Assert.Empty(cache);
        // The bundle stays unchanged; it must nevertheless be verified again after import.
        Assert.NotNull(assets.GetUpdateFor("9.0.0"));
        Assert.Equal(2L, VerificationCount);
    }

    private long VerificationCount => (long)typeof(AssetDistributionService)
        .GetField("studentFileVerificationCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(assets)!;

    public void Dispose() => Directory.Delete(root, true);
}
