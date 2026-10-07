using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Kiberone.Core;
using Microsoft.Win32;

namespace Kiberone.Student;

internal static class ApplicationInventory
{
    public static IReadOnlyList<InstalledApplication> Read()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var root = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key?.GetValue(null) is string value) paths.Add(value.Trim('"'));
            }
        }
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (process.MainWindowHandle != IntPtr.Zero && process.MainModule?.FileName is string path) paths.Add(path); }
                catch { }
            }
        }
        var result = new List<InstalledApplication>();
        foreach (var path in paths.Take(200))
        {
            if (!File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var name = string.IsNullOrWhiteSpace(info.FileDescription) ? Path.GetFileNameWithoutExtension(path) : info.FileDescription;
                string? icon = null;
                try
                {
                    using var extracted = Icon.ExtractAssociatedIcon(path);
                    if (extracted is not null)
                    {
                        using var bitmap = extracted.ToBitmap();
                        using var resized = new Bitmap(bitmap, 32, 32);
                        using var stream = new MemoryStream();
                        resized.Save(stream, ImageFormat.Png);
                        icon = Convert.ToBase64String(stream.ToArray());
                    }
                }
                catch { }
                result.Add(new InstalledApplication(Path.GetFileName(path).ToLowerInvariant(), name!, icon));
            }
            catch { }
        }
        return result.DistinctBy(x => x.Executable).OrderBy(x => x.Name).ToArray();
    }
}
