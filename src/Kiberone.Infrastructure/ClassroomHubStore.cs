using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class ClassroomHubStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions UpdateJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string rosterDirectory;
    private readonly string vpnDirectory;
    private readonly string updatesDirectory;
    private readonly string installersDirectory;
    private readonly IReadOnlyDictionary<string, LocationSecretRecord> secrets;
    private readonly object gate = new();

    public ClassroomHubStore(string dataDirectory, IEnumerable<LocationSecretRecord> secrets)
    {
        rosterDirectory = Path.Combine(dataDirectory, "rosters");
        vpnDirectory = Path.Combine(dataDirectory, "vpn");
        updatesDirectory = Path.Combine(dataDirectory, "updates");
        installersDirectory = Path.Combine(dataDirectory, "installers");
        Directory.CreateDirectory(rosterDirectory);
        Directory.CreateDirectory(vpnDirectory);
        Directory.CreateDirectory(updatesDirectory);
        Directory.CreateDirectory(installersDirectory);
        foreach (var region in VpnRegionCatalog.All)
            Directory.CreateDirectory(RegionDirectory(region.Id));
        this.secrets = secrets
            .Where(x => !string.IsNullOrWhiteSpace(x.Location))
            .ToDictionary(x => x.Location.Trim(), x => x, StringComparer.OrdinalIgnoreCase);
    }

    public string UpdatesDirectory => updatesDirectory;
    public string InstallersDirectory => installersDirectory;

    public IReadOnlyList<HubLocationStatus> List()
    {
        var names = ProgramCatalog.LocationNames().ToList();
        foreach (var extra in secrets.Keys)
        {
            if (!names.Contains(extra, StringComparer.OrdinalIgnoreCase))
                names.Add(extra);
        }

        lock (gate)
        {
            return names.Select(name =>
            {
                var snapshot = ReadUnlocked(name);
                return snapshot is null
                    ? new HubLocationStatus(name, null, 0, 0)
                    : new HubLocationStatus(name, snapshot.ExportedAt, snapshot.Groups.Count, snapshot.Students.Count);
            }).ToList();
        }
    }

    public LocationRosterSnapshot Get(string location)
    {
        lock (gate)
            return ReadUnlocked(location) ?? Empty(location);
    }

    public LocationRosterSnapshot Put(string location, string password, LocationRosterSnapshot snapshot)
    {
        if (!secrets.TryGetValue(location.Trim(), out var secret) || !LocationPassword.Verify(password, secret.Salt, secret.Hash))
            throw new UnauthorizedAccessException("Неверный пароль локации.");
        if (!string.Equals(snapshot.Location.Trim(), location.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Снимок относится к другой локации.");

        var stored = snapshot with
        {
            Location = location.Trim(),
            ExportedAt = DateTimeOffset.UtcNow
        };
        lock (gate)
        {
            File.WriteAllText(RosterPath(location), JsonSerializer.Serialize(stored, Json));
        }
        return stored;
    }

    private LocationRosterSnapshot? ReadUnlocked(string location)
    {
        var path = RosterPath(location);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<LocationRosterSnapshot>(File.ReadAllText(path), Json);
    }

    private string RosterPath(string location)
    {
        var safe = string.Join("_", location.Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safe)) safe = "location";
        return Path.Combine(rosterDirectory, safe + ".json");
    }

    public static LocationRosterSnapshot Empty(string location) =>
        new(location.Trim(), DateTimeOffset.UtcNow, [], []);

    public IReadOnlyList<VpnRegionInfo> ListVpnRegions()
    {
        lock (gate)
        {
            return VpnRegionCatalog.All.Select(region =>
            {
                var count = Directory.Exists(RegionDirectory(region.Id))
                    ? Directory.GetFiles(RegionDirectory(region.Id), "*.conf", SearchOption.TopDirectoryOnly).Length
                    : 0;
                return region with { PeerCount = count };
            }).ToList();
        }
    }

    public VpnPeerPack GetVpnPeers(string regionId, string location, string password)
    {
        EnsureAuthorized(location, password);
        var region = VpnRegionCatalog.Resolve(regionId);
        lock (gate)
        {
            var directory = RegionDirectory(region.Id);
            var files = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.conf", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => new VpnPeerFile(Path.GetFileName(path), File.ReadAllText(path)))
                    .ToList()
                : [];
            return new VpnPeerPack(region.Id, region.Name, region.CheckHost, files);
        }
    }

    public VpnPeerPack PutVpnPeers(string regionId, string location, string password, IReadOnlyList<VpnPeerFile> files)
    {
        EnsureAuthorized(location, password);
        var region = VpnRegionCatalog.Resolve(regionId);
        lock (gate)
        {
            var directory = RegionDirectory(region.Id);
            Directory.CreateDirectory(directory);
            foreach (var leftover in Directory.GetFiles(directory, "*.conf", SearchOption.TopDirectoryOnly))
                File.Delete(leftover);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file.FileName);
                if (string.IsNullOrWhiteSpace(name)
                    || !name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
                    || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidOperationException($"Некорректное имя VPN-конфига: {file.FileName}");
                File.WriteAllText(Path.Combine(directory, name), file.Content ?? string.Empty);
            }
        }
        return GetVpnPeers(region.Id, location, password);
    }

    public AppUpdateManifest? GetStudentUpdate()
    {
        var manifestPath = Path.Combine(updatesDirectory, "student_manifest.json");
        if (!File.Exists(manifestPath))
            return null;
        var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(File.ReadAllText(manifestPath), UpdateJson)
            ?? JsonSerializer.Deserialize<AppUpdateManifest>(File.ReadAllText(manifestPath), Json);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Filename))
            return null;
        var file = Path.Combine(updatesDirectory, Path.GetFileName(manifest.Filename));
        return File.Exists(file) ? manifest : null;
    }

    public Stream? OpenStudentUpdate()
    {
        var manifest = GetStudentUpdate();
        if (manifest is null)
            return null;
        var file = Path.Combine(updatesDirectory, Path.GetFileName(manifest.Filename));
        return File.Exists(file)
            ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    /// <summary>
    /// CI drops KIBERoneStudent.exe + student_manifest.json into updates/. This republishes
    /// from a built exe and writes a snake_case manifest Tutors already expect.
    /// </summary>
    public AppUpdateManifest PublishStudentUpdate(string sourceExePath, string version)
    {
        if (!File.Exists(sourceExePath))
            throw new FileNotFoundException("Не найден собранный Student.exe.", sourceExePath);
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Нужна версия обновления.", nameof(version));

        var bytes = File.ReadAllBytes(sourceExePath);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var filename = "KIBERoneStudent.exe";
        Directory.CreateDirectory(updatesDirectory);
        File.WriteAllBytes(Path.Combine(updatesDirectory, filename), bytes);
        var manifest = new AppUpdateManifest(version.Trim(), filename, bytes.Length, hash, DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(updatesDirectory, "student_manifest.json"),
            JsonSerializer.Serialize(manifest, UpdateJson));
        return manifest;
    }

    public IReadOnlyList<string> ListInstallerZips()
    {
        if (!Directory.Exists(installersDirectory))
            return [];
        return Directory.GetFiles(installersDirectory, "KIBERone*-Setup-*-win-x64.zip")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureAuthorized(string location, string password)
    {
        if (!secrets.TryGetValue(location.Trim(), out var secret) || !LocationPassword.Verify(password, secret.Salt, secret.Hash))
            throw new UnauthorizedAccessException("Неверный пароль локации.");
    }

    private string RegionDirectory(string regionId) =>
        Path.Combine(vpnDirectory, VpnRegionCatalog.Resolve(regionId).Id);
}
