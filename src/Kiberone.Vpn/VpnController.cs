using Kiberone.Vpn.WireGuard;
using Kiberone.Core;

namespace Kiberone.Vpn;

public sealed class VpnController
{
    private const string ServiceMissingMessage =
        "VPN-служба не установлена. Запустите Repair-Student-Vpn.cmd или переустановите Student от администратора (один раз).";

    private const string ServiceStoppedMessage =
        "VPN-служба установлена, но не запущена. Запустите Repair-Student-Vpn.cmd от администратора (нужны права Users на Start), затем снова включите VPN.";

    private const string ServicePipeMessage =
        "VPN-служба запущена, но pipe ещё не готов. Подождите пару секунд и включите VPN снова.";

    private const string ServiceAccessMessage =
        "Нет прав запустить VPN-службу от имени ученика. Один раз выполните Repair-Student-Vpn.cmd от администратора.";

    private readonly VpnOptions options;
    private readonly VpnBridgeClient bridgeClient = new();
    private readonly object gate = new();
    private VpnBridgeClient? bridge;
    private bool bridgeResolved;
    private string? lastError;
    private string? lastBridgeStartError;
    private VpnRuntimeInfo lastRuntime = new(false, false);

    public VpnRuntimeInfo LastRuntime => lastRuntime;

    public VpnController(VpnOptions? options = null)
    {
        VpnNativeBootstrap.Initialize();
        this.options = options ?? new VpnOptions();
    }

    public bool IsServiceAvailable => TryResolveBridge() is not null;
    public string ConfigPath => options.ResolvedConfigPath;

    /// <summary>Waits for the bridge pipe (same as Connect). Use from /verify-vpn.</summary>
    public bool WaitForBridge() => EnsureBridgeReady() is not null;

    public bool IsConnected
    {
        get
        {
            try
            {
                var path = ConfigPath;
                var activeBridge = TryResolveBridge();
                if (activeBridge is not null)
                    return activeBridge.GetStatus(path).Connected;

                return File.Exists(path) && TunnelService.GetStatus(path).Connected;
            }
            catch
            {
                return false;
            }
        }
    }

    public VpnStatus GetStatus()
    {
        var path = ConfigPath;
        var activeBridge = TryResolveBridge();
        if (activeBridge is not null)
        {
            var status = activeBridge.GetStatus(path);
            return status with { LastError = lastError };
        }

        if (!File.Exists(path))
        {
            return new VpnStatus(
                false,
                "config_missing",
                TunnelService.ServiceNameFromConfig(path),
                path,
                false,
                lastError ?? DescribeMissingBridge());
        }

        var direct = TunnelService.GetStatus(path);
        return new VpnStatus(direct.Connected, direct.State, direct.ServiceName, direct.ConfigPath, true, lastError);
    }

    public VpnStatus Connect()
    {
        lock (gate)
        {
            var path = ConfigPath;
            VpnLog.Info("controller", $"Connect path={path}");
            if (!File.Exists(path))
            {
                VpnLog.Error("controller", $"Config missing: {path}");
                throw new FileNotFoundException($"VPN config missing: {path}", path);
            }

            try
            {
                var activeBridge = EnsureBridgeReady();
                if (activeBridge is not null)
                {
                    var bridged = activeBridge.Connect(path);
                    lastError = bridged.Connected ? null : bridged.LastError ?? "VPN не подключился.";
                    lastRuntime = new VpnRuntimeInfo(bridged.Connected, bridged.Connected, null, null, null, lastError);
                    return bridged with { LastError = lastError };
                }

                EnsureDirectAllowed();
                var current = TunnelService.GetStatus(path);
                if (current.Connected)
                {
                    lastError = null;
                    return ToDirectStatus(current, true);
                }

                TunnelService.Connect(path, ephemeral: false);
                Thread.Sleep(400);
                lastError = null;
                var connected = ToDirectStatus(TunnelService.GetStatus(path), true);
                lastRuntime = new VpnRuntimeInfo(connected.Connected, connected.Connected, null, null, null, null);
                return connected;
            }
            catch (Exception error)
            {
                VpnLog.Error("controller", "Connect failed", error);
                throw;
            }
        }
    }

