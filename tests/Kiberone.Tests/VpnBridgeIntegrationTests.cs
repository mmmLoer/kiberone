using Kiberone.Vpn;

namespace Kiberone.Tests;

public sealed class VpnBridgeIntegrationTests
{
    [RealVpnFact]
    public void Bridge_ping_when_service_running()
    {
        var client = new VpnBridgeClient();
        if (!client.IsServiceInstalled || !client.IsServiceRunning)
            return;

        Assert.True(client.TryPing());
    }

    [RealVpnFact]
    public void Bridge_connect_when_config_present()
    {
        var configPath = VpnOptions.ManagedConfigPath;
        if (!File.Exists(configPath))
            return;

        var client = new VpnBridgeClient();
        if (!client.IsServiceInstalled || !client.IsServiceRunning || !client.TryPing())
            return;

        try
        {
            client.Disconnect(configPath);
        }
        catch
        {
            // ignore cleanup errors
        }

        var status = client.Connect(configPath);
        Assert.True(status.Connected, status.LastError ?? $"state={status.State}");
    }
}

public sealed class RealVpnFactAttribute : Xunit.FactAttribute
{
    public RealVpnFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("KIBERONE_TEST_REAL_VPN") != "1")
            Skip = "Real VPN tests require explicit opt-in on a test VM.";
    }
}
