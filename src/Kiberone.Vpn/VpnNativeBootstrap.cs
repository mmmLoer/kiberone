using System.Runtime.InteropServices;
using Kiberone.Vpn.WireGuard;

namespace Kiberone.Vpn;

public static class VpnNativeBootstrap
{
    private static bool initialized;

    public static void Initialize()
    {
        if (initialized)
            return;

        initialized = true;
        var nativeDir = ResolveNativeDirectory();
        if (nativeDir is null)
        {
            VpnLog.Warn("native", $"Native directory not found. Base={AppContext.BaseDirectory} ProcessPath={Environment.ProcessPath}");
            return;
        }

        VpnLog.Info("native", $"Using native directory: {nativeDir}");

        NativeLibrary.SetDllImportResolver(typeof(VpnNativeBootstrap).Assembly, (name, _, _) =>
        {
            var candidate = Path.Combine(nativeDir, name);
            if (File.Exists(candidate))
                return NativeLibrary.Load(candidate);

            return IntPtr.Zero;
        });

        Preload(Path.Combine(nativeDir, "wireguard.dll"));
        Preload(Path.Combine(nativeDir, "tunnel.dll"));
        WireGuardDriver.EnsureReady();
    }

    private static string? ResolveNativeDirectory()
    {
        foreach (var directory in GetCandidateDirectories())
        {
            if (File.Exists(Path.Combine(directory, "tunnel.dll"))
                && File.Exists(Path.Combine(directory, "wireguard.dll")))
            {
                return directory;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateDirectories()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                yield return Path.Combine(exeDir, "native");
                yield return exeDir;
            }
        }

        yield return Path.Combine(AppContext.BaseDirectory, "native");
        yield return AppContext.BaseDirectory;
    }

    private static void Preload(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            NativeLibrary.Load(path);
            VpnLog.Info("native", $"Preloaded {Path.GetFileName(path)}");
        }
        catch (Exception error)
        {
            VpnLog.Error("native", $"Failed to preload {path}", error);
        }
    }
}
