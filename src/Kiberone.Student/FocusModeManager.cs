using System.Runtime.InteropServices;
using System.Text;
using Kiberone.Core;

namespace Kiberone.Student;

internal sealed class FocusModeManager : IAsyncDisposable
{
    private const uint WmClose = 0x0010;
    private readonly CancellationTokenSource lifetime = new();
    private Task? loop;
    private int closedCount;
    private bool thresholdReported;
    private string[] blockedTitles = FocusModeBlocklist.Defaults;

    public bool IsActive { get; private set; }
    public event Action<int>? GameWindowsClosed;

    public void Start(IReadOnlyList<string>? titles = null)
    {
        blockedTitles = FocusModeBlocklist.Resolve(titles);
        IsActive = true;
        loop ??= RunAsync(lifetime.Token);
    }

    public void Stop() => IsActive = false;

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (IsActive) CloseBlockedWindows();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private void CloseBlockedWindows()
    {
        var blocked = blockedTitles;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var length = GetWindowTextLength(window);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            _ = GetWindowText(window, title, title.Capacity);
            if (!blocked.Any(item => title.ToString().Contains(item, StringComparison.OrdinalIgnoreCase))) return true;
            if (PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero))
            {
                closedCount++;
                if (closedCount >= 3 && !thresholdReported)
                {
                    thresholdReported = true;
                    GameWindowsClosed?.Invoke(closedCount);
                }
            }
            return true;
        }, IntPtr.Zero);
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (loop is not null) try { await loop; } catch (OperationCanceledException) { }
        lifetime.Dispose();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
