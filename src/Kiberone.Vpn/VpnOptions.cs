namespace Kiberone.Vpn;

public sealed class VpnOptions
{
    public static string ManagedConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KIBERone", "Student", "vpn", "peer.conf");

    public string ConfigPath { get; set; } = ManagedConfigPath;

    public bool RequireBridge { get; set; } = true;

    public string DevConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KIBERone", "Student", "vpn", "peer.conf");

    public string InstallTargetPath => ManagedConfigPath;

    public string ResolvedConfigPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ConfigPath) && File.Exists(ConfigPath))
                return Path.GetFullPath(ConfigPath);

            if (File.Exists(ManagedConfigPath))
                return Path.GetFullPath(ManagedConfigPath);

            if (File.Exists(DevConfigPath))
                return Path.GetFullPath(DevConfigPath);

            return Path.GetFullPath(string.IsNullOrWhiteSpace(ConfigPath) ? ManagedConfigPath : ConfigPath);
        }
    }
}
