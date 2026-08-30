using Avalonia;
using Kiberone.Vpn;

namespace Kiberone.Student;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VpnNativeBootstrap.Initialize();
        if (VpnServiceEntry.TryRunService(args, out var exitCode))
        {
            Environment.Exit(exitCode);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
