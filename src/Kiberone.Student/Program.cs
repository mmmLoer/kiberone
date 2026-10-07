using Avalonia;
using Kiberone.Vpn;

namespace Kiberone.Student;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) StudentCrashLog.Write("UnhandledException", error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            StudentCrashLog.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
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

internal static class StudentCrashLog
{
    public static void Write(string source, Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "student-crash.log"),
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* Logging must not cause another failure. */ }
    }
}
