using System.Net;
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
                if (!ShouldRewriteAllowedIps(value))
                    return match.Value;

                VpnLog.Info("config", "Rewriting AllowedIPs to classroom IPv4 split (LAN local, no IPv6 tunnel).");
                return $"AllowedIPs = {ClassroomAllowedIpv4}";
            });

        normalized = EnsureDns(normalized);
        normalized = StripIpv6Addresses(normalized);
        return normalized;
    }

    private static bool ShouldRewriteAllowedIps(string allowedIps)
    {
        var parts = allowedIps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return true;

        foreach (var part in parts)
        {
            if (part.Equals("0.0.0.0/0", StringComparison.OrdinalIgnoreCase)
                || part.Equals("::/0", StringComparison.OrdinalIgnoreCase)
                || part.StartsWith("2000::/", StringComparison.OrdinalIgnoreCase)
                || part.Contains(':', StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Server-issued "internet minus LAN" lists are long; pin to our known-safe IPv4 split.
        return parts.Length >= 8;
    }

    private static string EnsureDns(string content)
    {
        if (DnsLine().IsMatch(content))
            return content;

        VpnLog.Info("config", "Adding DNS = 1.1.1.1 for classroom VPN.");
        return InterfaceHeader().Replace(content, "$0\nDNS = 1.1.1.1", 1);
    }

    private static string StripIpv6Addresses(string content)
    {
        return AddressLine().Replace(content, match =>
        {
            var kept = match.Groups["value"].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.Contains(':', StringComparison.Ordinal))
                .ToArray();
            if (kept.Length == 0)
                return match.Value;
            return $"Address = {string.Join(", ", kept)}";
        });
    }

    public static string? TryGetEndpointHost(string content)
    {
        var endpoint = EndpointLine().Match(content);
        if (!endpoint.Success)
            return null;

        var value = endpoint.Groups["value"].Value.Trim();
        var host = value;
        var bracket = value.IndexOf(']', StringComparison.Ordinal);
        if (value.StartsWith('[') && bracket > 0)
            host = value[1..bracket];
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon > 0 && IPAddress.TryParse(value[..colon], out _))
                host = value[..colon];
            else if (colon > 0)
                host = value[..colon];
        }

        return string.IsNullOrWhiteSpace(host) ? null : host;
    }

    [GeneratedRegex(@"^AllowedIPs\s*=\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AllowedIpsLine();

    [GeneratedRegex(@"^DNS\s*=\s*.+$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DnsLine();

    [GeneratedRegex(@"^\[Interface\]\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex InterfaceHeader();

    [GeneratedRegex(@"^Address\s*=\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex AddressLine();

    [GeneratedRegex(@"^Endpoint\s*=\s*(?<value>.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EndpointLine();
}
