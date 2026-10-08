using System.Diagnostics;
using Kiberone.Core;
using Microsoft.Win32;

namespace Kiberone.Infrastructure;

public static class InstalledApplicationCatalog
{
    public static IReadOnlyList<InstalledApplication> Read()
    {
        var result = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            path = path.Trim().Trim('"');
            if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var executable = Path.GetFileName(path).ToLowerInvariant();
                var name = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
                result.TryAdd(executable, new InstalledApplication(executable,
                    string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var apps = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (apps is not null)
                    foreach (var keyName in apps.GetSubKeyNames())
                    {
                        using var key = apps.OpenSubKey(keyName);
                        if (key?.GetValue(null) is string path) Add(path);
                    }
                using var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key?.GetValue("DisplayIcon") is string icon)
                        Add(icon.StartsWith('"') ? icon.Split('"').ElementAtOrDefault(1) ?? "" : icon.Split(',')[0]);
                    if (key?.GetValue("InstallLocation") is string folder && Directory.Exists(folder))
                    {
                        try { foreach (var exe in Directory.EnumerateFiles(folder, "*.exe").Take(100)) Add(exe); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var folder in new[] { "/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var app in Directory.EnumerateDirectories(folder, "*.app"))
                {
                    var name = Path.GetFileNameWithoutExtension(app);
                    // Policies target Windows students, using their executable names.
                    var executable = name switch
                    {
                        "Google Chrome" => "chrome.exe", "Microsoft Edge" => "msedge.exe",
                        "Visual Studio Code" => "code.exe", "GDevelop 5" => "gdevelop.exe",
                        _ => name.Replace(" ", "").ToLowerInvariant() + ".exe"
                    };
                    result.TryAdd(executable, new InstalledApplication(executable, name));
                }
            }
        }
        return result.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
