using Kiberone.Vpn;

namespace Kiberone.Tests;

public sealed class VpnConfigNormalizerTests
{
    [Fact]
    public void Normalize_replaces_full_tunnel_allowed_ips_and_strips_dns()
    {
        const string input = """
            [Interface]
            PrivateKey = abc
            Address = 10.1.0.2/32
            DNS = 1.1.1.1

            [Peer]
            PublicKey = abc
            AllowedIPs = 0.0.0.0/0, ::/0
            """;

        var output = VpnConfigNormalizer.NormalizeForClassroom(input);

        Assert.DoesNotContain("0.0.0.0/0", output, StringComparison.Ordinal);
        Assert.DoesNotContain("::/0", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DNS =", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(VpnConfigNormalizer.ClassroomAllowedIpv4, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_rewrites_server_issued_internet_split_and_strips_ipv6()
    {
        const string input = """
            [Interface]
            PrivateKey = abc
            Address = 10.79.0.2/32, fd00::2/128
            DNS = 1.1.1.1, 2606:4700:4700::1111

            [Peer]
            PublicKey = abc
            Endpoint = 80.90.188.85:51823
            AllowedIPs = 0.0.0.0/5, 8.0.0.0/7, 11.0.0.0/8, 12.0.0.0/6, 16.0.0.0/4, 32.0.0.0/3, 64.0.0.0/3, 96.0.0.0/4, 2000::/3
            PersistentKeepalive = 25
            """;

        var output = VpnConfigNormalizer.NormalizeForClassroom(input);

        Assert.Contains(VpnConfigNormalizer.ClassroomAllowedIpv4, output, StringComparison.Ordinal);
        Assert.DoesNotContain("2000::/3", output, StringComparison.Ordinal);
        Assert.DoesNotContain("fd00::2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("DNS =", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Address = 10.79.0.2/32", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_keeps_narrow_split_tunnel_config()
    {
        const string input = """
            [Interface]
            PrivateKey = abc
            Address = 10.200.0.2/32

            [Peer]
            AllowedIPs = 10.200.0.0/24
            """;

        var output = VpnConfigNormalizer.NormalizeForClassroom(input);

        Assert.Equal(input.Replace("\r\n", "\n").Trim(), output.Replace("\r\n", "\n").Trim());
    }
}
