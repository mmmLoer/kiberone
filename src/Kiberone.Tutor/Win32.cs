using System.Runtime.InteropServices;

namespace Kiberone.Tutor;

internal static class Win32
{
    public const uint MB_ICONWARNING = 0x00000030;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
