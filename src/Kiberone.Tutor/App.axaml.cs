using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kiberone.Tutor.ViewModels;
using Kiberone.Tutor.Views;
using Kiberone.Infrastructure;
using System.Security.Cryptography;
using Avalonia.Threading;

namespace Kiberone.Tutor;

public partial class App : Application
{
    private ClassroomServer? server;
    private DiscoveryAnnouncer? discovery;
    private DispatcherTimer? clientRefreshTimer;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
            server.StartAsync().GetAwaiter().GetResult();
            var serverIdPath = Path.Combine(dataDirectory, "server-id.txt");
            var serverId = File.Exists(serverIdPath) ? File.ReadAllText(serverIdPath).Trim() : Guid.NewGuid().ToString("N");
            if (!File.Exists(serverIdPath)) File.WriteAllText(serverIdPath, serverId);
            discovery = new DiscoveryAnnouncer(serverOptions, serverId);
            discovery.Start();
            var viewModel = new MainViewModel(lessonService, classroomService, fileSyncService, assetService, clientRegistry, commandQueue, quizService, auditService);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.Exit += (_, _) =>
            {
                clientRefreshTimer?.Stop();
                discovery?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            };
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
}
