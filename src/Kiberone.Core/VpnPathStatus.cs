using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kiberone.Core;

public sealed record VpnPathStatusReport(
    bool Ok,
    string PathId,
    string PublicHost,
    int WgPort,
    int PeersActive,
    int PeersTotal,
    int PeersCap,
    double Cpu,
    double RxMbps,
    double TxMbps,
    double? RttExitMs,
    bool UplinkOk,
    int ClientRttMs,
    string? Error = null);

public sealed record VpnIssuedConfig(
    string Location,
    string LocationId,
    string PathId,
    string Slot,
    string Address,
    string Endpoint,
    string Config);

public sealed class VpnPathStatusClient : IDisposable
{
    public const int TimeoutMs = 2000;
    private const string SharedToken = "2dd164b02df8cadd7f959b6184e0a7f2";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient http;

    public VpnPathStatusClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        http = handler is null
            ? new HttpClient { Timeout = timeout ?? TimeSpan.FromMilliseconds(TimeoutMs) }
            : new HttpClient(handler, disposeHandler: false) { Timeout = timeout ?? TimeSpan.FromMilliseconds(TimeoutMs) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<bool> HealthAsync(VpnRegionInfo region, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(region.StatusBaseUrl))
            return false;

        try
        {
            using var response = await http.GetAsync($"{region.StatusBaseUrl}/health", ct);
            if (!response.IsSuccessStatusCode)
                return false;
            var body = await response.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<HealthDto>(body, Json);
            return parsed?.Ok == true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<VpnPathStatusReport> StatusAsync(VpnRegionInfo region, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(region.StatusBaseUrl))
            return Dead(region, "Нет адреса status API.");

        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{region.StatusBaseUrl}/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SharedToken);
            using var response = await http.SendAsync(request, ct);
            var elapsed = (int)clock.ElapsedMilliseconds;
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return Dead(region, "Status API: неверный токен.", elapsed);
            if (!response.IsSuccessStatusCode)
                return Dead(region, $"Status API: HTTP {(int)response.StatusCode}.", elapsed);

            var body = await response.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<StatusDto>(body, Json);
            if (parsed is null)
                return Dead(region, "Status API: пустой ответ.", elapsed);

            return new VpnPathStatusReport(
                parsed.Ok,
                parsed.PathId ?? region.Id,
                parsed.PublicHost ?? region.PublicHost,
                parsed.WgPort == 0 ? region.WgPort : parsed.WgPort,
                parsed.PeersActive,
                parsed.PeersTotal,
                parsed.PeersCap == 0 ? 80 : parsed.PeersCap,
                parsed.Cpu,
                parsed.RxMbps,
                parsed.TxMbps,
                parsed.RttExitMs,
                parsed.UplinkOk,
                elapsed);
        }
        catch (Exception error)
        {
            return Dead(region, error.Message, (int)clock.ElapsedMilliseconds);
        }
    }

    public async Task<VpnIssuedConfig> IssueConfigAsync(
        VpnRegionInfo region,
        string location,
        string? slot = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(region.StatusBaseUrl))
            throw new InvalidOperationException("Нет адреса VPN API.");
        if (string.IsNullOrWhiteSpace(location))
            throw new InvalidOperationException("Не указана локация класса.");

        var query = $"location={Uri.EscapeDataString(location.Trim())}";
        if (!string.IsNullOrWhiteSpace(slot))
            query += $"&slot={Uri.EscapeDataString(slot.Trim())}";

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{region.StatusBaseUrl}/config?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SharedToken);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("VPN API: неверный токен.");
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new VpnLocationFullException(region.Id, ExtractError(body) ?? "location_full");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractError(body) ?? $"VPN API: HTTP {(int)response.StatusCode}.");

        var parsed = JsonSerializer.Deserialize<ConfigDto>(body, Json)
                     ?? throw new InvalidOperationException("VPN API: пустой ответ /config.");
        if (!parsed.Ok || string.IsNullOrWhiteSpace(parsed.Config))
            throw new InvalidOperationException(ExtractError(body) ?? "VPN API не выдал конфиг.");

        return new VpnIssuedConfig(
            parsed.Location ?? location.Trim(),
            parsed.LocationId ?? string.Empty,
            parsed.PathId ?? region.Id,
            parsed.Slot ?? string.Empty,
            parsed.Address ?? string.Empty,
            parsed.Endpoint ?? $"{region.PublicHost}:{region.WgPort}",
            parsed.Config);
    }

    public void Dispose() => http.Dispose();

    private static string? ExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
                return error.GetString();
            if (document.RootElement.TryGetProperty("message", out var message))
                return message.GetString();
        }
        catch
        {
            // plain text body
        }

        return string.IsNullOrWhiteSpace(body) ? null : body.Trim();
    }

    private static VpnPathStatusReport Dead(VpnRegionInfo region, string error, int clientRttMs = TimeoutMs) =>
        new(false, region.Id, region.PublicHost, region.WgPort, 0, 0, 80, 0, 0, 0, null, false, clientRttMs, error);

    private sealed record HealthDto(bool Ok);

    private sealed record StatusDto(
        bool Ok,
        [property: JsonPropertyName("path_id")] string? PathId,
        [property: JsonPropertyName("public_host")] string? PublicHost,
        [property: JsonPropertyName("wg_port")] int WgPort,
        [property: JsonPropertyName("peers_active")] int PeersActive,
        [property: JsonPropertyName("peers_total")] int PeersTotal,
        [property: JsonPropertyName("peers_cap")] int PeersCap,
        double Cpu,
        [property: JsonPropertyName("rx_mbps")] double RxMbps,
        [property: JsonPropertyName("tx_mbps")] double TxMbps,
        [property: JsonPropertyName("rtt_exit_ms")] double? RttExitMs,
        [property: JsonPropertyName("uplink_ok")] bool UplinkOk);

    private sealed record ConfigDto(
        bool Ok,
        string? Location,
        [property: JsonPropertyName("location_id")] string? LocationId,
        [property: JsonPropertyName("path_id")] string? PathId,
        string? Slot,
        string? Address,
        string? Endpoint,
        string? Config);
}

