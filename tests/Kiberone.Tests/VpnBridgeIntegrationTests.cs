using Kiberone.Vpn;

namespace Kiberone.Tests;

public sealed class VpnBridgeIntegrationTests
{
    [RealVpnFact]
    public void Bridge_ping_when_service_running()
    {
        var client = new VpnBridgeClient();
        Assert.True(client.IsServiceInstalled, "VPN bridge is not installed on the test VM.");
        Assert.True(client.IsServiceRunning, "VPN bridge is not running on the test VM.");
        Assert.True(client.TryPing());
    }

    [RealVpnFact]
    public void Bridge_connect_when_config_present()
    {
        var configPath = VpnOptions.ManagedConfigPath;
        Assert.True(File.Exists(configPath), "The test VM has no managed VPN configuration.");

        var client = new VpnBridgeClient();
        Assert.True(client.IsServiceInstalled && client.IsServiceRunning && client.TryPing(), "VPN bridge is unavailable.");
        var initiallyConnected = client.GetStatus(configPath).Connected;
        try
        {
            client.Disconnect(configPath);
            var status = client.Connect(configPath);
            Assert.True(status.Connected, status.LastError ?? $"state={status.State}");
        }
        finally
        {
            var restored = initiallyConnected ? client.Connect(configPath) : client.Disconnect(configPath);
            Assert.Equal(initiallyConnected, restored.Connected);
        }
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
