using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public static class StarterPackRules
{
    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx"
    };

    public static bool IsInstallerFile(string name) =>
        InstallerExtensions.Contains(Path.GetExtension(name));

    public static bool HasTopLevelInstaller(string directory) =>
        FindTopLevelInstallers(directory).Count > 0;

    public static IReadOnlyList<string> FindTopLevelInstallers(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory)
            .Where(path => IsInstallerFile(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public static class ClassroomSoftwarePush
{
    private const long MaxDownloadBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<CommandExecutionResult> InstallStarterPackAsync(
        HttpClient http,
        string destinationRoot,
        string statePath,
        bool runInstallers,
        Func<string, CommandExecutionResult>? launchInstaller,
        Action<string>? progress,
        CancellationToken ct)
    {
        var items = await http.GetFromJsonAsync<List<DistributedAsset>>("/starter-pack", Json, ct) ?? [];
        if (items.Count == 0)
            return new CommandExecutionResult(false, "Тьютор ещё не собрал стартовый пакет.");

        Directory.CreateDirectory(destinationRoot);
        var applied = LoadState(statePath);
        var launched = 0;
        var downloaded = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name != Path.GetFileName(item.Name))
                continue;
            progress?.Invoke($"Скачиваем «{item.Name}»…");
            var target = Path.Combine(destinationRoot, item.Name);
            if (!string.IsNullOrWhiteSpace(item.Sha256)
                && applied.TryGetValue(item.Name, out var previous)
                && previous.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)
                && Path.Exists(target))
                continue;

            await DownloadAssetAsync(http, item, destinationRoot, ct);
            downloaded++;
            if (!string.IsNullOrWhiteSpace(item.Sha256))
                applied[item.Name] = item.Sha256;

            if (!runInstallers) continue;
            var folder = item.Kind == "folder" ? target : Path.GetDirectoryName(target)!;
            foreach (var installer in item.Kind == "file" && StarterPackRules.IsInstallerFile(item.Name)
                         ? [target]
                         : StarterPackRules.FindTopLevelInstallers(folder))
            {
                if (launchInstaller is null) continue;
                var result = launchInstaller(installer);
                if (!result.Succeeded)
                    return result;
                launched++;
            }
        }

        SaveState(statePath, applied);
        if (downloaded == 0 && launched == 0)
            return CommandExecutionResult.Success;
        return CommandExecutionResult.Success;
    }

    public static async Task<string> DownloadWallpaperAsync(HttpClient http, string destinationDirectory, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDirectory);
        using var response = await http.GetAsync("/wallpaper", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("Тьютор ещё не выбрал обои.");
        response.EnsureSuccessStatusCode();
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "desktop.jpg";
        fileName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "desktop.jpg";
        var destination = Path.Combine(destinationDirectory, fileName);
        var temporary = destination + ".tmp";
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > 12L * 1024 * 1024) throw new InvalidOperationException("Файл обоев слишком большой.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        File.Move(temporary, destination, true);
        return destination;
    }

    private static async Task DownloadAssetAsync(HttpClient http, DistributedAsset item, string destinationRoot, CancellationToken ct)
    {
        using var response = await http.GetAsync(
            "/starter-pack/file?name=" + Uri.EscapeDataString(item.Name),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? item.Name;
        fileName = Path.GetFileName(fileName.Trim('"'));
        var isZip = item.Kind == "folder"
            || string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);
        var temporary = Path.Combine(destinationRoot, $".download-{Guid.NewGuid():N}");
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes) throw new InvalidOperationException("Файл стартового пакета больше 1 ГБ.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            var target = Path.Combine(destinationRoot, item.Name);
            if (isZip && item.Kind == "folder")
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(temporary, target, true);
            }
            else
            {
                Directory.CreateDirectory(destinationRoot);
                File.Move(temporary, target, true);
                temporary = string.Empty;
            }
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporary) && File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static Dictionary<string, string> LoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveState(string path, Dictionary<string, string> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, Json));
    }
}
