using System.Diagnostics;

namespace Kiberone.Vpn;

/// <summary>
/// Path helpers for the VPN bridge install script.
/// Elevation (UAC) must only happen from install/repair tooling — never from lesson-time Connect.
/// </summary>
public static class VpnServiceInstaller
{
    public static string ServiceScriptPath =>
        Path.Combine(AppContext.BaseDirectory, "service", "install-student-vpn-service.ps1");

    public static bool IsScriptAvailable => File.Exists(ServiceScriptPath);
}