public sealed class VpnLocationFullException(string pathId, string message) : Exception(message)
{
    public string PathId { get; } = pathId;
}

public static class VpnPathSelector
{
    public static bool IsLive(VpnPathStatusReport report) =>
        report.Ok
        && report.UplinkOk
        && string.IsNullOrWhiteSpace(report.Error)
        && report.PeersActive < Math.Max(1, report.PeersCap);

    public static double Score(VpnPathStatusReport report)
    {
        var cap = Math.Max(1, report.PeersCap);
        return report.ClientRttMs
               + 2 * (report.RttExitMs ?? 999)
               + 25 * (report.PeersActive / (double)cap)
               + 20 * report.Cpu
               + 8 * (report.TxMbps / 200d);
    }

    public static async Task<IReadOnlyList<(VpnRegionInfo Region, bool Health, VpnPathStatusReport Status)>> ProbeAllAsync(
        VpnPathStatusClient client,
        CancellationToken ct = default)
    {
        var tasks = VpnRegionCatalog.All.Select(async region =>
        {
            var health = await client.HealthAsync(region, ct);
            var status = await client.StatusAsync(region, ct);
            return (region, health, status);
        });
        return await Task.WhenAll(tasks);
    }

    public static async Task<VpnRegionInfo?> PickBestAsync(VpnPathStatusClient client, CancellationToken ct = default)
    {
        var rows = await ProbeAllAsync(client, ct);
        return rows
            .Where(row => row.Health && IsLive(row.Status))
            .OrderBy(row => Score(row.Status))
            .Select(row => row.Region)
            .FirstOrDefault();
    }

    public static async Task<(VpnIssuedConfig Config, VpnRegionInfo Region)> LeaseConfigAsync(
        VpnPathStatusClient client,
        string classroomLocation,
        VpnRegionInfo preferred,
        CancellationToken ct = default)
    {
        var order = new List<VpnRegionInfo> { preferred, VpnRegionCatalog.Other(preferred.Id) };
        Exception? last = null;
        foreach (var region in order)
        {
            try
            {
                var issued = await client.IssueConfigAsync(region, classroomLocation, ct: ct);
                return (issued, region);
            }
            catch (VpnLocationFullException error)
            {
                last = error;
            }
            catch (Exception error)
            {
                last = error;
            }
        }

        throw last ?? new InvalidOperationException("Не удалось получить VPN-конфиг.");
    }
}
