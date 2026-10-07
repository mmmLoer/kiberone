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

    public LocationRosterSnapshot GetAuthorized(string location, string password)
    {
        EnsureAuthorized(location, password);
        return Get(location);
    }

    public LocationStudentSnapshot EnrollMailStudent(string location, string password, LocationStudentSnapshot student, LocationGroupSnapshot group)
    {
        EnsureAuthorized(location, password);
        if (student.Id == Guid.Empty || group.Id == Guid.Empty || student.GroupId != group.Id
            || !string.Equals(group.Location.Trim(), location.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(student.FirstName) || string.IsNullOrWhiteSpace(student.LastName)
            || student.FirstName.Length > 120 || student.LastName.Length > 120)
            throw new ArgumentException("Некорректные данные ученика.");
        lock (gate)
        {
            foreach (var other in secrets.Keys.Where(x => !string.Equals(x, location.Trim(), StringComparison.OrdinalIgnoreCase)))
                if (ReadUnlocked(other)?.Students.Any(x => x.Id == student.Id) == true
                    || ReadUnlocked(other)?.Groups.Any(x => x.Id == group.Id) == true)
                    throw new UnauthorizedAccessException("Ученик относится к другой локации.");
            var roster = ReadUnlocked(location) ?? Empty(location);
            var existing = roster.Students.FirstOrDefault(x => x.Id == student.Id);
            if (existing is not null) return existing;
            var groups = roster.Groups.Any(x => x.Id == group.Id) ? roster.Groups : roster.Groups.Append(group).ToArray();
            var updated = roster with { Groups = groups, Students = roster.Students.Append(student).ToArray(), ExportedAt = DateTimeOffset.UtcNow };
            var path = RosterPath(location);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(updated, Json));
                if (OperatingSystem.IsLinux())
                {
                    // Atomic replacement must retain the Hub user's access to its roster.
                    var reference = File.Exists(path) ? path : rosterDirectory;
                    var info = new System.Diagnostics.ProcessStartInfo("/usr/bin/chown") { UseShellExecute = false, CreateNoWindow = true };
                    info.ArgumentList.Add("--reference=" + reference);
                    info.ArgumentList.Add("--");
                    info.ArgumentList.Add(temporary);
                    using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("Не удалось сохранить владельца списка учеников.");
                    if (!process.WaitForExit(5000)) { process.Kill(); throw new IOException("Не удалось сохранить владельца списка учеников."); }
                    if (process.ExitCode != 0) throw new IOException("Не удалось сохранить владельца списка учеников.");
                    File.SetUnixFileMode(temporary, File.Exists(path) ? File.GetUnixFileMode(path) : UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return student;
        }
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

    public VpnPeerReservation? GetVpnReservation(string location, string password, string clientId)
    {
        EnsureAuthorized(location, password);
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Нужен client_id.");
        lock (gate)
            return ReadVpnReservations().FirstOrDefault(x =>
                x.Location.Equals(location.Trim(), StringComparison.OrdinalIgnoreCase)
                && x.ClientId.Equals(clientId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public VpnPeerReservation ReserveVpnPeer(VpnPeerReservationRequest request)
    {
        EnsureAuthorized(request.Location, request.Password);
        var region = VpnRegionCatalog.All.FirstOrDefault(x => x.Id.Equals(request.RegionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Неизвестный VPN-регион.");
        if (string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 128
            || request.ClientId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.Slot) || request.Slot.Length > 128
            || request.Slot.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.Fingerprint)
            || request.Fingerprint.Length != 64 || !request.Fingerprint.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Некорректные данные VPN-резервации.");

        var reservation = new VpnPeerReservation(request.Location.Trim(), request.ClientId.Trim(),
            region.Id, request.Slot.Trim(), request.Fingerprint.ToUpperInvariant());
        lock (gate)
        {
            var rows = ReadVpnReservations();
            var duplicate = rows.FirstOrDefault(x =>
                x.Fingerprint.Equals(reservation.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null &&
                (!duplicate.Location.Equals(reservation.Location, StringComparison.OrdinalIgnoreCase)
                 || !duplicate.ClientId.Equals(reservation.ClientId, StringComparison.OrdinalIgnoreCase)))
                throw new VpnPeerConflictException("Этот VPN-профиль уже закреплён за другим ПК.");

            var slotOwner = rows.FirstOrDefault(x =>
                x.RegionId.Equals(reservation.RegionId, StringComparison.OrdinalIgnoreCase)
                && x.Slot.Equals(reservation.Slot, StringComparison.OrdinalIgnoreCase));
            if (slotOwner is not null &&
                (!slotOwner.Location.Equals(reservation.Location, StringComparison.OrdinalIgnoreCase)
                 || !slotOwner.ClientId.Equals(reservation.ClientId, StringComparison.OrdinalIgnoreCase)))
                throw new VpnPeerConflictException("Этот VPN-слот уже закреплён за другим ПК.");

            var existing = rows.FirstOrDefault(x =>
                x.Location.Equals(reservation.Location, StringComparison.OrdinalIgnoreCase)
                && x.ClientId.Equals(reservation.ClientId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && existing != reservation)
                throw new VpnPeerConflictException("За этим ПК уже закреплён другой VPN-профиль.");
            if (existing is null)
            {
                rows.Add(reservation);
                var path = Path.Combine(vpnDirectory, "reservations.json");
                var staging = path + ".tmp";
                File.WriteAllText(staging, JsonSerializer.Serialize(rows, Json));
                File.Move(staging, path, true);
            }
            return reservation;
        }
    }

    private List<VpnPeerReservation> ReadVpnReservations()
    {
        var path = Path.Combine(vpnDirectory, "reservations.json");
        return File.Exists(path)
            ? JsonSerializer.Deserialize<List<VpnPeerReservation>>(File.ReadAllText(path), Json) ?? []
            : [];
    }

    public AppUpdateManifest? GetStudentUpdate(bool testChannel = false)
    {
        var directory = testChannel ? Path.Combine(updatesDirectory, "test") : updatesDirectory;
        var manifestPath = Path.Combine(directory, "student_manifest.json");
        if (!File.Exists(manifestPath))
            return null;
        var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(File.ReadAllText(manifestPath), UpdateJson)
            ?? JsonSerializer.Deserialize<AppUpdateManifest>(File.ReadAllText(manifestPath), Json);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Filename) ||
            !StudentUpdateSignature.Verify(manifest.Version, manifest.Size, manifest.Sha256, manifest.Signature))
            return null;
        var file = Path.Combine(directory, Path.GetFileName(manifest.Filename));
        if (!File.Exists(file))
            return null;
        var info = new FileInfo(file);
        if (info.Length != manifest.Size)
            return null;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
        return hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase) ? manifest : null;
    }

    public Stream? OpenStudentUpdate(bool testChannel = false)
    {
        var manifest = GetStudentUpdate(testChannel);
        if (manifest is null)
            return null;
        var directory = testChannel ? Path.Combine(updatesDirectory, "test") : updatesDirectory;
        var file = Path.Combine(directory, Path.GetFileName(manifest.Filename));
        return File.Exists(file)
            ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)
            : null;
    }

    /// <summary>
    /// CI drops KIBERoneStudent.exe + student_manifest.json into updates/. This republishes
    /// from a built exe and writes a snake_case manifest Tutors already expect.
    /// </summary>
    public AppUpdateManifest PublishStudentUpdate(string sourceExePath, string version, string signature)
    {
        if (!File.Exists(sourceExePath))
            throw new FileNotFoundException("Не найден собранный Student.exe.", sourceExePath);
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Нужна версия обновления.", nameof(version));

        var bytes = File.ReadAllBytes(sourceExePath);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (!StudentUpdateSignature.Verify(version, bytes.LongLength, hash, signature))
            throw new InvalidOperationException("Недействительная подпись обновления Student.");
        var filename = "KIBERoneStudent.exe";
        Directory.CreateDirectory(updatesDirectory);
        File.WriteAllBytes(Path.Combine(updatesDirectory, filename), bytes);
        var manifest = new AppUpdateManifest(version.Trim(), filename, bytes.Length, hash, DateTimeOffset.UtcNow, signature);
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
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(password))
            throw new UnauthorizedAccessException("Неверный пароль локации.");
        if (!secrets.TryGetValue(location.Trim(), out var secret) || !LocationPassword.Verify(password, secret.Salt, secret.Hash))
            throw new UnauthorizedAccessException("Неверный пароль локации.");
    }

    private string RegionDirectory(string regionId) =>
        Path.Combine(vpnDirectory, VpnRegionCatalog.Resolve(regionId).Id);
}
