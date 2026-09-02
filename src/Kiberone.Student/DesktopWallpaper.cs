using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kiberone.Infrastructure;

namespace Kiberone.Student;

[SupportedOSPlatform("windows")]
internal static class DesktopWallpaper
{
    private const int SpiSetDeskWallpaper = 20;
    private const int SpifUpdateIniFile = 0x01;
    private const int SpifSendWinIniChange = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int action, int param, string p, int winIni);

    public static CommandExecutionResult Apply(string path)
    {
        if (!File.Exists(path))
            return new CommandExecutionResult(false, "Файл обоев не найден.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "wallpaper");
        Directory.CreateDirectory(directory);
        var bitmapPath = Path.Combine(directory, "applied.bmp");
        using (var image = Image.FromFile(path))
            image.Save(bitmapPath, ImageFormat.Bmp);
        return SystemParametersInfo(SpiSetDeskWallpaper, 0, bitmapPath, SpifUpdateIniFile | SpifSendWinIniChange)
            ? CommandExecutionResult.Success
            : new CommandExecutionResult(false, "Windows не принял файл обоев.");
    }

    public static CommandExecutionResult LaunchInstaller(string path)
    {
        if (!File.Exists(path))
            return new CommandExecutionResult(false, "Установщик не найден.");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        Process.Start(start);
        return CommandExecutionResult.Success;
    }
}
