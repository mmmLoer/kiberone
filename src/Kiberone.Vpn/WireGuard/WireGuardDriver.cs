using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kiberone.Vpn.WireGuard;

public static class WireGuardDriver
{
    [DllImport("wireguard.dll", EntryPoint = "WireGuardGetRunningDriverVersion", CallingConvention = CallingConvention.Winapi)]
    private static extern uint WireGuardGetRunningDriverVersion();

    public static uint? GetRunningVersion()
    {
        try
        {
            var version = WireGuardGetRunningDriverVersion();
            if (version == 0)
            {
                var error = Marshal.GetLastWin32Error();
                VpnLog.Warn("driver", $"WireGuard NT driver not loaded (error {error})");
                return null;
            }

            return version;
        }
        catch (Exception error)
        {
            VpnLog.Error("driver", "Failed to query WireGuard NT driver version", error);
            return null;
        }
    }

    public static void EnsureReady()
    {
        var version = GetRunningVersion();
        if (version is not null)
        {
            VpnLog.Info("driver", $"WireGuard NT driver version {version.Value}");
            return;
        }

        VpnLog.Info(
            "driver",
            "WireGuard NT driver not loaded yet; it will be installed on first tunnel start.");
    }
}
