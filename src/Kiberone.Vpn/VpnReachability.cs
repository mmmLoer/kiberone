using System.Net.NetworkInformation;
using Kiberone.Core;

namespace Kiberone.Vpn;

public sealed record VpnPingResult(bool Ok, string Host, int? RoundtripMs, string? Error);

public static class VpnReachability
{
    public static async Task<VpnPingResult> PingAsync(string host, TimeSpan timeout, int attempts = 3, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new VpnPingResult(false, host, null, "Не указан адрес для ping.");

        var trimmed = host.Trim();
        Exception? lastError = null;
        using var ping = new Ping();
        var budget = timeout <= TimeSpan.Zero ? 1000 : (int)Math.Clamp(timeout.TotalMilliseconds, 300, 3000);
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(trimmed, budget).WaitAsync(ct);
                if (reply.Status == IPStatus.Success)
                    return new VpnPingResult(true, trimmed, (int)reply.RoundtripTime, null);

                lastError = new InvalidOperationException(reply.Status.ToString());
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                lastError = error;
            }

            await Task.Delay(150, ct);
        }

        return new VpnPingResult(false, trimmed, null, lastError?.Message ?? "Ping не ответил.");
    }

    public static VpnPingResult Ping(string host, TimeSpan timeout, int attempts = 3)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new VpnPingResult(false, host, null, "Не указан адрес для ping.");

        var trimmed = host.Trim();
        Exception? lastError = null;
        using var ping = new Ping();
        var budget = timeout <= TimeSpan.Zero ? 1000 : (int)Math.Clamp(timeout.TotalMilliseconds, 300, 3000);
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                var reply = ping.Send(trimmed, budget);
                if (reply.Status == IPStatus.Success)
                    return new VpnPingResult(true, trimmed, (int)reply.RoundtripTime, null);

                lastError = new InvalidOperationException(reply.Status.ToString());
            }
            catch (Exception error)
            {
                lastError = error;
            }

            Thread.Sleep(150);
        }

        return new VpnPingResult(false, trimmed, null, lastError?.Message ?? "Ping не ответил.");
    }
}

public static class VpnHealthCheck
{
    public static VpnRuntimeInfo FromPing(VpnStatus status, VpnPingResult ping, string? region = null)
    {
        var healthy = status.Connected && ping.Ok;
        return new VpnRuntimeInfo(
            status.Connected,
            healthy,
            ping.RoundtripMs,
            region,
            ping.Host,
            healthy ? null : ping.Error ?? status.LastError);
    }

    public static string ResolveCheckHost(string? configText, string? requested, string? regionFallback)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim();
        if (!string.IsNullOrWhiteSpace(configText))
        {
            var fromConfig = VpnConfigText.CheckHost(configText, regionFallback);
            if (!string.IsNullOrWhiteSpace(fromConfig))
                return fromConfig;
        }

        return string.IsNullOrWhiteSpace(regionFallback) ? "1.1.1.1" : regionFallback.Trim();
    }
}
