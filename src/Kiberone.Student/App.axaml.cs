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
            agent = new StudentAgent();
            vpn = new VpnController();
            agent.VpnStateProvider = () => vpn.IsConnected;
            agent.BatteryProvider = BatteryInfo.TryGetBatteryPercent;
            agent.VpnCommandHandler = HandleVpnCommand;
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
            agent.ConnectionChanged += state => Dispatcher.UIThread.Post(() => viewModel.SetConnection(state));
            agent.SyncStateChanged += state => Dispatcher.UIThread.Post(() => viewModel.SetSyncState(state));
            agent.UpdateAvailable += update => Dispatcher.UIThread.Post(() => viewModel.SetUpdate(update));
            agent.UpdateStateChanged += state => Dispatcher.UIThread.Post(() => viewModel.SetUpdateState(state));
            agent.StudentsAvailable += students => Dispatcher.UIThread.Post(() => viewModel.SetStudents(students));
            viewModel.UpdateRequested = agent.RequestUpdateInstallation;
            viewModel.QuizAnswerRequested = agent.SubmitQuizAnswer;
            viewModel.StudentSelected = agent.AssignStudent;
            agent.CommandHandler = async (command, _) => await Dispatcher.UIThread.InvokeAsync(() => viewModel.ApplyCommand(command));
            agent.Start();
            desktop.Exit += (_, _) =>
            {
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
                ClassroomCommandKinds.VpnConnect => vpn.Connect().Connected
                    ? ReportVpnSuccess()
                    : ReportVpnFailure(vpn.GetStatus().LastError ?? "Не удалось подключить VPN."),
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
        viewModel?.SetVpnState(vpn.GetStatus(), "Конфиг получен от тьютора");

        var autoConnect = command.Payload.TryGetProperty("auto_connect", out var connectFlag) && connectFlag.GetBoolean();
        if (!autoConnect)
            return CommandExecutionResult.Success;

        return vpn.Connect().Connected
            ? ReportVpnSuccess()
            : ReportVpnFailure(vpn.GetStatus().LastError ?? "Конфиг установлен, но VPN не подключился.");
    }

    private CommandExecutionResult ReportVpnSuccess()
    {
        viewModel?.SetVpnState(vpn?.GetStatus(), null);
        return CommandExecutionResult.Success;
    }

    private CommandExecutionResult ReportVpnStatus()
    {
        viewModel?.SetVpnState(vpn?.GetStatus(), null);
        return CommandExecutionResult.Success;
    }

    private CommandExecutionResult ReportVpnDisconnected()
    {
        vpn?.Disconnect();
        viewModel?.SetVpnState(vpn?.GetStatus(), "Отключён тьютором");
        return CommandExecutionResult.Success;
    }

    private CommandExecutionResult ReportVpnFailure(string message)
    {
        if (message.Contains("VPN-служба не установлена", StringComparison.Ordinal))
            message += " Служба ставится один раз при установке Student на ПК.";
        VpnLog.Error("student", $"VPN command failed: {message}");
        if (!message.Contains("vpn.log", StringComparison.OrdinalIgnoreCase))
            message += $" Лог: {VpnLog.PrimaryLogPath}";
        viewModel?.SetVpnState(vpn?.GetStatus(), message);
        return new CommandExecutionResult(false, message);
    }
}
