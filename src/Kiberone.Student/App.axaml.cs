using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kiberone.Student.ViewModels;
using Kiberone.Student.Views;
using Kiberone.Infrastructure;
using Kiberone.Vpn;
using Kiberone.Core;
using Avalonia.Threading;

namespace Kiberone.Student;

public partial class App : Avalonia.Application
{
    private StudentAgent? agent;
    private FocusModeManager? focusMode;
    private WatchdogManager? watchdog;
    private VpnController? vpn;
    private MainViewModel? viewModel;
    private ScreenLockManager? screenLock;

    private VpnRuntimeInfo lastVpnRuntime = new(false, false);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            screenLock = new ScreenLockManager();
            viewModel.ScreenLockChanged = locked => Dispatcher.UIThread.Post(() =>
            {
                if (desktop.MainWindow is null || screenLock is null || viewModel is null) return;
                if (locked) screenLock.Show(desktop.MainWindow, viewModel);
                else screenLock.Hide();
            });
            agent = new StudentAgent();
            vpn = new VpnController();
            agent.VpnStateProvider = () => lastVpnRuntime.Connected;
            agent.VpnRuntimeProvider = () => lastVpnRuntime;
            agent.BatteryProvider = BatteryInfo.TryGetBatteryPercent;
            agent.VpnCommandHandler = HandleVpnCommand;
            agent.LaunchInstaller = DesktopWallpaper.LaunchInstaller;
            agent.ApplyWallpaperFile = DesktopWallpaper.Apply;
            focusMode = new FocusModeManager();
            watchdog = new WatchdogManager();
            focusMode.GameWindowsClosed += _ => agent.QueueClientEvent("games_addict");
            viewModel.FocusEnabled = focusMode.Start;
            viewModel.FocusDisabled = focusMode.Stop;
            viewModel.WatchdogEnabled = watchdog.Start;
            viewModel.WatchdogDisabled = watchdog.Stop;
            if (watchdog.ConsumeRestartSentinel())
            {
                agent.QueueClientEvent("watchdog_survivor");
                watchdog.Start();
            }
            agent.ScreenProvider = ScreenCapture.CaptureJpeg;
            agent.FocusModeStateProvider = () => focusMode.IsActive;
            agent.WatchdogStateProvider = () => watchdog.IsActive;
            agent.ScreenLockStateProvider = () => viewModel.IsScreenLocked;
            agent.ConnectionChanged += state => Dispatcher.UIThread.Post(() => viewModel.SetConnection(state));
            agent.SyncStateChanged += state => Dispatcher.UIThread.Post(() => viewModel.SetSyncState(state));
            agent.UpdateAvailable += update => Dispatcher.UIThread.Post(() => viewModel.SetUpdate(update));
            agent.UpdateStateChanged += state => Dispatcher.UIThread.Post(() => viewModel.SetUpdateState(state));
            agent.StudentsAvailable += students => Dispatcher.UIThread.Post(() => viewModel.SetStudents(students, agent.PreferredGroupName));
            agent.PreferredGroupChanged += group => Dispatcher.UIThread.Post(() => viewModel.ApplyPreferredGroup(group));
            viewModel.UpdateRequested = agent.RequestUpdateInstallation;
            viewModel.QuizAnswerRequested = agent.SubmitQuizAnswer;
            viewModel.StudentSelected = agent.AssignStudent;
            agent.CommandHandler = async (command, _) => await Dispatcher.UIThread.InvokeAsync(() => viewModel.ApplyCommand(command));
            agent.Start();
            desktop.Exit += (_, _) =>
            {
                screenLock?.Hide();
                focusMode?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                agent?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private CommandExecutionResult HandleVpnCommand(ClassroomCommand command)
    {
        if (vpn is null)
            return ReportVpnFailure("VPN не инициализирован.");

        try
        {
            var result = command.Kind switch
            {
                ClassroomCommandKinds.VpnInstallConfig => HandleVpnInstallConfig(command),
                ClassroomCommandKinds.VpnConnect => ConnectWithHealth(command),
                ClassroomCommandKinds.VpnDisconnect => ReportVpnDisconnected(),
                ClassroomCommandKinds.VpnStatus => ReportVpnStatus(),
                _ => ReportVpnFailure($"Неизвестная VPN-команда: {command.Kind}")
            };
            return result;
        }
        catch (Exception error)
        {
            return ReportVpnFailure(error.Message);
        }
    }

    private CommandExecutionResult HandleVpnInstallConfig(ClassroomCommand command)
    {
        if (vpn is null)
            return ReportVpnFailure("VPN не инициализирован.");

        if (!command.Payload.TryGetProperty("config_base64", out var encoded) || encoded.GetString() is not { Length: > 0 } base64)
            return ReportVpnFailure("В команде нет VPN-конфига.");

        byte[] content;
        try
        {
            content = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return ReportVpnFailure("Некорректный VPN-конфиг (base64).");
        }

        if (content.Length == 0)
            return ReportVpnFailure("Пустой VPN-конфиг.");

        vpn.InstallConfig(content);
        UpdateVpnUi(vpn.GetStatus(), "Конфиг получен от тьютора");

        var autoConnect = !command.Payload.TryGetProperty("auto_connect", out var connectFlag) || connectFlag.GetBoolean();
        if (!autoConnect)
            return CommandExecutionResult.Success;

        var checkHost = ReadString(command, "check_host");
        var region = ReadString(command, "vpn_region");
        var connected = ConnectWithHealth(checkHost, region);
        if (connected.Succeeded)
            return connected;

        if (!command.Payload.TryGetProperty("fallback_config_base64", out var fallbackEncoded)
            || fallbackEncoded.GetString() is not { Length: > 0 } fallbackBase64)
            return connected;

        try
        {
            vpn.InstallConfig(Convert.FromBase64String(fallbackBase64));
        }
        catch (Exception error)
        {
            return ReportVpnFailure($"Основной VPN не ответил, запасной конфиг не принят: {error.Message}");
        }

        var fallbackRegion = ReadString(command, "fallback_vpn_region") ?? region;
        var fallbackHost = ReadString(command, "fallback_check_host") ?? checkHost;
        var fallback = ConnectWithHealth(fallbackHost, fallbackRegion);
        return fallback.Succeeded
            ? fallback
            : ReportVpnFailure($"Не ответили ни основной, ни запасной VPN. {fallback.Error}");
    }

    private CommandExecutionResult ConnectWithHealth(ClassroomCommand command) =>
        ConnectWithHealth(ReadString(command, "check_host"), ReadString(command, "vpn_region"));

    private CommandExecutionResult ConnectWithHealth(string? checkHost, string? region)
    {
        if (vpn is null)
            return ReportVpnFailure("VPN не инициализирован.");

        var connected = false;
        try
        {
            var status = vpn.Connect();
            if (!status.Connected)
                return ReportVpnFailure(status.LastError ?? "Не удалось подключить VPN.");

            connected = true;
            // Handshake + routes usually finish within 1–3 s (see WireGuard log.bin).
            Thread.Sleep(2500);
            lastVpnRuntime = vpn.VerifyReachability(checkHost, region);
            if (!lastVpnRuntime.Healthy)
            {
                VpnLog.Warn("student", $"VPN health failed after connect: {lastVpnRuntime.Error}. Rolling back tunnel.");
                SafeDisconnect();
                connected = false;
                lastVpnRuntime = new VpnRuntimeInfo(false, false, null, region, lastVpnRuntime.CheckHost, lastVpnRuntime.Error);
                return ReportVpnFailure(lastVpnRuntime.Error ?? "VPN поднялся, но интернет через него не заработал. Туннель отключён.");
            }

            UpdateVpnUi(vpn.GetStatus() with
            {
                PingMs = lastVpnRuntime.PingMs,
                CheckHost = lastVpnRuntime.CheckHost,
                LastError = null
            }, null);

            return CommandExecutionResult.Success;
        }
        catch (Exception error)
        {
            if (connected)
                SafeDisconnect();
            return ReportVpnFailure(error.Message);
        }
    }

    private void SafeDisconnect()
    {
        try { vpn?.Disconnect(); }
        catch (Exception error) { VpnLog.Warn("student", $"Rollback disconnect failed: {error.Message}"); }
    }

    private void UpdateVpnUi(VpnStatus? status, string? detail)
    {
        var vm = viewModel;
        if (vm is null) return;
        Dispatcher.UIThread.Post(() => vm.SetVpnState(status, detail));
    }

    private static string? ReadString(ClassroomCommand command, string name) =>
        command.Payload.TryGetProperty(name, out var value) ? value.GetString() : null;

    private CommandExecutionResult ReportVpnSuccess()
    {
        UpdateVpnUi(vpn?.GetStatus(), null);
        return CommandExecutionResult.Success;
    }

    private CommandExecutionResult ReportVpnStatus()
    {
        UpdateVpnUi(vpn?.GetStatus(), null);
        return CommandExecutionResult.Success;
    }

    private CommandExecutionResult ReportVpnDisconnected()
    {
        vpn?.Disconnect();
        lastVpnRuntime = new VpnRuntimeInfo(false, false);
        UpdateVpnUi(vpn?.GetStatus(), "Отключён тьютором");
        return CommandExecutionResult.Success;
    }

    private CommandExecutionResult ReportVpnFailure(string message)
    {
        if (message.Contains("VPN-служба не установлена", StringComparison.Ordinal))
            message += " Подтвердите UAC при первом включении VPN или запустите Repair-Student-Vpn.cmd от администратора.";
        VpnLog.Error("student", $"VPN command failed: {message}");
        if (!message.Contains("vpn.log", StringComparison.OrdinalIgnoreCase))
            message += $" Лог: {VpnLog.PrimaryLogPath}";
        UpdateVpnUi(vpn?.GetStatus(), message);
        return new CommandExecutionResult(false, message);
    }
}
