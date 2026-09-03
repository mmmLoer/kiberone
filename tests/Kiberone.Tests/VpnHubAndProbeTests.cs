using System.Net;
using System.Text;
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
    public void Embedded_probes_are_england_and_netherlands()
    {
        Assert.True(VpnProbeConfigs.AreReady);
        Assert.Equal(10, VpnProbeConfigs.All.Count);
        Assert.Equal(5, VpnProbeConfigs.ForRegionPool("path-fr", rotate: false).Count);
        Assert.Equal(5, VpnProbeConfigs.ForRegionPool("path-nl", rotate: false).Count);
        Assert.Contains(VpnProbeConfigs.All, x => (VpnConfigText.ReadAssignment(x.Content, "Address") ?? "").Contains("10.77.77.4"));
        Assert.Contains(VpnProbeConfigs.All, x => (VpnConfigText.ReadAssignment(x.Content, "Address") ?? "").Contains("10.200.0.103"));
        Assert.Contains("path-fr", VpnProbeConfigs.All.Select(x => x.RegionId));
        Assert.Contains("path-nl", VpnProbeConfigs.All.Select(x => x.RegionId));
        Assert.Equal("51.89.174.71", VpnConfigText.CheckHost(VpnProbeConfigs.ForRegionPool("path-fr", rotate: false)[0].Content));
        Assert.Equal("193.235.147.228", VpnConfigText.CheckHost(VpnProbeConfigs.ForRegionPool("path-nl", rotate: false)[0].Content));
        Assert.Equal("193.233.220.158:51821", VpnConfigText.ReadAssignment(VpnProbeConfigs.ForRegionPool("path-fr", rotate: false)[0].Content, "Endpoint"));
        Assert.Equal("80.90.188.85:51821", VpnConfigText.ReadAssignment(VpnProbeConfigs.ForRegionPool("path-nl", rotate: false)[0].Content, "Endpoint"));
        Assert.Contains("Авто", VpnRegionCatalog.Names());
        Assert.Equal("path-fr", VpnRegionCatalog.Resolve("vpn-1").Id);
        Assert.True(VpnRegionCatalog.IsAuto("Авто"));
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
        store.PutVpnPeers("path-fr", "ШБ", "Shb-Test-4821", [new VpnPeerFile("01.conf", "[Interface]\nPrivateKey = abc")]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        ClassroomHubApi.Map(app, store);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new ClassroomHubClient(address);

        var regions = await client.ListVpnRegionsAsync();
        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, x => x.Id == "path-fr" && x.PeerCount >= 1);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.DownloadVpnPeersAsync("path-fr", "ШБ", "wrong"));

        var pack = await client.DownloadVpnPeersAsync("path-fr", "ШБ", "Shb-Test-4821");
        Assert.Single(pack.Files);
        Assert.Equal("01.conf", pack.Files[0].FileName);

        await app.StopAsync();
        Directory.Delete(data, true);
    }

    [Fact]
    public async Task Path_selector_picks_least_loaded_live_path()
    {
        using var handler = new StubStatusHandler();
        using var client = new VpnPathStatusClient(handler);
        var picked = await VpnPathSelector.PickBestAsync(client);
        Assert.Equal("path-nl", picked?.Id);

        var rows = await VpnPathSelector.ProbeAllAsync(client);
        Assert.All(rows, row => Assert.True(row.Health));
        Assert.Contains(rows, row => row.Region.Id == "path-fr" && row.Status.PeersActive == 70);
        Assert.StartsWith("Bearer ", handler.LastAuthorization ?? string.Empty);
        Assert.Equal(32, handler.LastAuthorization?.Split(' ').LastOrDefault()?.Length);
    }

    [Fact]
    public async Task Config_api_leases_free_slot_and_falls_back_when_primary_full()
    {
        using var handler = new StubStatusHandler { FrConfigFull = true };
        using var client = new VpnPathStatusClient(handler, TimeSpan.FromSeconds(5));
        var preferred = VpnRegionCatalog.Resolve("path-fr");
        var (lease, region) = await VpnPathSelector.LeaseConfigAsync(client, "ШБ", preferred);
        Assert.Equal("path-nl", region.Id);
        Assert.Equal("shb-02", lease.Slot);
        Assert.Contains("[Interface]", lease.Config);
        Assert.Equal("ШБ", lease.Location);
    }

    [Fact]
    public async Task Config_api_issues_from_preferred_path()
    {
        using var handler = new StubStatusHandler();
        using var client = new VpnPathStatusClient(handler, TimeSpan.FromSeconds(5));
        var issued = await client.IssueConfigAsync(VpnRegionCatalog.Resolve("path-fr"), "АКСАКОВА 2");
        Assert.Equal("path-fr", issued.PathId);
        Assert.Equal("aksakova-2-01", issued.Slot);
        Assert.Equal("193.233.220.158:51822", issued.Endpoint);
    }

    [Fact]
    public void Score_prefers_empty_path_over_busy_one()
    {
        var busy = new VpnPathStatusReport(true, "path-fr", "193.233.220.158", 51822, 70, 72, 80, 1.2, 0, 40, 80, true, 40);
        var free = new VpnPathStatusReport(true, "path-nl", "80.90.188.85", 51823, 2, 4, 80, 0.2, 0, 1, 20, true, 30);
        Assert.True(VpnPathSelector.Score(free) < VpnPathSelector.Score(busy));
        Assert.False(VpnPathSelector.IsLive(busy with { PeersActive = 80 }));
        Assert.Equal(51822, VpnRegionCatalog.Resolve("path-fr").WgPort);
        Assert.Equal(51823, VpnRegionCatalog.Resolve("path-nl").WgPort);
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

    private sealed class StubStatusHandler : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }
        public bool FrConfigFull { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            var path = request.RequestUri?.AbsolutePath ?? "";
            var host = request.RequestUri?.Host ?? "";
            if (path == "/health")
                return Json(new { ok = true });

            if (path == "/config")
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                var location = request.RequestUri!.Query
                    .TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Split('=', 2))
                    .Where(parts => parts.Length == 2 && parts[0] == "location")
                    .Select(parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')))
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException("location required");
                if (host.StartsWith("193.233.220.158") && FrConfigFull)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("""{"ok":false,"error":"location_full"}""", Encoding.UTF8, "application/json")
                    });

                if (host.StartsWith("193.233.220.158"))
                {
                    return Json(new
                    {
                        ok = true,
                        location = location,
                        location_id = "aksakova-2",
                        path_id = "path-fr",
                        slot = "aksakova-2-01",
                        address = "10.78.0.2",
                        endpoint = "193.233.220.158:51822",
                        config = "[Interface]\nPrivateKey = aaa\nAddress = 10.78.0.2/32\n\n[Peer]\nEndpoint = 193.233.220.158:51822\n"
                    });
                }

                return Json(new
                {
                    ok = true,
                    location = "ШБ",
                    location_id = "shb",
                    path_id = "path-nl",
                    slot = "shb-02",
                    address = "10.79.0.3",
                    endpoint = "80.90.188.85:51823",
                    config = "[Interface]\nPrivateKey = bbb\nAddress = 10.79.0.3/32\n\n[Peer]\nEndpoint = 80.90.188.85:51823\n"
                });
            }

            if (path == "/status" && host.StartsWith("193.233.220.158"))
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                return Json(new
                {
                    ok = true,
                    path_id = "path-fr",
                    public_host = "193.233.220.158",
                    wg_port = 51822,
                    peers_active = 70,
                    peers_total = 72,
                    peers_cap = 80,
                    cpu = 1.1,
                    rx_mbps = 0.0,
                    tx_mbps = 30.0,
                    rtt_exit_ms = 80.0,
                    uplink_ok = true
                });
            }

            return Json(new
            {
                ok = true,
                path_id = "path-nl",
                public_host = "80.90.188.85",
                wg_port = 51823,
                peers_active = 1,
                peers_total = 2,
                peers_cap = 80,
                cpu = 0.2,
                rx_mbps = 0.0,
                tx_mbps = 1.0,
                rtt_exit_ms = 20.0,
                uplink_ok = true
            });
        }

        private static Task<HttpResponseMessage> Json(object body)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

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
