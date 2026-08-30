using Kiberone.Vpn.WireGuard;

namespace Kiberone.Vpn;

public static class VpnServiceEntry
{
    public static bool TryRunService(string[] args, out int exitCode)
    {
        VpnNativeBootstrap.Initialize();

        if (args.Any(arg => string.Equals(arg, "/vpn-bridge", StringComparison.OrdinalIgnoreCase)))
        {
            VpnBridgeHost.Run(args);
            exitCode = 0;
            return true;
        }

        if (args is ["/service", var configFile, ..])
        {
            try
            {
                var ok = TunnelService.Run(configFile);
                if (!ok)
                    VpnLog.Error("service", $"WireGuardTunnelService returned false for {configFile}");
                exitCode = ok ? 0 : 1;
            }
            catch (Exception error)
            {
                VpnLog.Error("service", $"WireGuard /service failed for {configFile}", error);
                exitCode = 1;
            }
            return true;
        }

        exitCode = 0;
        return false;
    }
}
