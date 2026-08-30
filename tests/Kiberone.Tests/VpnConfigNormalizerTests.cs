using Kiberone.Vpn;

namespace Kiberone.Tests;

public sealed class VpnConfigNormalizerTests
{
    [Fact]
    public void Normalize_replaces_full_tunnel_allowed_ips()
    {
        const string input = """
            [Peer]
            PublicKey = abc
            AllowedIPs = 0.0.0.0/0
            """;

        var output = VpnConfigNormalizer.NormalizeForClassroom(input);

        Assert.DoesNotContain("0.0.0.0/0", output, StringComparison.Ordinal);
        Assert.Contains(VpnConfigNormalizer.ClassroomAllowedIpv4, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_keeps_split_tunnel_config()
    {
        const string input = """
            [Peer]
            AllowedIPs = 10.200.0.0/24
            """;

        var output = VpnConfigNormalizer.NormalizeForClassroom(input);

        Assert.Equal(input, output);
    }
}
