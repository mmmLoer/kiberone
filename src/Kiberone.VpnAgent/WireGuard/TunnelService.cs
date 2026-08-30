/* Adapted from WireGuard embeddable-dll-service csharp sample (MIT). */

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kiberone.VpnAgent.WireGuard;

/// <summary>
/// Manages a WireGuard tunnel via official tunnel.dll (WireGuardTunnelService).
/// Service name: WireGuardTunnel${tunnelName} where tunnelName = conf file name without extension.
/// </summary>
internal static class TunnelService
{
    private const string DisplayPrefix = "KIBERone WireGuard";
    private const string Description = "KIBERone classroom WireGuard tunnel (embeddable-dll-service)";

    [DllImport("tunnel.dll", EntryPoint = "WireGuardTunnelService", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Run([MarshalAs(UnmanagedType.LPWStr)] string configFile);

    public static string TunnelNameFromConfig(string configFile) =>
        Path.GetFileNameWithoutExtension(configFile);

    public static string ServiceNameFromConfig(string configFile) =>
        $"WireGuardTunnel${TunnelNameFromConfig(configFile)}";

    public static void Connect(string configFile, bool ephemeral = true)
    {
        if (!File.Exists(configFile))
            throw new FileNotFoundException("WireGuard config not found.", configFile);

        var tunnelName = TunnelNameFromConfig(configFile);
        var shortName = ServiceNameFromConfig(configFile);
        var longName = $"{DisplayPrefix}: {tunnelName}";
        var exeName = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve agent executable path.");
        var pathAndArgs = $"\"{exeName}\" /service \"{configFile}\"";

        var scm = Win32.OpenSCManager(null, null, Win32.ScmAccessRights.AllAccess);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var existing = Win32.OpenService(scm, shortName, Win32.ServiceAccessRights.AllAccess);
            if (existing != IntPtr.Zero)
            {
                Win32.CloseServiceHandle(existing);
                Disconnect(configFile, waitForStop: true);
            }

            var service = Win32.CreateService(
                scm,
                shortName,
                longName,
                Win32.ServiceAccessRights.AllAccess,
                Win32.ServiceType.Win32OwnProcess,
                Win32.ServiceStartType.Demand,
                Win32.ServiceError.Normal,
                pathAndArgs,
                null,
                IntPtr.Zero,
                "Nsi\0TcpIp\0",
                null,
                null);

            if (service == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                var sidType = Win32.ServiceSidType.Unrestricted;
                if (!Win32.ChangeServiceConfig2(service, Win32.ServiceConfigType.SidInfo, ref sidType))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var description = new Win32.ServiceDescription { lpDescription = Description };
                if (!Win32.ChangeServiceConfig2(service, Win32.ServiceConfigType.Description, ref description))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (!Win32.StartService(service, 0, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                // Mark for deletion after stop — keeps SCM clean for daily connect/disconnect.
                if (ephemeral && !Win32.DeleteService(service))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Win32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }
    }

    public static void Disconnect(string configFile, bool waitForStop = true)
    {
        var shortName = ServiceNameFromConfig(configFile);
        var scm = Win32.OpenSCManager(null, null, Win32.ScmAccessRights.AllAccess);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var service = Win32.OpenService(scm, shortName, Win32.ServiceAccessRights.AllAccess);
            if (service == IntPtr.Zero)
                return;

            try
            {
                var serviceStatus = new Win32.ServiceStatus();
                Win32.ControlService(service, Win32.ServiceControl.Stop, serviceStatus);

                for (var i = 0; waitForStop && i < 60 && Win32.QueryServiceStatus(service, serviceStatus)
                     && serviceStatus.dwCurrentState != Win32.ServiceState.Stopped; ++i)
                {
                    Thread.Sleep(500);
                }

                // ERROR_SERVICE_MARKED_FOR_DELETE = 0x430 — already ephemeral
                if (!Win32.DeleteService(service) && Marshal.GetLastWin32Error() is not (0 or 0x00000430))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Win32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }
    }

    public static TunnelStatus GetStatus(string configFile)
    {
        var shortName = ServiceNameFromConfig(configFile);
        var scm = Win32.OpenSCManager(null, null, Win32.ScmAccessRights.Connect);
        if (scm == IntPtr.Zero)
            return new TunnelStatus(false, "scm_unavailable", shortName, configFile);

        try
        {
            var service = Win32.OpenService(scm, shortName, Win32.ServiceAccessRights.QueryStatus);
            if (service == IntPtr.Zero)
                return new TunnelStatus(false, "stopped", shortName, configFile);

            try
            {
                var status = new Win32.ServiceStatus();
                if (!Win32.QueryServiceStatus(service, status))
                    return new TunnelStatus(false, "query_failed", shortName, configFile);

                var running = status.dwCurrentState is Win32.ServiceState.Running or Win32.ServiceState.StartPending;
                return new TunnelStatus(running, status.dwCurrentState.ToString().ToLowerInvariant(), shortName, configFile);
            }
            finally
            {
                Win32.CloseServiceHandle(service);
            }
        }
        finally
        {
            Win32.CloseServiceHandle(scm);
        }
    }
}

internal sealed record TunnelStatus(bool Connected, string State, string ServiceName, string ConfigPath);
