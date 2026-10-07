using Microsoft.Win32;

namespace Kiberone.Vpn;

public sealed class VpnOptions
{
    public static string ManagedVpnDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KIBERone", "Student", "vpn");

    public static string ManagedConfigPath { get; } = Path.Combine(ManagedVpnDirectory, "peer.conf");

    public string ConfigPath { get; set; } = ManagedConfigPath;

    public bool RequireBridge { get; set; } = true;

    public string DevConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KIBERone", "Student", "vpn", "peer.conf");

    public string InstallTargetPath => ManagedConfigPath;

    public string ResolvedConfigPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ConfigPath) && File.Exists(ConfigPath))
                return Path.GetFullPath(ConfigPath);

            if (File.Exists(ManagedConfigPath))
                return Path.GetFullPath(ManagedConfigPath);

            if (File.Exists(DevConfigPath))
                return Path.GetFullPath(DevConfigPath);

            return Path.GetFullPath(string.IsNullOrWhiteSpace(ConfigPath) ? ManagedConfigPath : ConfigPath);
        }
    }

    public static bool IsAllowedBridgeConfigPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(full), ".conf", StringComparison.OrdinalIgnoreCase))
            return false;

        var root = Path.GetFullPath(ManagedVpnDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAllowedUpdateSourcePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                return false;
            if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!Path.GetFileName(full).StartsWith("student-", StringComparison.OrdinalIgnoreCase))
                return false;

            var localRoot = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KIBERone Classroom", "updates"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (full.StartsWith(localRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            // The bridge runs as SYSTEM: its LocalApplicationData is not the student's.
            using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profiles is null) return false;
            foreach (var sid in profiles.GetSubKeyNames())
            {
                using var key = profiles.OpenSubKey(sid);
                var profile = Environment.ExpandEnvironmentVariables(key?.GetValue("ProfileImagePath") as string ?? string.Empty);
                if (string.IsNullOrWhiteSpace(profile)) continue;
                var updates = Path.GetFullPath(Path.Combine(profile, "AppData", "Local", "KIBERone Classroom", "updates"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(updates, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAllowedUpdateTargetPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
                return false;

            var fileName = Path.GetFileNameWithoutExtension(full);
            if (!fileName.Equals("Kiberone.Student", StringComparison.OrdinalIgnoreCase)
                && !fileName.Equals("KIBERoneStudent", StringComparison.OrdinalIgnoreCase))
                return false;

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var allowedRoots = new[]
            {
                Path.Combine(programFiles, "KIBERone", "Student"),
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            };

            return allowedRoots.Any(root =>
            {
                var normalized = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return full.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch
        {
            return false;
        }
    }
}
