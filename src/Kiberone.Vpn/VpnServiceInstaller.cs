using System.Diagnostics;

namespace Kiberone.Vpn;

public static class VpnServiceInstaller
{
    public static string ServiceScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "service", "install-student-vpn-service.ps1");

    public static bool IsScriptAvailable => File.Exists(ServiceScriptPath);

    public static bool TryInstallInPlace(TimeSpan? timeout = null)
    {
        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var script = ServiceScriptPath;
        if (!File.Exists(script))
        {
            VpnLog.Error("installer", $"Service install script missing: {script}");
            return false;
        }

        if (!File.Exists(Path.Combine(installDir, "Kiberone.Student.exe")))
        {
            VpnLog.Error("installer", $"Student exe missing in {installDir}");
            return false;
        }

        try
        {
            VpnLog.Info("installer", $"Requesting admin install from {script}");
            var arguments =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -SourceDir \"{installDir}\" -InPlace";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                VpnLog.Warn("installer", "Admin install was cancelled or blocked.");
                return false;
            }

            timeout ??= TimeSpan.FromMinutes(3);
            if (!process.WaitForExit((int)timeout.Value.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored
                }

                VpnLog.Error("installer", "VPN service install timed out.");
                return false;
            }

            if (process.ExitCode != 0)
            {
                VpnLog.Error("installer", $"VPN service install exited with code {process.ExitCode}.");
                return false;
            }

            VpnLog.Info("installer", "VPN service install completed.");
            return true;
        }
        catch (Exception error)
        {
            VpnLog.Error("installer", "VPN service install failed", error);
            return false;
        }
    }
}
