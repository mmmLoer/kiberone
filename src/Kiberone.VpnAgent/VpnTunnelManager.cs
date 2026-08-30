using Kiberone.VpnAgent.WireGuard;

namespace Kiberone.VpnAgent;

public sealed class VpnTunnelManager
{
    private readonly VpnAgentOptions options;
    private readonly ILogger<VpnTunnelManager> logger;
    private readonly object gate = new();
    private string? lastError;

    public VpnTunnelManager(VpnAgentOptions options, ILogger<VpnTunnelManager> logger)
    {
        this.options = options;
        this.logger = logger;
    }

    public string ConfigPath => Path.GetFullPath(options.ConfigPath);

    public object Connect()
    {
        lock (gate)
        {
            try
            {
                var path = ConfigPath;
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Config missing: {path}");

                var current = TunnelService.GetStatus(path);
                if (current.Connected)
                {
                    lastError = null;
                    return StatusPayload(current);
                }

                logger.LogInformation("Connecting WireGuard tunnel from {Config}", path);
                TunnelService.Connect(path, ephemeral: true);
                lastError = null;
                Thread.Sleep(400);
                return StatusPayload(TunnelService.GetStatus(path));
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                logger.LogError(ex, "Connect failed");
                throw;
            }
        }
    }

    public object Disconnect()
    {
        lock (gate)
        {
            try
            {
                var path = ConfigPath;
                logger.LogInformation("Disconnecting WireGuard tunnel {Config}", path);
                if (File.Exists(path))
                    TunnelService.Disconnect(path, waitForStop: true);
                lastError = null;
                return StatusPayload(File.Exists(path)
                    ? TunnelService.GetStatus(path)
                    : new TunnelStatus(false, "stopped", "WireGuardTunnel$peer", path));
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                logger.LogError(ex, "Disconnect failed");
                throw;
            }
        }
    }

    public object Status()
    {
        var path = ConfigPath;
        var status = File.Exists(path)
            ? TunnelService.GetStatus(path)
            : new TunnelStatus(false, "config_missing", "WireGuardTunnel$peer", path);
        return StatusPayload(status);
    }

    private object StatusPayload(TunnelStatus status) => new
    {
        ok = true,
        connected = status.Connected,
        state = status.State,
        service_name = status.ServiceName,
        config_path = status.ConfigPath,
        config_exists = File.Exists(status.ConfigPath),
        endpoint = "80.90.188.85:51821",
        last_error = lastError
    };
}
