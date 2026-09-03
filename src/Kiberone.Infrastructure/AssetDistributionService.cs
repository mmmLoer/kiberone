using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed record DistributedAsset(string Name, long Size, string Kind, string Sha256 = "", bool RunsInstaller = false);
public sealed record DistributedAssetDownload(Stream Content, string FileName, string ContentType);
public sealed record StudentReleaseManifest(string Version, string Filename, long Size, string Sha256, DateTimeOffset PublishedAt);

public sealed class AssetDistributionService
{
    private const long MaxScreenBytes = 2L * 1024 * 1024;
    private const long MaxStarterBytes = 1024L * 1024 * 1024;
    private const long MaxWallpaperBytes = 12L * 1024 * 1024;
    private readonly string updatesRoot;
    private readonly string deployRoot;
    private readonly string starterRoot;
    private readonly string wallpaperRoot;
    private readonly string screensRoot;

    public AssetDistributionService(string applicationRoot, string dataRoot)
    {
        updatesRoot = Path.Combine(applicationRoot, "updates");
        deployRoot = Path.Combine(applicationRoot, "deploy");
        var bundledStarter = Path.Combine(applicationRoot, "starter-pack");
        starterRoot = Path.Combine(dataRoot, "starter-pack");
        wallpaperRoot = Path.Combine(dataRoot, "wallpaper");
        screensRoot = Path.Combine(dataRoot, "screens");
        Directory.CreateDirectory(starterRoot);
        Directory.CreateDirectory(wallpaperRoot);
        Directory.CreateDirectory(screensRoot);
        if (Directory.Exists(bundledStarter) && !Directory.EnumerateFileSystemEntries(starterRoot).Any())
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(bundledStarter))
            {
                var name = Path.GetFileName(entry);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) continue;
                var target = Path.Combine(starterRoot, name);
                if (Directory.Exists(entry)) CopyDirectory(entry, target);
                else File.Copy(entry, target, true);
            }
        }
    }

    public string StarterPackFolder => starterRoot;

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

    public StudentReleaseManifest ImportStudentRelease(AppUpdateManifest remote, byte[] content)
    {
        if (content.Length == 0)
            throw new LessonValidationException(["Пустой файл обновления Student."]);
        if (!IsSafeName(remote.Filename))
            throw new LessonValidationException(["Некорректное имя файла обновления."]);
        var hash = Convert.ToHexString(SHA256.HashData(content));
        if (!hash.Equals(remote.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new LessonValidationException(["Хеш обновления Student не совпал."]);

        Directory.CreateDirectory(updatesRoot);
        var destination = Path.Combine(updatesRoot, remote.Filename);
        File.WriteAllBytes(destination, content);
        var stored = new StudentReleaseManifest(remote.Version, remote.Filename, content.Length, hash, remote.PublishedAt);
        File.WriteAllText(
            Path.Combine(updatesRoot, "student_manifest.json"),
            JsonSerializer.Serialize(stored, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true }));
        return stored;
    }

    public IReadOnlyList<DistributedAsset> ListStarterPack() => ListAssets(starterRoot);

    public void AddStarterFile(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new LessonValidationException(["Файл стартового пакета не найден."]);
        var info = new FileInfo(sourcePath);
        if (info.Length > MaxStarterBytes) throw new LessonValidationException(["Файл стартового пакета больше 1 ГБ."]);
        Directory.CreateDirectory(starterRoot);
        var destination = UniquePath(starterRoot, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destination);
    }

    public void AddStarterFolder(string sourcePath)
    {
        if (!Directory.Exists(sourcePath)) throw new LessonValidationException(["Папка стартового пакета не найдена."]);
        Directory.CreateDirectory(starterRoot);
        var destination = UniquePath(starterRoot, Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        CopyDirectory(sourcePath, destination);
    }

    public void RemoveStarterAsset(string name)
    {
        var path = ResolveStarterPath(name);
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
        else throw new KeyNotFoundException("Элемент стартового пакета не найден.");
    }

    public void SetWallpaper(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new LessonValidationException(["Файл обоев не найден."]);
        var info = new FileInfo(sourcePath);
        if (info.Length > MaxWallpaperBytes) throw new LessonValidationException(["Файл обоев больше 12 МБ."]);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp"))
            throw new LessonValidationException(["Обои: JPG, PNG или BMP."]);
        Directory.CreateDirectory(wallpaperRoot);
        foreach (var leftover in Directory.EnumerateFileSystemEntries(wallpaperRoot))
        {
            if (File.Exists(leftover)) File.Delete(leftover);
            else Directory.Delete(leftover, true);
        }
        File.Copy(sourcePath, Path.Combine(wallpaperRoot, "desktop" + extension.ToLowerInvariant()));
    }

    public DistributedAsset? GetWallpaper() => ListAssets(wallpaperRoot).FirstOrDefault();

    public DistributedAssetDownload? OpenWallpaper()
    {
        var wallpaper = GetWallpaper();
        if (wallpaper is null) return null;
        var path = Path.Combine(wallpaperRoot, wallpaper.Name);
        if (!File.Exists(path)) return null;
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
        return new DistributedAssetDownload(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), wallpaper.Name, type);
    }

    public DistributedAssetDownload? OpenStarterAsset(string name)
    {
        var path = ResolveStarterPath(name);
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
                if (total > MaxStarterBytes) throw new LessonValidationException(["Папка стартового пакета превышает 1 ГБ."]);
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

    private static IReadOnlyList<DistributedAsset> ListAssets(string root)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFileSystemEntries(root)
            .Where(path => !Path.GetFileName(path).StartsWith('.'))
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    return new DistributedAsset(name, info.Length, "file", HashFile(path), StarterPackRules.IsInstallerFile(name));
                }
                return new DistributedAsset(name, FolderSize(path), "folder", HashFolder(path), StarterPackRules.HasTopLevelInstaller(path));
            })
            .OrderBy(x => x.Name)
            .ToList();
    }
    private string ResolveStarterPath(string name)
    {
        if (!IsSafeName(name)) throw new LessonValidationException(["Некорректное имя стартового пакета."]);
        return Path.Combine(starterRoot, name);
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        long total = 0;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            total += info.Length;
            if (total > MaxStarterBytes) throw new LessonValidationException(["Папка стартового пакета превышает 1 ГБ."]);
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
    private static string UniquePath(string directory, string name)
    {
        var safe = SanitizeFileName(name);
        var destination = Path.Combine(directory, safe);
        if (!Path.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var extension = Path.GetExtension(safe);
        for (var index = 2; index < 100; index++)
        {
            destination = Path.Combine(directory, $"{stem} {index}{extension}");
            if (!Path.Exists(destination)) return destination;
        }
        throw new InvalidOperationException("Не удалось подобрать имя для файла пакета.");
    }
    private static string SanitizeFileName(string name)
    {
        var fileName = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('.'))
            throw new LessonValidationException(["Некорректное имя файла пакета."]);
        foreach (var character in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(character, '_');
        if (!IsSafeName(fileName)) throw new LessonValidationException(["Некорректное имя файла пакета."]);
        return fileName;
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static string HashFolder(string path)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            hasher.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file).Replace('\\', '/')));
            hasher.AppendData(BitConverter.GetBytes(info.Length));
            hasher.AppendData(BitConverter.GetBytes(info.LastWriteTimeUtc.Ticks));
        }
        return Convert.ToHexString(hasher.GetHashAndReset());
    }
    private static long FolderSize(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => new FileInfo(file))
            .Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0)
            .Sum(info => info.Length);
    private static Stream? OpenAsset(string root, string name)
    {
        if (!IsSafeName(name)) throw new LessonValidationException(["Некорректное имя deploy-файла."]);
        var path = Path.Combine(root, name);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }
    private static bool IsSafeName(string name) => !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.StartsWith('.');
    private static string SafeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new LessonValidationException(["client_id обязателен."]);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
    }
}
