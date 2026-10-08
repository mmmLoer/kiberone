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

    // The roster endpoint uses the ASP.NET web (camelCase) contract.
    // Update manifests use snake_case and keep their separate options above.
    private static readonly JsonSerializerOptions RosterJson = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;

    public ClassroomHubClient(string? baseUrl = null)
    {
        http = new HttpClient
        {
            BaseAddress = ResolveBaseAddress(baseUrl),
            // Student update packages are ~200MB+; keep this high for hub downloads.
            Timeout = TimeSpan.FromMinutes(15)
        };
    }

    public static Uri ResolveBaseAddress(string? baseUrl)
    {
        var value = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim()).TrimEnd('/') + "/";
        var uri = new Uri(value);
        // Upgrade the known production endpoint while preserving the existing credential scope.
        return uri.Scheme == "http" && uri.Host == "193.182.145.64" && uri.Port == 8787
            ? new Uri("https://nshub.pro/") : uri;
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
        return await response.Content.ReadFromJsonAsync<LocationRosterSnapshot>(RosterJson, ct);
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
        testChannel ? GetOptionalAsync<AppUpdateManifest>("api/update/student?channel=test", ct)
            : GetAppUpdateAsync("student", "release", ct);

    public async Task<AppUpdateManifest?> GetAppUpdateAsync(string app, string channel, CancellationToken ct = default)
    {
        ClassroomHubStore.ValidateAppChannel(app, channel);
        var manifest = await GetOptionalAsync<AppUpdateManifest>($"api/update/{app}?channel={channel}", ct);
        if (manifest is null) return null;
        ValidateAppManifest(app, channel, manifest);
        return manifest;
    }

    public async Task<byte[]> DownloadAppUpdateFileAsync(string app, string channel, CancellationToken ct = default)
    {
        ClassroomHubStore.ValidateAppChannel(app, channel);
        var manifest = await GetAppUpdateAsync(app, channel, ct)
            ?? throw new HttpRequestException("Обновление не найдено.", null, System.Net.HttpStatusCode.NotFound);
        return await DownloadAppUpdateFileAsync(app, channel, manifest, ct);
    }

    public async Task<byte[]> DownloadAppUpdateFileAsync(string app, string channel, AppUpdateManifest expected, CancellationToken ct = default)
    {
        ClassroomHubStore.ValidateAppChannel(app, channel);
        ArgumentNullException.ThrowIfNull(expected);
        ValidateAppManifest(app, channel, expected);
        var bytes = await DownloadUpdateFileAsync($"api/update/{app}/file?channel={channel}&version={Uri.EscapeDataString(expected.Version)}&sha256={Uri.EscapeDataString(expected.Sha256)}", ct);
        if (bytes.LongLength != expected.Size
            || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл обновления не соответствует подписанному манифесту.");
        return bytes;
    }

    private static void ValidateAppManifest(string app, string channel, AppUpdateManifest manifest)
    {
        if (!AppReleaseVersion.IsValid(manifest.Version) || AppReleaseVersion.ChannelFor(manifest.Version) != channel
            || !StudentUpdateSignature.VerifyApp(app, manifest.Version, manifest.Size, manifest.Sha256, manifest.Signature))
            throw new InvalidDataException("Сервер вернул недействительное обновление приложения или канала.");
    }

    public Task<byte[]> DownloadStudentUpdateFileAsync(bool testChannel = false, CancellationToken ct = default) =>
        testChannel ? DownloadUpdateFileAsync("api/update/student/file?channel=test", ct)
            : DownloadAppUpdateFileAsync("student", "release", ct);

    private async Task<byte[]> DownloadUpdateFileAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url,
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
