using System.Text;
using System.Text.RegularExpressions;

namespace Kiberone.Vpn;

/// <summary>
/// Adjusts WireGuard configs for classroom use: keep internet via VPN but do not steal LAN routes.
/// </summary>
public static partial class VpnConfigNormalizer
{
    // 0.0.0.0/0 minus RFC1918 (10/8, 172.16/12, 192.168/16) and link-local 169.254/16.
    public const string ClassroomAllowedIpv4 =
        "0.0.0.0/5, 8.0.0.0/7, 11.0.0.0/8, 12.0.0.0/6, 16.0.0.0/4, 32.0.0.0/3, 64.0.0.0/2, " +
        "128.0.0.0/3, 160.0.0.0/5, 168.0.0.0/6, 172.0.0.0/12, 172.32.0.0/11, 172.64.0.0/10, " +
        "172.128.0.0/9, 173.0.0.0/8, 174.0.0.0/7, 176.0.0.0/4, 192.0.0.0/9, 192.128.0.0/11, " +
        "192.160.0.0/13, 193.0.0.0/8, 194.0.0.0/7, 196.0.0.0/6, 200.0.0.0/5, 208.0.0.0/4, 224.0.0.0/3";

    public static byte[] NormalizeForClassroom(ReadOnlySpan<byte> content)
    {
        var text = Encoding.UTF8.GetString(content);
        var normalized = NormalizeForClassroom(text);
        return Encoding.UTF8.GetBytes(normalized);
    }

    public static string NormalizeForClassroom(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        var normalized = AllowedIpsLine().Replace(
            content,
            match =>
            {
                var value = match.Groups["value"].Value;
                if (!UsesFullTunnel(value))
                    return match.Value;

                VpnLog.Info("config", "Replacing full-tunnel AllowedIPs with classroom split (LAN stays local).");
                return $"AllowedIPs = {ClassroomAllowedIpv4}";
            });

        return normalized;
    }

    private static bool UsesFullTunnel(string allowedIps)
    {
        foreach (var part in allowedIps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("0.0.0.0/0", StringComparison.OrdinalIgnoreCase)
                || part.Equals("::/0", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^AllowedIPs\s*=\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AllowedIpsLine();
}
