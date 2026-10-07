using System.Net.Http.Json;
using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class ClassroomHubClient
{
    public const string DefaultBaseUrl = "http://193.182.145.64:8787";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient http;

    public ClassroomHubClient(string? baseUrl = null)
    {
        http = new HttpClient
        {
            BaseAddress = new Uri((string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim()).TrimEnd('/') + "/"),
            // Student update packages are ~200MB+; keep this high for hub downloads.
            Timeout = TimeSpan.FromMinutes(15)
        };
    }

    public async Task<IReadOnlyList<HubLocationStatus>> ListLocationsAsync(CancellationToken ct = default)
    {
        var rows = await http.GetFromJsonAsync<List<HubLocationStatus>>("api/locations", ct);
        return rows ?? [];
    }

    public async Task<LocationRosterSnapshot?> DownloadAsync(string location, string password, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/locations/{Uri.EscapeDataString(location.Trim())}/roster");
        request.Headers.TryAddWithoutValidation("X-Location-Password", password);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden
            || response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("Неверный пароль локации.");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LocationRosterSnapshot>(Json, ct);
    }

    public async Task UploadAsync(string location, string password, LocationRosterSnapshot snapshot, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync(
            $"api/locations/{Uri.EscapeDataString(location.Trim())}/roster",
            new LocationRosterUploadRequest(password, snapshot),
            ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Неверный пароль локации.");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? $"Сервер ответил {(int)response.StatusCode}." : body);
        }
    }

    public async Task<IReadOnlyList<VpnRegionInfo>> ListVpnRegionsAsync(CancellationToken ct = default)
    {
        try
        {
            var rows = await http.GetFromJsonAsync<List<VpnRegionInfo>>("api/vpn/regions", ct);
            return rows is { Count: > 0 } ? rows : VpnRegionCatalog.All;
        }
        catch
        {
            return VpnRegionCatalog.All;
        }
    }

    public async Task<VpnPeerPack> DownloadVpnPeersAsync(string regionId, string location, string password, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/vpn/regions/{Uri.EscapeDataString(regionId.Trim())}/peers",
            new VpnPeerDownloadRequest(location, password),
            ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Неверный пароль локации.");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? $"Сервер ответил {(int)response.StatusCode}." : body);
        }

        return await response.Content.ReadFromJsonAsync<VpnPeerPack>(ct)
               ?? throw new InvalidOperationException("Сервер не вернул VPN-конфиги.");
    }

    public async Task<VpnPeerReservation?> GetVpnReservationAsync(string location, string password,
        string clientId, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/vpn/reservations/lookup",
            new VpnPeerReservationLookup(location, password, clientId), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Неверный пароль локации.");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<VpnPeerReservation>(body, Json);
    }

    public async Task<VpnPeerReservation> ReserveVpnPeerAsync(VpnPeerReservationRequest request,
        CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/vpn/reservations", request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Неверный пароль локации.");
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new VpnPeerConflictException("VPN-профиль уже закреплён за другим ПК.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VpnPeerReservation>(Json, ct)
            ?? throw new InvalidOperationException("Сервер не подтвердил VPN-резервацию.");
    }

    public Task<AppUpdateManifest?> GetStudentUpdateAsync(bool testChannel = false, CancellationToken ct = default) =>
        GetOptionalAsync<AppUpdateManifest>(testChannel ? "api/update/student?channel=test" : "api/update/student", ct);

    public async Task<byte[]> DownloadStudentUpdateFileAsync(bool testChannel = false, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(testChannel ? "api/update/student/file?channel=test" : "api/update/student/file",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return memory.ToArray();
    }

    private async Task<T?> GetOptionalAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }
}
