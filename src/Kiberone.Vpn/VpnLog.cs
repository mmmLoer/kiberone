namespace Kiberone.Vpn;

public static class VpnLog
{
    private static readonly object Gate = new();
    private static readonly string[] LogPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KIBERone", "Student", "vpn", "vpn.log"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone", "Student", "vpn", "vpn.log")
    ];

    public static string PrimaryLogPath => LogPaths[0];

    public static void Info(string source, string message) => Write("INFO", source, message);

    public static void Warn(string source, string message) => Write("WARN", source, message);

    public static void Error(string source, string message, Exception? exception = null)
    {
        var details = exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}";
        Write("ERROR", source, details);
        if (exception?.InnerException is not null)
            Write("ERROR", source, $"  inner: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
    }

    private static void Write(string level, string source, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{source}] {message}";
        lock (Gate)
        {
            foreach (var path in LogPaths)
            {
                try
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory))
                        Directory.CreateDirectory(directory);
                    File.AppendAllText(path, line + Environment.NewLine);
                    break;
                }
                catch
                {
                    // try next path
                }
            }
        }
    }
}
