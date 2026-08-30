using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed record DistributedAsset(string Name, long Size, string Kind);
public sealed record DistributedAssetDownload(Stream Content, string FileName, string ContentType);
public sealed record StudentReleaseManifest(string Version, string Filename, long Size, string Sha256, DateTimeOffset PublishedAt);

public sealed class AssetDistributionService
{
    private const long MaxScreenBytes = 2L * 1024 * 1024;
    private readonly string updatesRoot;
    private readonly string deployRoot;
    private readonly string starterRoot;
    private readonly string screensRoot;

    public AssetDistributionService(string applicationRoot, string dataRoot)
    {
        updatesRoot = Path.Combine(applicationRoot, "updates");
        deployRoot = Path.Combine(applicationRoot, "deploy");
        starterRoot = Path.Combine(applicationRoot, "starter-pack");
        screensRoot = Path.Combine(dataRoot, "screens");
        Directory.CreateDirectory(screensRoot);
    }

    public StudentReleaseManifest? GetStudentRelease()
    {
        var manifestPath = Path.Combine(updatesRoot, "student_manifest.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize<StudentReleaseManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            if (manifest is null || !IsSafeName(manifest.Filename)) return null;
            var file = Path.Combine(updatesRoot, manifest.Filename);
            if (!File.Exists(file) || new FileInfo(file).Length != manifest.Size) return null;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            return hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase) ? manifest : null;
        }
        catch { return null; }
    }

    public StudentUpdateInfo? GetUpdateFor(string currentVersion)
    {
        var release = GetStudentRelease();
        if (release is null || !Version.TryParse(release.Version, out var available)) return null;
        return !Version.TryParse(currentVersion, out var current) || available > current
            ? new StudentUpdateInfo(release.Version, release.Sha256, release.Size)
            : null;
    }

    public Stream? OpenStudentUpdate()
    {
        var release = GetStudentRelease();
        if (release is null) return null;
        return new FileStream(Path.Combine(updatesRoot, release.Filename), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public IReadOnlyList<DistributedAsset> ListStarterPack() => ListAssets(starterRoot);
    public DistributedAssetDownload? OpenStarterAsset(string name)
    {
        if (!IsSafeName(name)) throw new LessonValidationException(["Некорректное имя стартового пакета."]);
        var path = Path.Combine(starterRoot, name);
        if (File.Exists(path)) return new DistributedAssetDownload(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), name, "application/octet-stream");
        if (!Directory.Exists(path)) return null;
        var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                total += info.Length;
                if (total > 200L * 1024 * 1024) throw new LessonValidationException(["Папка стартового пакета превышает 200 МБ."]);
                var entry = archive.CreateEntry(Path.GetRelativePath(path, file).Replace('\\', '/'), CompressionLevel.Fastest);
                using var source = File.OpenRead(file);
                using var destination = entry.Open();
                source.CopyTo(destination);
            }
        }
        memory.Position = 0;
        return new DistributedAssetDownload(memory, name + ".zip", "application/zip");
    }
    public Stream? OpenDeployAsset(string name) => OpenAsset(deployRoot, name);

    public async Task SaveScreenAsync(string clientId, Stream content, CancellationToken ct = default)
    {
        var destination = Path.Combine(screensRoot, SafeKey(clientId) + ".jpg");
        var temporary = destination + ".tmp";
        try
        {
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxScreenBytes) throw new LessonValidationException(["Превью экрана превышает 2 МБ."]);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            await output.FlushAsync(ct);
            output.Close();
            var header = new byte[2];
            await using (var verify = File.OpenRead(temporary)) _ = await verify.ReadAsync(header, ct);
            if (header[0] != 0xFF || header[1] != 0xD8) throw new LessonValidationException(["Превью должно быть JPEG-файлом."]);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Stream? OpenScreen(string clientId)
    {
        var path = Path.Combine(screensRoot, SafeKey(clientId) + ".jpg");
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
    }

    private static IReadOnlyList<DistributedAsset> ListAssets(string root) => Directory.Exists(root)
        ? Directory.EnumerateFileSystemEntries(root).Select(path => new DistributedAsset(Path.GetFileName(path), File.Exists(path) ? new FileInfo(path).Length : 0, File.Exists(path) ? "file" : "folder")).OrderBy(x => x.Name).ToList()
        : [];
    private static Stream? OpenAsset(string root, string name)
    {
        if (!IsSafeName(name)) throw new LessonValidationException(["Некорректное имя deploy-файла."]);
        var path = Path.Combine(root, name);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }
    private static bool IsSafeName(string name) => !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    private static string SafeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new LessonValidationException(["client_id обязателен."]);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    }
}
