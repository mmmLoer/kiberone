namespace Kiberone.Vpn;

public sealed record VpnStatus(
    bool Connected,
    string State,
    string ServiceName,
    string ConfigPath,
    bool ConfigExists,
    string? LastError = null);
