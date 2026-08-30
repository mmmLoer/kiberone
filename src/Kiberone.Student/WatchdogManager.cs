using System.Diagnostics;

namespace Kiberone.Student;

internal sealed class WatchdogManager
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "KIBERone-Classroom-Watchdog");
    private string StopPath => Path.Combine(directory, "stop.flag");
    public string SentinelPath => Path.Combine(directory, "restarted.flag");
    private string ScriptPath => Path.Combine(directory, "watchdog.cmd");

    public bool CanRun => Environment.ProcessPath is { } path && Path.GetFileName(path).Contains("KIBERoneStudent", StringComparison.OrdinalIgnoreCase);
    public bool IsActive { get; private set; }

    public bool ConsumeRestartSentinel()
    {
        if (!File.Exists(SentinelPath)) return false;
        File.Delete(SentinelPath);
        return true;
    }

    public void Start()
    {
        if (IsActive) return;
        if (!CanRun) throw new InvalidOperationException("Watchdog доступен только в собранном KIBERoneStudent.exe.");
        Directory.CreateDirectory(directory);
        if (File.Exists(StopPath)) File.Delete(StopPath);
        var executable = Environment.ProcessPath!;
        File.WriteAllLines(ScriptPath,
        [
            "@echo off",
            ":loop",
            $"if exist \"{StopPath}\" exit /b 0",
            $"tasklist /FI \"PID eq {Environment.ProcessId}\" 2>NUL | find \"{Environment.ProcessId}\" >NUL",
            "if errorlevel 1 goto restart",
            "ping 127.0.0.1 -n 2 >NUL",
            "goto loop",
            ":restart",
            $"echo restarted>\"{SentinelPath}\"",
            $"start \"\" \"{executable}\"",
            "exit /b 0"
        ]);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add(ScriptPath);
        Process.Start(start);
        IsActive = true;
    }

    public void Stop()
    {
        if (!IsActive && File.Exists(StopPath)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllText(StopPath, "stop");
        IsActive = false;
    }
}
