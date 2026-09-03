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
            Console.WriteLine("Тестовые клиенты ещё не встроены (заглушки PLACEHOLDER).");
            Console.WriteLine("Служба установлена. Когда появятся боевые пробники, проверка ping заработает сама.");
            return 0;
        }

        var failed = 0;
        foreach (var probe in VpnProbeConfigs.All)
        {
            Console.WriteLine();
            Console.WriteLine($"Сервер {probe.RegionId} ({probe.FileName})");
            try
            {
                var result = Probe(controller, probe);
                if (result.Healthy)
                    Console.WriteLine($"  туннель ок · ping {result.CheckHost} {result.PingMs} мс");
                else
                {
                    Console.WriteLine($"  ошибка: {result.Error}");
                    failed++;
                }
            }
            catch (Exception error)
            {
                Console.WriteLine($"  ошибка: {error.Message}");
                failed++;
            }
            finally
            {
                try { controller.Disconnect(); } catch { }
            }
        }

        Console.WriteLine();
        if (failed > 0)
        {
            Console.WriteLine($"Проверка не прошла: {failed} из {VpnProbeConfigs.All.Count} серверов.");
            return 1;
        }

        Console.WriteLine("Оба тестовых VPN-сервера отвечают. Установка в порядке.");
        return 0;
    }

    private static VpnRuntimeInfo Probe(VpnController controller, VpnProbeConfig probe)
    {
        var checkHost = VpnHealthCheck.ResolveCheckHost(probe.Content, null, VpnRegionCatalog.Resolve(probe.RegionId).CheckHost);
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "KIBERone", "Student", "vpn");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"probe-{probe.RegionId}.conf");
        File.WriteAllText(path, probe.Content);
        try
        {
            controller.InstallConfig(System.Text.Encoding.UTF8.GetBytes(probe.Content));
            var connected = controller.Connect();
            if (!connected.Connected)
                return new VpnRuntimeInfo(false, false, null, probe.RegionId, checkHost, connected.LastError ?? "Туннель не поднялся.");

            Thread.Sleep(1200);
            return controller.VerifyReachability(checkHost, probe.RegionId);
        }
        finally
        {
            try { controller.Disconnect(); } catch { }
            try { File.Delete(path); } catch { }
        }
    }
}
