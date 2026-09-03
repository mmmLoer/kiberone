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

        if (args.Any(arg => string.Equals(arg, "/verify-vpn", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(VpnInstallVerifier.Run());
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
