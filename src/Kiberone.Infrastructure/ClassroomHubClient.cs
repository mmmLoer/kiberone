using System.Net.Http.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class ClassroomHubClient
{
    public const string DefaultBaseUrl = "http://193.235.147.228:8787";

    private readonly HttpClient http;

    public ClassroomHubClient(string? baseUrl = null)
    {
        http = new HttpClient
        {
            BaseAddress = new Uri((string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim()).TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    public async Task<IReadOnlyList<HubLocationStatus>> ListLocationsAsync(CancellationToken ct = default)
    {
        var rows = await http.GetFromJsonAsync<List<HubLocationStatus>>("api/locations", ct);
        return rows ?? [];
    }

    public Task<LocationRosterSnapshot?> DownloadAsync(string location, CancellationToken ct = default) =>
        http.GetFromJsonAsync<LocationRosterSnapshot>($"api/locations/{Uri.EscapeDataString(location.Trim())}/roster", ct);

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

    public Task<AppUpdateManifest?> GetStudentUpdateAsync(CancellationToken ct = default) =>
        GetOptionalAsync<AppUpdateManifest>("api/update/student", ct);

    public async Task<byte[]> DownloadStudentUpdateFileAsync(CancellationToken ct = default) =>
        await http.GetByteArrayAsync("api/update/student/file", ct);

    private async Task<T?> GetOptionalAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }
}
