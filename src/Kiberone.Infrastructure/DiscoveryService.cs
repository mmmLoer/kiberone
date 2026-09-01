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
                var beacon = CreateBeacon();
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
        using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var destination = new IPEndPoint(IPAddress.Broadcast, DiscoveryProtocol.BeaconPort);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await sender.SendAsync(DiscoveryProtocol.Serialize(CreateBeacon()), destination, cancellationToken);
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

    private DiscoveryBeacon CreateBeacon() => new(
        DiscoveryProtocol.BeaconType,
        options.SyncToken,
        LocalAddressResolver.GetPreferredIpv4Address().ToString(),
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
                if (DiscoveryProtocol.Parse(packet.Buffer) is { } beacon) return beacon;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        return null;
    }
}

public static class LocalAddressResolver
{
    public static IPAddress GetPreferredIpv4Address()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
            .OrderByDescending(address => IsPrivate(address.Address))
            .Select(address => address.Address)
            .ToList();
        return candidates.FirstOrDefault() ?? IPAddress.Loopback;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }
}
