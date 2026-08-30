namespace Kiberone.VpnAgent;

public sealed class VpnAgentOptions
{
    public const string SectionName = "VpnAgent";

    /// <summary>HTTP listen port for router control API.</summary>
    public int Port { get; set; } = 9777;

    /// <summary>Shared secret for X-Vpn-Token. Required for all routes except /health.</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Absolute or relative path to the WireGuard .conf for this PC.</summary>
    public string ConfigPath { get; set; } = @"C:\ProgramData\KIBERone\VpnAgent\peer.conf";

    /// <summary>Optional comma-separated IPv4 allowlist for API clients (router). Empty = any.</summary>
    public string AllowedRemoteAddresses { get; set; } = string.Empty;

    public IReadOnlyList<string> ParseAllowlist()
    {
        if (string.IsNullOrWhiteSpace(AllowedRemoteAddresses))
            return Array.Empty<string>();
        return AllowedRemoteAddresses
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    public void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new InvalidOperationException("VpnAgent:Port must be 1–65535.");
        if (string.IsNullOrWhiteSpace(ApiToken) || ApiToken.Length < 16)
            throw new InvalidOperationException("VpnAgent:ApiToken must be at least 16 characters.");
        if (string.IsNullOrWhiteSpace(ConfigPath))
            throw new InvalidOperationException("VpnAgent:ConfigPath is required.");
    }
}
