using Kiberone.Vpn.WireGuard;
using Kiberone.Core;

namespace Kiberone.Vpn;

public sealed class VpnController
{
    private const string ServiceMissingMessage =
        "VPN-служба не установлена. Подтвердите запрос UAC или запустите Install-Student.cmd от администратора.";

    private const string ServiceStoppedMessage =
        "VPN-служба установлена, но не запущена. Выполните: sc start KIBERoneStudentVpn";

    private readonly VpnOptions options;
    private readonly VpnBridgeClient bridgeClient = new();
    private readonly object gate = new();
    private VpnBridgeClient? bridge;
    private bool bridgeResolved;
    private string? lastError;
    private VpnRuntimeInfo lastRuntime = new(false, false);

    public VpnRuntimeInfo LastRuntime => lastRuntime;

    public VpnController(VpnOptions? options = null)
    {
        VpnNativeBootstrap.Initialize();
        this.options = options ?? new VpnOptions();
    }

    public bool IsServiceAvailable => TryResolveBridge() is not null;
    public string ConfigPath => options.ResolvedConfigPath;

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
            // ping still uses the requested host
        }

        var host = VpnHealthCheck.ResolveCheckHost(configText, checkHost, VpnRegionCatalog.Resolve(region).CheckHost);
        var ping = VpnReachability.Ping(host, TimeSpan.FromMilliseconds(800), attempts: 3);
        lastRuntime = VpnHealthCheck.FromPing(status with { PingMs = ping.RoundtripMs, CheckHost = host }, ping, region);
        if (!ping.Ok)
            lastError = $"VPN подключён, но ping {host} не прошёл: {ping.Error}";
        else
            lastError = null;
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

    private VpnBridgeClient? EnsureBridgeReady()
    {
        var activeBridge = TryResolveBridge();
        if (activeBridge is not null || !options.RequireBridge)
            return activeBridge;

        if (!bridgeClient.IsServiceInstalled)
        {
            if (VpnServiceInstaller.TryInstallInPlace())
                ResetBridgeCache();
        }
        else if (!bridgeClient.IsServiceRunning)
        {
            TryStartBridgeService();
            ResetBridgeCache();
        }

        return TryResolveBridge();
    }

    private void ResetBridgeCache()
    {
        bridgeResolved = false;
        bridge = null;
    }

    private static void TryStartBridgeService()
    {
        try
        {
            using var controller = new System.ServiceProcess.ServiceController(VpnBridgeConstants.ServiceName);
            if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                return;

            controller.Start();
            controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            VpnLog.Info("controller", "VPN bridge service started.");
        }
        catch (Exception error)
        {
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

        if (bridgeClient.IsServiceInstalled && !bridgeClient.IsServiceRunning)
            return ServiceStoppedMessage;

        return ServiceMissingMessage;
    }

    private void EnsureDirectAllowed()
    {
        if (options.RequireBridge)
            throw new InvalidOperationException(DescribeMissingBridge());
    }

    private static VpnStatus ToDirectStatus(TunnelStatus status, bool configExists) =>
        new(status.Connected, status.State, status.ServiceName, status.ConfigPath, configExists, null);
}