    public VpnStatus Disconnect()
    {
        lock (gate)
        {
            var path = ConfigPath;
            var activeBridge = TryResolveBridge();
            if (activeBridge is not null)
            {
                lastError = null;
                lastRuntime = new VpnRuntimeInfo(false, false);
                return activeBridge.Disconnect(path);
            }

            EnsureDirectAllowed();
            if (File.Exists(path))
                TunnelService.Disconnect(path, waitForStop: true);

            lastError = null;
            lastRuntime = new VpnRuntimeInfo(false, false);
            return File.Exists(path)
                ? ToDirectStatus(TunnelService.GetStatus(path), true)
                : new VpnStatus(false, "stopped", TunnelService.ServiceNameFromConfig(path), path, false, null);
        }
    }

    public VpnRuntimeInfo VerifyReachability(string? checkHost = null, string? region = null)
    {
        var status = GetStatus();
        if (!status.Connected)
        {
            lastRuntime = new VpnRuntimeInfo(false, false, null, region, checkHost, lastError ?? "VPN не подключён.");
            return lastRuntime;
        }

        string? configText = null;
        try
        {
            if (File.Exists(ConfigPath))
                configText = File.ReadAllText(ConfigPath);
        }
        catch
        {
            // probe still uses the requested host
        }

        var host = VpnHealthCheck.ResolveCheckHost(configText, checkHost, VpnRegionCatalog.Resolve(region).CheckHost);
        var handshake = VpnTunnelDiagnostics.WaitForHandshake(ConfigPath, TimeSpan.FromSeconds(6));
        VpnLog.Info("health", $"handshake completed={handshake.Completed} keepalive={handshake.KeepaliveSeen} adapterUp={handshake.AdapterUp} addr={handshake.AdapterAddress ?? "-"} last={handshake.LastLine ?? "-"}");

        if (handshake.Completed || handshake.AdapterUp)
        {
            // Classroom policy: keep the tunnel once WireGuard is up. Exit-node ICMP/HTTP is unreliable.
            var probe = VpnReachability.Probe(host, TimeSpan.FromMilliseconds(1200), attempts: 1);
            if (probe.Ok)
            {
                lastError = null;
                lastRuntime = VpnHealthCheck.FromPing(status with { PingMs = probe.RoundtripMs, CheckHost = probe.Host }, probe, region);
                VpnLog.Info("health", $"traffic OK via {probe.Method} {probe.Host} {probe.RoundtripMs} ms");
                return lastRuntime;
            }

            lastError = $"Handshake/adapter OK, internet probe soft-fail ({probe.Host}): {probe.Error}";
            lastRuntime = new VpnRuntimeInfo(true, true, probe.RoundtripMs, region, probe.Host, lastError);
            VpnLog.Warn("health", lastError);
            return lastRuntime;
        }

        var deepProbe = VpnReachability.Probe(host, TimeSpan.FromMilliseconds(2000), attempts: 2);
        if (deepProbe.Ok)
        {
            lastError = null;
            lastRuntime = VpnHealthCheck.FromPing(status with { PingMs = deepProbe.RoundtripMs, CheckHost = deepProbe.Host }, deepProbe, region);
            VpnLog.Info("health", $"traffic OK without handshake log via {deepProbe.Method} {deepProbe.Host}");
            return lastRuntime;
        }

        lastError = $"Нет handshake WireGuard и трафик не проходит ({deepProbe.Host}): {deepProbe.Error}";
        lastRuntime = new VpnRuntimeInfo(true, false, null, region, deepProbe.Host, lastError);
        VpnLog.Warn("health", lastError);
        return lastRuntime;
    }

