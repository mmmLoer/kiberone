using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Kiberone.Student.ViewModels;
using Kiberone.Student.Views;

namespace Kiberone.Student;

internal sealed class ScreenLockManager : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int VkTab = 0x09;
    private const int VkEscape = 0x1B;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkF4 = 0x73;
    private const int VkSpace = 0x20;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int GwlExStyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int SwpNomove = 0x0002;
    private const int SwpNosize = 0x0001;
    private const int SwpShowwindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly List<LockOverlayWindow> overlays = [];
    private readonly LowLevelKeyboardProc hookProc;
    private IntPtr hook;
    private CancellationTokenSource? keeper;
    private Task? keeperTask;
    private bool active;

    public ScreenLockManager()
    {
        hookProc = HookCallback;
    }

    public bool IsActive => active;

    public void Show(Window owner, MainViewModel viewModel)
    {
        if (active) return;
        active = true;
        CreateOverlays(owner, viewModel);
        InstallHook();
        keeper = new CancellationTokenSource();
        keeperTask = KeepOnTopAsync(keeper.Token);
    }

    public void Hide()
    {
        active = false;
        RemoveHook();
        keeper?.Cancel();
        keeperTask = null;
        keeper?.Dispose();
        keeper = null;
        foreach (var overlay in overlays.ToList())
        {
            overlay.AllowClose = true;
            try { overlay.Close(); } catch { }
        }
        overlays.Clear();
    }

    private void CreateOverlays(Window owner, MainViewModel viewModel)
    {
        var screens = owner.Screens?.All;
        if (screens is null || screens.Count == 0)
        {
            OpenOverlay(owner, viewModel, null);
            return;
        }

        foreach (var screen in screens)
            OpenOverlay(owner, viewModel, screen);
    }

    private void OpenOverlay(Window owner, MainViewModel viewModel, Avalonia.Platform.Screen? screen)
    {
        var overlay = new LockOverlayWindow { DataContext = viewModel };
        if (screen is not null)
        {
            overlay.Position = screen.Bounds.Position;
            overlay.Width = screen.Bounds.Width / screen.Scaling;
            overlay.Height = screen.Bounds.Height / screen.Scaling;
        }

        overlay.Opened += (_, _) =>
        {
            ApplyLockWindowStyle(overlay);
            overlay.Activate();
        };
        overlay.Show();
        overlay.WindowState = WindowState.FullScreen;
        overlays.Add(overlay);
    }

    private void ApplyLockWindowStyle(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        var ex = GetWindowLongPtr(handle, GwlExStyle);
        _ = SetWindowLongPtr(handle, GwlExStyle, ex | (IntPtr)(WsExToolwindow | WsExTopmost));
        _ = SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpShowwindow);
    }

    private async Task KeepOnTopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && active)
        {
            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var overlay in overlays)
                    {
                        if (!overlay.IsVisible) continue;
                        overlay.Topmost = false;
                        overlay.Topmost = true;
                        ApplyLockWindowStyle(overlay);
                        overlay.Activate();
                    }
                });
            }
            catch
            {
            }

            try
            {
                await Task.Delay(400, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void InstallHook()
    {
        if (hook != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = GetModuleHandle(module?.ModuleName);
        hook = SetWindowsHookEx(WhKeyboardLl, hookProc, moduleHandle, 0);
    }

    private void RemoveHook()
    {
        if (hook == IntPtr.Zero) return;
        _ = UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && active)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (ShouldBlock(info.VkCode))
                return (IntPtr)1;
        }

        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private static bool ShouldBlock(int vk)
    {
        var alt = IsDown(VkLMenu) || IsDown(VkRMenu);
        var control = IsDown(VkLControl) || IsDown(VkRControl);

        if (vk is VkLWin or VkRWin) return true;
        if (alt && vk is VkTab or VkEscape or VkF4 or VkSpace) return true;
        if (control && vk == VkEscape) return true;
        if (control && vk == VkTab) return true;
        return false;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => Hide();

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
