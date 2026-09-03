using System.Text;
using System.Text.RegularExpressions;

namespace Kiberone.Vpn;

public sealed record VpnHandshakeInfo(bool Completed, bool KeepaliveSeen, string? LastLine, string LogPath);

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
        if (!File.Exists(path))
            return new VpnHandshakeInfo(false, false, null, path);

        try
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.Unicode.GetString(bytes) + "\n" + Encoding.ASCII.GetString(bytes);
            var lines = Regex.Split(text, @"[\u0000\r\n]+")
                .Select(line => line.Trim())
                .Where(line => line.Contains("[TUN]", StringComparison.Ordinal))
                .ToList();

            var recent = lines.TakeLast(80).ToList();
            var completed = recent.Any(line =>
                line.Contains("Receiving handshake response", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Keypair", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Startup complete", StringComparison.OrdinalIgnoreCase));
            var keepalive = recent.Any(line =>
                line.Contains("Receiving keepalive", StringComparison.OrdinalIgnoreCase));
            var last = recent.LastOrDefault();
            return new VpnHandshakeInfo(completed, keepalive, last, path);
        }
        catch (Exception error)
        {
            VpnLog.Warn("diag", $"Cannot read WireGuard log.bin: {error.Message}");
            return new VpnHandshakeInfo(false, false, null, path);
        }
    }

    public static VpnHandshakeInfo WaitForHandshake(string configPath, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        VpnHandshakeInfo last = ReadHandshake(configPath);
        while (DateTime.UtcNow < deadline)
        {
            last = ReadHandshake(configPath);
            if (last.Completed)
                return last;
            Thread.Sleep(250);
        }

        return last;
    }
}
