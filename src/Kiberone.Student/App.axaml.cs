using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kiberone.Student.ViewModels;
using Kiberone.Student.Views;
using Kiberone.Infrastructure;
using Avalonia.Threading;

namespace Kiberone.Student;

public partial class App : Avalonia.Application
{
    private StudentAgent? agent;
    private FocusModeManager? focusMode;
    private WatchdogManager? watchdog;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            agent = new StudentAgent();
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
}
