using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kiberone.Tutor.ViewModels;
using Kiberone.Tutor.Views;
using Kiberone.Infrastructure;
using System.IO;
using System.Security.Cryptography;
using Avalonia.Threading;

namespace Kiberone.Tutor;

public partial class App : Application
{
    private ClassroomServer? server;
    private DiscoveryAnnouncer? discovery;
    private DispatcherTimer? clientRefreshTimer;
    private bool isShuttingDown;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom");
            Directory.CreateDirectory(dataDirectory);
            var databaseOptions = ClassroomDatabase.CreateOptions(Path.Combine(dataDirectory, "classroom.db"));

            // Avalonia already has a UI SynchronizationContext here. Blocking GetResult() on that
            // thread deadlocks as soon as EF/file I/O posts a continuation back to the dispatcher.
            StartupServices started;
            try
            {
                started = Task.Run(() => StartClassroomAsync(dataDirectory, databaseOptions)).GetAwaiter().GetResult();
            }
            catch (IOException ex) when (ex.Message.Contains("8765", StringComparison.Ordinal) || ex.InnerException?.Message?.Contains("address already in use", StringComparison.OrdinalIgnoreCase) == true)
            {
                Win32.MessageBox(
                    IntPtr.Zero,
                    "Порт 8765 уже занят — вероятно, Tutor остался в фоне.\r\n\r\nЗавершите процессы Kiberone.Tutor в диспетчере задач или выполните:\r\nStop-Process -Name Kiberone.Tutor -Force",
                    "KIBERone Tutor",
                    Win32.MB_ICONWARNING);
                desktop.Shutdown();
                return;
            }

            server = started.Server;
            discovery = started.Discovery;
            var viewModel = new MainViewModel(started.Lessons, started.Classroom, started.FileSync, started.Assets, started.Clients, started.Commands, started.Quizzes, started.Audit)
            {
                LiveState = started.LiveState
            };
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            viewModel.VpnConfigsFolderPicker = () => mainWindow.PickVpnConfigsFolderAsync();
            viewModel.StudentSavesFolderPicker = () => mainWindow.PickStudentSavesFolderAsync();
            viewModel.QuizExportPathPicker = () => mainWindow.PickQuizExportPathAsync();
            viewModel.QuizImportPathPicker = () => mainWindow.PickQuizImportPathAsync();
            viewModel.QuizMediaPathPicker = () => mainWindow.PickQuizMediaPathAsync();
            viewModel.StarterFilesPicker = () => mainWindow.PickStarterFilesAsync();
            viewModel.StarterFolderPicker = () => mainWindow.PickStarterFolderAsync();
            viewModel.WallpaperFilePicker = () => mainWindow.PickWallpaperFileAsync();
            desktop.MainWindow = mainWindow;
            _ = viewModel.InitializeAsync();
            clientRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            clientRefreshTimer.Tick += (_, _) =>
            {
                if (isShuttingDown) return;
                try
                {
                    viewModel.RefreshClients();
                    if (viewModel.ShowClassScreens || viewModel.IsSection5)
                        _ = viewModel.RefreshScreensAsync();
                }
                catch (Exception error)
                {
                    CrashLog.Write("ClientRefreshTimer", error);
                }
            };
            clientRefreshTimer.Start();
            viewModel.RefreshClients();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task<StartupServices> StartClassroomAsync(string dataDirectory, Microsoft.EntityFrameworkCore.DbContextOptions<ClassroomDbContext> databaseOptions)
    {
        await ClassroomDatabase.InitializeAsync(databaseOptions).ConfigureAwait(false);
        await ClassroomDatabase.SeedDefaultsAsync(databaseOptions).ConfigureAwait(false);
        var classroomService = new ClassroomService(databaseOptions);

        var lessonService = new TypingLessonService(databaseOptions);
        var fileSyncService = new FileSyncService(databaseOptions, Path.Combine(dataDirectory, "sync"));
        var assetService = new AssetDistributionService(AppContext.BaseDirectory, dataDirectory);
        var tokenPath = Path.Combine(dataDirectory, "sync-token.txt");
        var token = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, token);
        var serverOptions = new ClassroomServerOptions(token);
        var clientRegistry = new ClientRegistry();
        var commandQueue = new ReliableCommandQueue(clientRegistry);
        var quizService = new QuizService(databaseOptions, clientRegistry, commandQueue);
        var auditService = new AuditService(databaseOptions);
        var liveState = new ClassroomLiveState();
        var classroomServer = new ClassroomServer(serverOptions, lessonService, classroomService, fileSyncService, assetService, quizService, auditService, clientRegistry, commandQueue)
        {
            LiveState = liveState
        };
        await classroomServer.StartAsync().ConfigureAwait(false);
        var serverIdPath = Path.Combine(dataDirectory, "server-id.txt");
        var serverId = File.Exists(serverIdPath) ? File.ReadAllText(serverIdPath).Trim() : Guid.NewGuid().ToString("N");
        if (!File.Exists(serverIdPath)) File.WriteAllText(serverIdPath, serverId);
        var announcer = new DiscoveryAnnouncer(serverOptions, serverId);
        announcer.Start();
        return new StartupServices(classroomServer, announcer, lessonService, classroomService, fileSyncService, assetService, clientRegistry, commandQueue, quizService, auditService, liveState);
    }

    private sealed record StartupServices(
        ClassroomServer Server,
        DiscoveryAnnouncer Discovery,
        TypingLessonService Lessons,
        ClassroomService Classroom,
        FileSyncService FileSync,
        AssetDistributionService Assets,
        ClientRegistry Clients,
        ReliableCommandQueue Commands,
        QuizService Quizzes,
        AuditService Audit,
        ClassroomLiveState LiveState);

    internal void ShutdownServicesAndExit()
    {
        if (isShuttingDown) return;
        isShuttingDown = true;

        try { clientRefreshTimer?.Stop(); } catch { }

        try
        {
            discovery?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch { }

        try
        {
            server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch { }

        discovery = null;
        server = null;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
