using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kiberone.Tutor;

internal static class Win32
{
    public const uint MB_ICONWARNING = 0x00000030;

    public static int MessageBox(IntPtr hWnd, string text, string caption, uint type)
    {
        if (OperatingSystem.IsWindows())
            return NativeMessageBox(hWnd, text, caption, type);

        try
        {
            var escapedCaption = EscapeAppleScript(caption);
            var escapedText = EscapeAppleScript(text.Replace("\r\n", "\n"));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", $"display alert \"{escapedCaption}\" message \"{escapedText}\" as warning" },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(8000);
        }
        catch
        {
            Console.Error.WriteLine($"{caption}: {text}");
        }

        return 0;
    }

    private static string EscapeAppleScript(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int NativeMessageBox(IntPtr hWnd, string text, string caption, uint type);
}
