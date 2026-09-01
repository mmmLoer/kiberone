using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class AssetDistributionServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"kiberone-assets-{Guid.NewGuid():N}");
    private readonly AssetDistributionService service;

    public AssetDistributionServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "app", "updates"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        service = new AssetDistributionService(Path.Combine(root, "app"), Path.Combine(root, "data"));
    }

    [Fact]
    public void ValidManifest_OffersOnlyNewerVerifiedRelease()
    {
        var bytes = Encoding.UTF8.GetBytes("student-binary");
        var updatePath = Path.Combine(root, "app", "updates", "KIBERoneStudent.exe");
        File.WriteAllBytes(updatePath, bytes);
        var manifest = new
        {
            version = "9.1.0", filename = "KIBERoneStudent.exe", size = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)), published_at = DateTimeOffset.UtcNow
        };
        File.WriteAllText(Path.Combine(root, "app", "updates", "student_manifest.json"), JsonSerializer.Serialize(manifest));

        Assert.Equal("9.1.0", service.GetStudentRelease()?.Version);
        Assert.NotNull(service.GetUpdateFor("9.0.0"));
        Assert.Null(service.GetUpdateFor("9.1.0"));
    }

    [Fact]
    public void TamperedUpdate_IsNotOffered()
    {
        File.WriteAllText(Path.Combine(root, "app", "updates", "KIBERoneStudent.exe"), "tampered");
        File.WriteAllText(Path.Combine(root, "app", "updates", "student_manifest.json"),
            "{\"version\":\"9.1.0\",\"filename\":\"KIBERoneStudent.exe\",\"size\":8,\"sha256\":\"BAD\",\"published_at\":\"2026-01-01T00:00:00Z\"}");

        Assert.Null(service.GetStudentRelease());
    }

    [Fact]
    public async Task Screen_MustBeJpegAndWithinLimit()
    {
        await Assert.ThrowsAsync<LessonValidationException>(async () =>
        {
            await using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("not jpeg"));
            await service.SaveScreenAsync("pc-1", invalid);
        });
        await using var jpeg = new MemoryStream([0xFF, 0xD8, 0xFF, 0xD9]);
        await service.SaveScreenAsync("pc-1", jpeg);
        await using var stored = service.OpenScreen("pc-1");
        Assert.NotNull(stored);
        Assert.Equal(4, stored.Length);
    }

    [Fact]
    public void StarterFolder_IsDeliveredAsZip()
    {
        var folder = Path.Combine(root, "app", "starter-pack", "Python Start");
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        File.WriteAllText(Path.Combine(folder, "src", "main.py"), "print('KIBERone')");

        var download = service.OpenStarterAsset("Python Start")!;
        using var content = download.Content;
        using var archive = new System.IO.Compression.ZipArchive(content, System.IO.Compression.ZipArchiveMode.Read);

        Assert.Equal("Python Start.zip", download.FileName);
        Assert.Contains(archive.Entries, x => x.FullName == "src/main.py");
    }

    [Fact]
    public void UnsafeDeployName_IsRejected()
    {
        Assert.Throws<LessonValidationException>(() => service.OpenDeployAsset("../secret.exe"));
        Assert.Throws<LessonValidationException>(() => service.OpenStarterAsset("../pack"));
        Assert.Throws<LessonValidationException>(() => service.OpenStarterAsset(""));
        Assert.Null(service.OpenStarterAsset("missing-pack"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
