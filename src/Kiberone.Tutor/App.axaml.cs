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
            ClassroomDatabase.InitializeAsync(databaseOptions).GetAwaiter().GetResult();
            ClassroomDatabase.SeedDefaultsAsync(databaseOptions).GetAwaiter().GetResult();
            var lessonService = new TypingLessonService(databaseOptions);
            var classroomService = new ClassroomService(databaseOptions);
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
            server = new ClassroomServer(serverOptions, lessonService, classroomService, fileSyncService, assetService, quizService, auditService, clientRegistry, commandQueue);
            try
            {
                server.StartAsync().GetAwaiter().GetResult();
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
            var serverIdPath = Path.Combine(dataDirectory, "server-id.txt");
            var serverId = File.Exists(serverIdPath) ? File.ReadAllText(serverIdPath).Trim() : Guid.NewGuid().ToString("N");
            if (!File.Exists(serverIdPath)) File.WriteAllText(serverIdPath, serverId);
            discovery = new DiscoveryAnnouncer(serverOptions, serverId);
            discovery.Start();
            var viewModel = new MainViewModel(lessonService, classroomService, fileSyncService, assetService, clientRegistry, commandQueue, quizService, auditService);
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            viewModel.VpnConfigsFolderPicker = () => mainWindow.PickVpnConfigsFolderAsync();
            viewModel.QuizExportPathPicker = () => mainWindow.PickQuizExportPathAsync();
            viewModel.QuizImportPathPicker = () => mainWindow.PickQuizImportPathAsync();
            viewModel.QuizMediaPathPicker = () => mainWindow.PickQuizMediaPathAsync();
            desktop.MainWindow = mainWindow;
            _ = viewModel.InitializeAsync();
            clientRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            clientRefreshTimer.Tick += async (_, _) =>
            {
                viewModel.RefreshClients();
                await viewModel.RefreshScreensAsync();
            };
            clientRefreshTimer.Start();
            viewModel.RefreshClients();
        }

        base.OnFrameworkInitializationCompleted();
    }

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

        Environment.Exit(0);
    }
}