    public VpnStatus InstallConfig(ReadOnlySpan<byte> content)
    {
        lock (gate)
        {
            var path = ResolveInstallPath();
            var activeBridge = EnsureBridgeReady();
            if (activeBridge is not null)
            {
                var status = activeBridge.InstallConfig(content.ToArray(), path);
                options.ConfigPath = path;
                lastError = null;
                return status;
            }

            var normalized = VpnConfigNormalizer.NormalizeForClassroom(content);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, normalized);
            options.ConfigPath = path;
            lastError = null;
            return GetStatus();
        }
    }

    /// <summary>
    /// Copies staged Student.exe into the install dir. Uses the SYSTEM bridge when Program Files is not writable.
    /// </summary>
    public bool TryApplyStudentUpdate(string sourceExe, string targetExe)
    {
        try
        {
            var targetDir = Path.GetDirectoryName(targetExe);
            if (!string.IsNullOrWhiteSpace(targetDir) && CanWriteToPath(Path.Combine(targetDir, "probe")))
            {
                File.Copy(sourceExe, targetExe, overwrite: true);
                return true;
            }

            var activeBridge = EnsureBridgeReady();
            if (activeBridge is null)
                return false;

            activeBridge.ApplyUpdate(sourceExe, targetExe);
            return true;
        }
        catch (Exception error)
        {
            VpnLog.Warn("controller", $"TryApplyStudentUpdate failed: {error.Message}");
            return false;
        }
    }

    private VpnBridgeClient? EnsureBridgeReady()
    {
        var activeBridge = TryResolveBridge();
        if (activeBridge is not null || !options.RequireBridge)
            return activeBridge;

        // Never prompt UAC during lessons. Service must be installed once by Setup-Student / Inno / Repair-Student-Vpn.
        if (!bridgeClient.IsServiceInstalled)
        {
            VpnLog.Warn("controller", "VPN bridge service is not installed; refusing interactive UAC install.");
            return null;
        }

        if (!bridgeClient.IsServiceRunning)
        {
            TryStartBridgeService();
            ResetBridgeCache();
        }

        // After install/reboot the service can be Running before the named pipe accepts clients.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            ResetBridgeCache();
            activeBridge = TryResolveBridge();
            if (activeBridge is not null)
                return activeBridge;

            if (!bridgeClient.IsServiceRunning)
            {
                TryStartBridgeService();
                ResetBridgeCache();
            }

            Thread.Sleep(500);
        }

        VpnLog.Warn("controller", "VPN bridge service did not answer pipe ping in time.");
        return null;
    }

    private void ResetBridgeCache()
    {
        bridgeResolved = false;
        bridge = null;
    }

    private void TryStartBridgeService()
    {
        try
        {
            using var controller = new System.ServiceProcess.ServiceController(VpnBridgeConstants.ServiceName);
            if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                lastBridgeStartError = null;
                return;
            }

            controller.Start();
            controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            lastBridgeStartError = null;
            VpnLog.Info("controller", "VPN bridge service started.");
        }
        catch (Exception error)
        {
            lastBridgeStartError = error.Message;
            VpnLog.Warn("controller", $"Could not start VPN bridge service: {error.Message}");
        }
    }

    private VpnBridgeClient? TryResolveBridge()
    {
        if (!options.RequireBridge)
            return null;

        if (bridgeResolved)
            return bridge;

        bridgeResolved = true;
        if (!bridgeClient.IsServiceInstalled)
            return null;

        bridge = bridgeClient.IsServiceRunning && bridgeClient.TryPing()
            ? bridgeClient
            : null;
        VpnLog.Info("controller", bridge is null
            ? "Bridge unavailable (service missing, stopped, or pipe ping failed)"
            : "Bridge available");
        return bridge;
    }

    private string ResolveInstallPath()
    {
        if (TryResolveBridge() is not null || CanWriteToPath(options.InstallTargetPath))
            return options.InstallTargetPath;

        return options.DevConfigPath;
    }

    private static bool CanWriteToPath(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return false;

            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string DescribeMissingBridge()
    {
        if (!options.RequireBridge)
            return string.Empty;

        if (!bridgeClient.IsServiceInstalled)
            return ServiceMissingMessage;

        if (!string.IsNullOrWhiteSpace(lastBridgeStartError)
            && (lastBridgeStartError.Contains("denied", StringComparison.OrdinalIgnoreCase)
                || lastBridgeStartError.Contains("отказано", StringComparison.OrdinalIgnoreCase)
                || lastBridgeStartError.Contains("доступ", StringComparison.OrdinalIgnoreCase)))
            return ServiceAccessMessage;

        if (!bridgeClient.IsServiceRunning)
            return ServiceStoppedMessage;

        return ServicePipeMessage;
    }

    private void EnsureDirectAllowed()
    {
        if (options.RequireBridge)
            throw new InvalidOperationException(DescribeMissingBridge());
    }

    private static VpnStatus ToDirectStatus(TunnelStatus status, bool configExists) =>
        new(status.Connected, status.State, status.ServiceName, status.ConfigPath, configExists, null);
}
