using Avalonia;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Kiberone.Tutor;

sealed class Program
{
    private const string SingleInstanceMutexName = "Global\\Kiberone.Tutor.SingleInstance";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
                CrashLog.Write("UnhandledException", error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        using var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            Win32.MessageBox(
                IntPtr.Zero,
                "KIBERone Tutor уже запущен.\r\nЕсли окно не видно, завершите процесс Kiberone.Tutor в диспетчере задач.",
                "KIBERone Tutor",
                Win32.MB_ICONWARNING);
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error)
        {
            CrashLog.Write("Main", error);
            Win32.MessageBox(
                IntPtr.Zero,
                "KIBERone Tutor остановился из‑за ошибки.\r\n\r\n" + error.Message,
                "KIBERone Tutor",
                Win32.MB_ICONWARNING);
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

internal static class CrashLog
{
    public static void Write(string source, Exception error)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KIBERone Classroom");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "tutor-crash.log"),
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
