using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Kiberone.Tests;

public sealed class VpnHubAndProbeTests
{
    [Fact]
    public void Embedded_probes_are_two_placeholders_until_real_configs_arrive()
    {
        Assert.Equal(2, VpnProbeConfigs.All.Count);
        Assert.False(VpnProbeConfigs.AreReady);
        Assert.Contains("vpn-1", VpnProbeConfigs.All.Select(x => x.RegionId));
        Assert.Contains("vpn-2", VpnProbeConfigs.All.Select(x => x.RegionId));
        Assert.Equal("1.1.1.1", VpnConfigText.CheckHost(VpnProbeConfigs.All[0].Content));
    }

    [Fact]
    public void Assign_attaches_fallback_config_with_the_same_file_name()
    {
        using var primary = new TempDirectory();
        using var fallback = new TempDirectory();
        File.WriteAllText(Path.Combine(primary.Path, "05.conf"), "primary");
        File.WriteAllText(Path.Combine(fallback.Path, "05.conf"), "fallback");

        var assignments = VpnConfigDistributor.Assign(
            [CreateClient("c-1", "05", "PC-05")],
            primary.Path,
            fallback.Path);

        var row = Assert.Single(assignments);
        Assert.Equal("05.conf", row.ConfigFileName);
        Assert.Equal(Path.Combine(fallback.Path, "05.conf"), row.FallbackConfigFilePath);
    }

    [Fact]
    public async Task Hub_serves_vpn_peers_only_with_location_password()
    {
        var data = Path.Combine(Path.GetTempPath(), $"kiberone-hub-vpn-{Guid.NewGuid():N}");
        Directory.CreateDirectory(data);
        var created = LocationPassword.Create("Shb-Test-4821");
        var store = new ClassroomHubStore(data, [new LocationSecretRecord("ШБ", created.Salt, created.Hash)]);
        store.PutVpnPeers("vpn-1", "ШБ", "Shb-Test-4821", [new VpnPeerFile("01.conf", "[Interface]\nPrivateKey = abc")]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        ClassroomHubApi.Map(app, store);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new ClassroomHubClient(address);

        var regions = await client.ListVpnRegionsAsync();
        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, x => x.Id == "vpn-1" && x.PeerCount >= 1);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.DownloadVpnPeersAsync("vpn-1", "ШБ", "wrong"));

        var pack = await client.DownloadVpnPeersAsync("vpn-1", "ШБ", "Shb-Test-4821");
        Assert.Single(pack.Files);
        Assert.Equal("01.conf", pack.Files[0].FileName);

        await app.StopAsync();
        Directory.Delete(data, true);
    }

    private static ClassroomClientSnapshot CreateClient(string clientId, string pcNumber, string hostname) =>
        new(
            clientId,
            pcNumber,
            hostname,
            "C:\\Projects",
            BuildInfo.Version,
            null,
            null,
            new ClientRuntimeInfo(false, false, string.Empty, null, false),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            true);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kiberone-vpn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }
}
