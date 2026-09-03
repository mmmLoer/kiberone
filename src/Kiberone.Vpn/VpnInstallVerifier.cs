using Kiberone.Core;

namespace Kiberone.Vpn;

public static class VpnInstallVerifier
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Проверка VPN после установки Student");
        Console.WriteLine();

        var options = new VpnOptions { RequireBridge = true };
        var controller = new VpnController(options);
        if (!controller.IsServiceAvailable)
        {
            Console.WriteLine("Ошибка: служба KIBERoneStudentVpn не запущена.");
            Console.WriteLine("Запустите Repair-Student-Vpn.cmd от администратора.");
            return 1;
        }

        Console.WriteLine("Служба VPN: ок");

        if (!VpnProbeConfigs.AreReady)
        {
            Console.WriteLine("Тестовые клиенты ещё не встроены.");
            return 1;
        }

        using var api = new VpnPathStatusClient();
        var liveCount = 0;
        IReadOnlyList<(VpnRegionInfo Region, bool Health, VpnPathStatusReport Status)> rows;
        try
        {
            rows = VpnPathSelector.ProbeAllAsync(api).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Console.WriteLine($"Status API недоступен: {error.Message}");
            rows = [];
        }

        Console.WriteLine();
        Console.WriteLine("Status API");
        foreach (var region in VpnRegionCatalog.All)
        {
            var row = rows.FirstOrDefault(item => item.Region.Id == region.Id);
            var health = row.Region is not null && row.Health;
            var status = row.Region is not null ? row.Status : null;
            var live = health && status is not null && VpnPathSelector.IsLive(status);
            if (live) liveCount++;
            Console.WriteLine($"  {region.Name} ({region.Id})");
            Console.WriteLine($"    /health: {(health ? "ок" : "нет ответа")}");
            if (status is null)
                Console.WriteLine("    /status: нет ответа");
            else if (!string.IsNullOrWhiteSpace(status.Error))
                Console.WriteLine($"    /status: {status.Error}");
            else
            {
                Console.WriteLine($"    /status: ok={status.Ok} uplink={status.UplinkOk} peers={status.PeersActive}/{status.PeersCap} cpu={status.Cpu:0.00} rtt_exit={status.RttExitMs:0} мс");
                if (live)
                    Console.WriteLine($"    оценка нагрузки: {VpnPathSelector.Score(status):0.0}");
            }
        }

        var failedRegions = 0;
        foreach (var region in VpnRegionCatalog.All)
        {
            Console.WriteLine();
            Console.WriteLine($"Туннель {region.Name}");
            var probes = VpnProbeConfigs.ForRegionPool(region.Id);
            var connected = false;
            foreach (var probe in probes)
            {
                Console.WriteLine($"  пробуем {probe.FileName}…");
                try
                {
                    var result = Probe(controller, probe, region);
                    if (result.Healthy)
                    {
                        Console.WriteLine($"  туннель ок · {probe.FileName} · ping {result.CheckHost} {result.PingMs} мс");
                        connected = true;
                        break;
                    }

                    Console.WriteLine($"  занят или не ответил: {result.Error}");
                }
                catch (Exception error)
                {
                    Console.WriteLine($"  ошибка: {error.Message}");
                }
                finally
                {
                    try { controller.Disconnect(); } catch { }
                }
            }

            if (!connected)
            {
                Console.WriteLine("  ни один тестовый профиль этой локации не поднялся.");
                failedRegions++;
            }
        }

        Console.WriteLine();
        if (failedRegions == VpnRegionCatalog.All.Count)
        {
            Console.WriteLine("Ни один тестовый туннель не поднялся. VPN не готов.");
            return 1;
        }

        if (liveCount == 0)
            Console.WriteLine("Туннель есть, но status API не ответил. Установка принята с предупреждением.");
        else if (failedRegions > 0)
            Console.WriteLine($"Один путь не прошёл ping. Живых status API: {liveCount}. Установка принята.");
        else
            Console.WriteLine("Оба VPN-пути отвечают. Установка в порядке.");
        return 0;
    }

    private static VpnRuntimeInfo Probe(VpnController controller, VpnProbeConfig probe, VpnRegionInfo region)
    {
        var checkHost = VpnHealthCheck.ResolveCheckHost(probe.Content, null, region.CheckHost);
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "KIBERone", "Student", "vpn");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"probe-{probe.FileName}");
        File.WriteAllText(path, probe.Content);
        try
        {
            controller.InstallConfig(System.Text.Encoding.UTF8.GetBytes(probe.Content));
            var connected = controller.Connect();
            if (!connected.Connected)
                return new VpnRuntimeInfo(false, false, null, probe.RegionId, checkHost, connected.LastError ?? "Туннель не поднялся.");

            Thread.Sleep(1500);
            return controller.VerifyReachability(checkHost, probe.RegionId);
        }
        finally
        {
            try { controller.Disconnect(); } catch { }
            try { File.Delete(path); } catch { }
        }
    }
}
