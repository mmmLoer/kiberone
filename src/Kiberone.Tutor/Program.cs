using Avalonia;
using System;
using System.Threading;

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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
