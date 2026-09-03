using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using Kiberone.Core;

namespace Kiberone.Vpn;

public sealed record VpnPingResult(bool Ok, string Host, int? RoundtripMs, string? Error, string Method = "ping");

public static class VpnReachability
{
    private static readonly string[] FallbackProbeHosts = ["1.1.1.1", "8.8.8.8"];

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
        var budget = timeout <= TimeSpan.Zero ? 1000 : (int)Math.Clamp(timeout.TotalMilliseconds, 300, 4000);
        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var reply = ping.Send(trimmed, budget);
                clock.Stop();
                if (reply.Status == IPStatus.Success)
                {
                    VpnLog.Info("probe", $"ping {trimmed} OK {clock.ElapsedMilliseconds} ms");
                    return new VpnPingResult(true, trimmed, (int)clock.ElapsedMilliseconds, null);
                }

                lastError = new InvalidOperationException(reply.Status.ToString());
                VpnLog.Info("probe", $"ping {trimmed} attempt {attempt + 1}: {reply.Status}");
            }
            catch (Exception error)
            {
                lastError = error;
                VpnLog.Info("probe", $"ping {trimmed} attempt {attempt + 1}: {error.Message}");
            }

            Thread.Sleep(150);
        }

        return new VpnPingResult(false, trimmed, null, lastError?.Message ?? "Ping не ответил.");
    }

    /// <summary>
    /// Verifies the tunnel carries traffic. ICMP to exit check-hosts is often blocked,
    /// so we also try well-known hosts and a short HTTP probe.
    /// </summary>
    public static VpnPingResult Probe(string preferredHost, TimeSpan timeout, int attempts = 2)
    {
        var hosts = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredHost))
            hosts.Add(preferredHost.Trim());
        foreach (var host in FallbackProbeHosts)
        {
            if (!hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                hosts.Add(host);
        }

        VpnLog.Info("probe", $"Start traffic probe hosts=[{string.Join(", ", hosts)}] timeoutMs={(int)timeout.TotalMilliseconds}");
        VpnPingResult? last = null;
        foreach (var host in hosts)
        {
            var ping = Ping(host, timeout, attempts);
            if (ping.Ok)
                return ping;
            last = ping;

            var http = HttpProbe(host, timeout);
            if (http.Ok)
                return http;
            last = http;
        }

        VpnLog.Warn("probe", $"All traffic probes failed. Last: {last?.Host} {last?.Method} {last?.Error}");
        return last ?? new VpnPingResult(false, preferredHost, null, "Нет ответа через туннель.");
    }

    public static VpnPingResult HttpProbe(string host, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host))
            return new VpnPingResult(false, host, null, "Не указан адрес для HTTP-проверки.", "http");

        var trimmed = host.Trim();
        var budget = timeout <= TimeSpan.Zero ? 3000 : (int)Math.Clamp(timeout.TotalMilliseconds, 800, 8000);
        var urls = new[]
        {
            $"http://{trimmed}/cdn-cgi/trace",
            $"http://{trimmed}/"
        };

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(budget) };
                var clock = Stopwatch.StartNew();
                using var response = client.GetAsync(url).GetAwaiter().GetResult();
                clock.Stop();
                if ((int)response.StatusCode is >= 200 and < 500)
                {
                    VpnLog.Info("probe", $"http {url} OK {(int)response.StatusCode} {clock.ElapsedMilliseconds} ms");
                    return new VpnPingResult(true, trimmed, (int)clock.ElapsedMilliseconds, null, "http");
                }

                lastError = new InvalidOperationException($"HTTP {(int)response.StatusCode}");
                VpnLog.Info("probe", $"http {url} => {(int)response.StatusCode}");
            }
            catch (Exception error)
            {
                lastError = error;
                VpnLog.Info("probe", $"http {url} => {error.Message}");
            }
        }

        return new VpnPingResult(false, trimmed, null, lastError?.Message ?? "HTTP не ответил.", "http");
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
