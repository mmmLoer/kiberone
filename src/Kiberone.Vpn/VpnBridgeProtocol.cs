using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiberone.Vpn;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VpnBridgeAction
{
    Ping,
    Status,
    InstallConfig,
    Connect,
    Disconnect,
    ApplyUpdate
}

public sealed record VpnBridgeRequest(
    VpnBridgeAction Action,
    string? ConfigPath = null,
    string? ConfigBase64 = null,
    string? SourcePath = null,
    string? TargetPath = null);

public sealed record VpnBridgeResponse(
    bool Ok,
    bool Connected = false,
    string State = "unknown",
    string? ConfigPath = null,
    bool ConfigExists = false,
    string? Error = null);

public static class VpnBridgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
