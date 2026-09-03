using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace Kiberone.Vpn;

public sealed record VpnHandshakeInfo(
    bool Completed,
    bool KeepaliveSeen,
    bool AdapterUp,
    string? AdapterAddress,
    string? LastLine,
    string LogPath);

public static class VpnTunnelDiagnostics
{
    public static string LogBinPath(string configPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KIBERone", "Student", "vpn");
        return Path.Combine(directory, "log.bin");
    }

    public static VpnHandshakeInfo ReadHandshake(string configPath)
    {
        var path = LogBinPath(configPath);
        var adapter = TryReadAdapter(configPath);
        if (!File.Exists(path))
            return new VpnHandshakeInfo(adapter.Up, false, adapter.Up, adapter.Address, null, path);

        try
        {
            // WireGuard keeps log.bin open for write; share must allow concurrent readers.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var text = Encoding.Unicode.GetString(bytes) + "\n" + Encoding.ASCII.GetString(bytes);
            var lines = Regex.Split(text, @"[\u0000\r\n]+")
                .Select(line => line.Trim())
                .Where(line => line.Contains("[TUN]", StringComparison.Ordinal))
                .ToList();

            var recent = lines.TakeLast(120).ToList();
            var completed = recent.Any(line =>
                line.Contains("Receiving handshake response", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Keypair", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Startup complete", StringComparison.OrdinalIgnoreCase));
            var keepalive = recent.Any(line =>
                line.Contains("Receiving keepalive", StringComparison.OrdinalIgnoreCase));
            var last = recent.LastOrDefault();

            // Adapter with tunnel address is strong evidence even if log parsing lags.
            if (!completed && adapter.Up)
                completed = true;

            return new VpnHandshakeInfo(completed, keepalive, adapter.Up, adapter.Address, last, path);
        }
        catch (Exception error)
        {
            VpnLog.Warn("diag", $"Cannot read WireGuard log.bin: {error.Message}");
            // Fall back to adapter presence so we do not false-fail a live tunnel.
            return new VpnHandshakeInfo(adapter.Up, false, adapter.Up, adapter.Address, null, path);
        }
    }

    public static VpnHandshakeInfo WaitForHandshake(string configPath, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        VpnHandshakeInfo last = ReadHandshake(configPath);
        while (DateTime.UtcNow < deadline)
        {
            last = ReadHandshake(configPath);
            if (last.Completed || (last.AdapterUp && last.KeepaliveSeen))
                return last;
            Thread.Sleep(250);
        }

        return last;
    }

    private static (bool Up, string? Address) TryReadAdapter(string configPath)
    {
        try
        {
            string? expected = null;
            if (File.Exists(configPath))
            {
                foreach (var line in File.ReadLines(configPath))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("Address", StringComparison.OrdinalIgnoreCase) || !trimmed.Contains('='))
                        continue;
                    expected = trimmed.Split('=', 2)[1].Trim().Split(',', 2)[0].Trim();
                    var slash = expected.IndexOf('/');
                    if (slash > 0)
                        expected = expected[..slash];
                    break;
                }
            }

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                    continue;

                var name = nic.Name + " " + nic.Description;
                var looksLikeTunnel = name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                                      || name.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
                                      || name.Contains("peer", StringComparison.OrdinalIgnoreCase)
                                      || name.Contains("KIBERone", StringComparison.OrdinalIgnoreCase);
                var props = nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToList();
                if (props.Count == 0)
                    continue;

                if (!string.IsNullOrWhiteSpace(expected) && props.Contains(expected))
                    return (true, expected);
                if (looksLikeTunnel && props.Any(ip => ip.StartsWith("10.7", StringComparison.Ordinal) || ip.StartsWith("10.79", StringComparison.Ordinal) || ip.StartsWith("10.200", StringComparison.Ordinal)))
                    return (true, props[0]);
            }
        }
        catch (Exception error)
        {
            VpnLog.Warn("diag", $"Adapter probe failed: {error.Message}");
        }

        return (false, null);
    }
}
