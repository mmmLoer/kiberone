using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public static class DiscoveryProtocol
{
    public const string Request = "KIBERONE_DISCOVER";
    public const string BeaconType = "KIBERONE_TEACHER";
    public const int RequestPort = 8766;
    public const int BeaconPort = 8767;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static byte[] Serialize(DiscoveryBeacon beacon) => JsonSerializer.SerializeToUtf8Bytes(beacon, JsonOptions);

    public static DiscoveryBeacon? Parse(ReadOnlySpan<byte> payload)
    {
        try
        {
            var beacon = JsonSerializer.Deserialize<DiscoveryBeacon>(payload, JsonOptions);
            return beacon is { Type: BeaconType, Port: > 0 and <= 65535 } &&
                   !string.IsNullOrWhiteSpace(beacon.Host) && !string.IsNullOrWhiteSpace(beacon.Token)
                ? beacon
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class DiscoveryAnnouncer(
    ClassroomServerOptions options,
    string serverId,
    string version = BuildInfo.Version) : IAsyncDisposable
{
    private CancellationTokenSource? lifetime;
    private UdpClient? listener;
    private Task? listenerTask;
    private Task? beaconTask;

    public void Start()
    {
        if (lifetime is not null) return;
        options.Validate();
        lifetime = new CancellationTokenSource();
        listener = CreateBoundClient(DiscoveryProtocol.RequestPort);
        listenerTask = ListenAsync(lifetime.Token);
        beaconTask = BroadcastAsync(lifetime.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var packet = await listener!.ReceiveAsync(cancellationToken);
                if (!Encoding.UTF8.GetString(packet.Buffer).Equals(DiscoveryProtocol.Request, StringComparison.Ordinal)) continue;
                var beacon = CreateBeacon(packet.RemoteEndPoint.Address);
                await listener.SendAsync(DiscoveryProtocol.Serialize(beacon), packet.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task BroadcastAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var lan = LocalAddressResolver.GetLanIpv4Endpoints();
                if (lan.Count == 0)
                {
                    // Fallback: limited global broadcast when no LAN NIC is visible yet.
                    using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                    await sender.SendAsync(
                        DiscoveryProtocol.Serialize(CreateBeacon()),
                        new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.BeaconPort),
                        cancellationToken);
                }
                else
                {
                    foreach (var endpoint in lan)
                    {
                        try
                        {
                            // Bind to the LAN NIC so WireGuard default routes cannot steal classroom beacons.
                            using var sender = new UdpClient(new IPEndPoint(endpoint.Address, 0));
                            sender.EnableBroadcast = true;
                            var beacon = CreateBeacon(preferredHost: endpoint.Address);
                            await sender.SendAsync(
                                DiscoveryProtocol.Serialize(beacon),
                                new IPEndPoint(endpoint.Broadcast, DiscoveryProtocol.BeaconPort),
                                cancellationToken);
                        }
                        catch (SocketException)
                        {
                            // NIC may disappear while VPN reconnects; try the rest.
                        }
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private DiscoveryBeacon CreateBeacon(IPAddress? peer = null, IPAddress? preferredHost = null) => new(
        DiscoveryProtocol.BeaconType,
        options.SyncToken,
        (preferredHost ?? LocalAddressResolver.GetPreferredIpv4Address(peer)).ToString(),
        options.Port,
        serverId,
        version);

    private static UdpClient CreateBoundClient(int port)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        client.EnableBroadcast = true;
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        if (lifetime is null) return;
        await lifetime.CancelAsync();
        listener?.Dispose();
        var tasks = new[] { listenerTask, beaconTask }.Where(task => task is not null).Cast<Task>();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        lifetime.Dispose();
        lifetime = null;
    }
}

public static class DiscoveryClient
{
    public static async Task<DiscoveryBeacon?> DiscoverAsync(TimeSpan timeout, string? hintAddress = null, CancellationToken cancellationToken = default)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.BeaconPort));
        client.EnableBroadcast = true;
        var request = Encoding.UTF8.GetBytes(DiscoveryProtocol.Request);

        // Probe from each LAN NIC so student WireGuard cannot swallow classroom discovery.
        foreach (var endpoint in LocalAddressResolver.GetLanIpv4Endpoints())
        {
            try
            {
                using var probe = new UdpClient(new IPEndPoint(endpoint.Address, 0));
                probe.EnableBroadcast = true;
                await probe.SendAsync(request, new IPEndPoint(endpoint.Broadcast, DiscoveryProtocol.RequestPort), cancellationToken);
            }
            catch (SocketException)
            {
            }
        }

        await client.SendAsync(request, new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.RequestPort), cancellationToken);
        if (IPAddress.TryParse(hintAddress, out var hint))
            await client.SendAsync(request, new IPEndPoint(hint, DiscoveryProtocol.RequestPort), cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (!timeoutSource.Token.IsCancellationRequested)
            {
                var packet = await client.ReceiveAsync(timeoutSource.Token);
                if (DiscoveryProtocol.Parse(packet.Buffer) is { } beacon
                    && LocalAddressResolver.IsReachableClassroomHost(beacon.Host))
                    return beacon;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        return null;
    }
}

public readonly record struct LanIpv4Endpoint(IPAddress Address, IPAddress Broadcast, int PrefixLength, string InterfaceName, bool HasGateway, NetworkInterfaceType InterfaceType);

public static class LocalAddressResolver
{
    public static IPAddress GetPreferredIpv4Address(IPAddress? peer = null)
    {
        var endpoints = GetLanIpv4Endpoints();
        if (peer is not null)
        {
            var sameSubnet = endpoints.FirstOrDefault(endpoint => IsSameSubnet(endpoint, peer));
            if (sameSubnet.Address is not null)
                return sameSubnet.Address;
        }

        return endpoints.Count > 0 ? endpoints[0].Address : IPAddress.Loopback;
    }

    public static IReadOnlyList<LanIpv4Endpoint> GetLanIpv4Endpoints()
    {
        var result = new List<LanIpv4Endpoint>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!IsClassroomLanInterface(network))
                continue;

            var props = network.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(gateway.Address)
                && !gateway.Address.Equals(IPAddress.Any));

            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;
                if (IPAddress.IsLoopback(unicast.Address) || IsLinkLocal(unicast.Address))
                    continue;

                var prefix = unicast.PrefixLength > 0 && unicast.PrefixLength <= 32
                    ? unicast.PrefixLength
                    : PrefixFromMask(unicast.IPv4Mask);
                if (prefix is < 8 or > 30)
                    continue;

                result.Add(new LanIpv4Endpoint(
                    unicast.Address,
                    CalculateBroadcast(unicast.Address, prefix),
                    prefix,
                    network.Name,
                    hasGateway,
                    network.NetworkInterfaceType));
            }
        }

        return result
            .OrderByDescending(Score)
            .ThenBy(endpoint => endpoint.Address.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    public static bool IsReachableClassroomHost(string host)
    {
        if (!IPAddress.TryParse(host, out var address))
            return false;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        if (IPAddress.IsLoopback(address) || IsLinkLocal(address))
            return false;

        var lan = GetLanIpv4Endpoints();
        if (lan.Count == 0)
            return IsPrivate(address);

        // Prefer beacons that share a classroom subnet. WireGuard tunnel IPs (often 10.x)
        // are private but not on the student LAN, so they must be ignored.
        return lan.Any(endpoint => IsSameSubnet(endpoint, address));
    }

    internal static bool IsClassroomLanInterface(NetworkInterface network)
    {
        if (network.OperationalStatus != OperationalStatus.Up)
            return false;
        if (network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return false;

        var name = network.Name ?? string.Empty;
        var description = network.Description ?? string.Empty;
        if (IsTunnelName(name) || IsTunnelName(description))
            return false;

        return network.Supports(NetworkInterfaceComponent.IPv4);
    }

    public static bool IsTunnelName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        return text.StartsWith("utun", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("tun", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("wg", StringComparison.OrdinalIgnoreCase)
               || text.Contains("wireguard", StringComparison.OrdinalIgnoreCase)
               || text.Contains("wintun", StringComparison.OrdinalIgnoreCase)
               || text.Contains("tailscale", StringComparison.OrdinalIgnoreCase)
               || text.Contains("zerotier", StringComparison.OrdinalIgnoreCase)
               || text.Contains("hamachi", StringComparison.OrdinalIgnoreCase)
               || text.Contains("radmin vpn", StringComparison.OrdinalIgnoreCase);
    }

    private static int Score(LanIpv4Endpoint endpoint)
    {
        var score = 0;
        if (endpoint.HasGateway) score += 100;
        score += endpoint.InterfaceType switch
        {
            NetworkInterfaceType.Ethernet => 50,
            NetworkInterfaceType.Wireless80211 => 40,
            NetworkInterfaceType.GigabitEthernet => 50,
            NetworkInterfaceType.FastEthernetT => 45,
            _ => 10
        };
        // Prefer common classroom LAN ranges over VPN-style 10/8 when both are non-tunnel.
        var bytes = endpoint.Address.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168) score += 30;
        else if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) score += 20;
        else if (bytes[0] == 10) score += 5;
        return score;
    }

    private static bool IsSameSubnet(LanIpv4Endpoint endpoint, IPAddress peer)
    {
        if (peer.AddressFamily != AddressFamily.InterNetwork) return false;
        var local = endpoint.Address.GetAddressBytes();
        var remote = peer.GetAddressBytes();
        var mask = PrefixToMask(endpoint.PrefixLength);
        for (var i = 0; i < 4; i++)
        {
            if ((local[i] & mask[i]) != (remote[i] & mask[i]))
                return false;
        }
        return true;
    }

    private static IPAddress CalculateBroadcast(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var mask = PrefixToMask(prefixLength);
        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
            broadcast[i] = (byte)(bytes[i] | (byte)~mask[i]);
        return new IPAddress(broadcast);
    }

    private static byte[] PrefixToMask(int prefixLength)
    {
        var mask = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var bits = Math.Clamp(prefixLength - i * 8, 0, 8);
            mask[i] = bits == 0 ? (byte)0 : (byte)(0xFF << (8 - bits));
        }
        return mask;
    }

    private static int PrefixFromMask(IPAddress? mask)
    {
        if (mask is null || mask.AddressFamily != AddressFamily.InterNetwork)
            return 24;
        var bits = 0;
        foreach (var value in mask.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) == 0) return bits;
                bits++;
            }
        }
        return bits;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
               || bytes[0] == 192 && bytes[1] == 168;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
