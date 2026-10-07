using System.Runtime.InteropServices;
using System.Diagnostics;
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
    private HashSet<string> allowedApps = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> blockedApps = new(StringComparer.OrdinalIgnoreCase);
    private bool enforceAllowlist;
    private static readonly HashSet<string> SystemApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "searchhost.exe", "applicationframehost.exe", "taskmgr.exe", "kiberone.tutor.exe"
    };

    public bool IsActive { get; private set; }
    public event Action<int>? GameWindowsClosed;

    public void Start(IReadOnlyList<string>? titles = null, IReadOnlyList<string>? apps = null)
    {
        blockedTitles = FocusModeBlocklist.Resolve(titles);
        allowedApps = apps is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(apps.Select(NormalizeAppName).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        IsActive = true;
        enforceAllowlist = allowedApps.Count > 0;
        blockedApps.Clear();
        loop ??= RunAsync(lifetime.Token);
    }

    public void Apply(ClassroomAccessPolicy policy)
    {
        if (!policy.Enabled) { Stop(); return; }
        Start([], policy.AllowedApps);
        blockedTitles = [];
        enforceAllowlist = policy.OnlyAllowedApps;
        blockedApps = new HashSet<string>(policy.BlockedApps.Select(NormalizeAppName), StringComparer.OrdinalIgnoreCase);
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
            var titleBlocked = blocked.Any(item => title.ToString().Contains(item, StringComparison.OrdinalIgnoreCase));
            var appBlocked = false;
            if ((enforceAllowlist || blockedApps.Count > 0) && GetWindowThreadProcessId(window, out var pid) != 0 && pid != Environment.ProcessId)
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    var name = NormalizeAppName(process.ProcessName);
                    appBlocked = !SystemApps.Contains(name) && (enforceAllowlist ? !allowedApps.Contains(name) : blockedApps.Contains(name));
                }
                catch { /* Unknown/system windows stay open. */ }
            }
            if (!titleBlocked && !appBlocked) return true;
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

    private static string NormalizeAppName(string value)
    {
        var name = Path.GetFileName(value.Trim());
        if (name.Length == 0) return string.Empty;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
