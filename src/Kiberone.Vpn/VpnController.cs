using Kiberone.Vpn.WireGuard;

namespace Kiberone.Vpn;

public sealed class VpnController
{
    private const string ServiceMissingMessage =
        "VPN-служба не установлена. Один раз запустите от администратора: scripts\\install-student-vpn-service.ps1";

    private const string ServiceStoppedMessage =
        "VPN-служба установлена, но не запущена. Выполните: sc start KIBERoneStudentVpn";

    private readonly VpnOptions options;
    private readonly VpnBridgeClient bridgeClient = new();
    private readonly object gate = new();
    private VpnBridgeClient? bridge;
    private bool bridgeResolved;
    private string? lastError;

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
                var activeBridge = TryResolveBridge();
                if (activeBridge is not null)
                {
                    var bridged = activeBridge.Connect(path);
                    lastError = bridged.Connected ? null : bridged.LastError ?? "VPN не подключился.";
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
                return ToDirectStatus(TunnelService.GetStatus(path), true);
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
                return activeBridge.Disconnect(path);
            }

            EnsureDirectAllowed();
            if (File.Exists(path))
                TunnelService.Disconnect(path, waitForStop: true);

            lastError = null;
            return File.Exists(path)
                ? ToDirectStatus(TunnelService.GetStatus(path), true)
                : new VpnStatus(false, "stopped", TunnelService.ServiceNameFromConfig(path), path, false, null);
        }
    }

    public VpnStatus InstallConfig(ReadOnlySpan<byte> content)
    {
        lock (gate)
        {
            var path = ResolveInstallPath();
            var activeBridge = TryResolveBridge();
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
