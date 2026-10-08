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
        lock (gate)
        {
            var roster = ReadUnlocked(location) ?? Empty(location);
            EnsureRosterOwnershipUnlocked(location, roster.Students.Select(x => x.Id),
                roster.Groups.Select(x => x.Id).Concat(roster.Students.Select(x => x.GroupId)));
            return roster;
        }
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
            EnsureRosterOwnershipUnlocked(location, [student.Id], [group.Id]);
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
        EnsureAuthorized(location, password);
        if (!string.Equals(snapshot.Location.Trim(), location.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Снимок относится к другой локации.");

        var stored = snapshot with
        {
            Location = location.Trim(),
            ExportedAt = DateTimeOffset.UtcNow
        };
        lock (gate)
        {
            EnsureRosterOwnershipUnlocked(location, stored.Students.Select(x => x.Id),
                stored.Groups.Select(x => x.Id).Concat(stored.Students.Select(x => x.GroupId)));
            File.WriteAllText(RosterPath(location), JsonSerializer.Serialize(stored, Json));
        }
        return stored;
    }

    // Called under gate so two locations cannot claim the same UUID concurrently.
    private void EnsureRosterOwnershipUnlocked(string location, IEnumerable<Guid> studentIds, IEnumerable<Guid> groupIds)
    {
        var students = studentIds.ToHashSet();
        var groups = groupIds.ToHashSet();
        foreach (var other in secrets.Keys.Where(x => !string.Equals(x, location.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var roster = ReadUnlocked(other);
            if (roster is not null && (roster.Students.Any(x => students.Contains(x.Id))
                || roster.Groups.Any(x => groups.Contains(x.Id))
                || roster.Students.Any(x => groups.Contains(x.GroupId))))
                throw new UnauthorizedAccessException("Ученик или группа относится к другой локации.");
        }
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

    public AppUpdateManifest? GetAppUpdate(string app, string channel)
    {
        ValidateAppChannel(app, channel);
        var release = OpenVerifiedUpdate(app, AppUpdateDirectory(app, channel), channel);
        using var content = release?.Content;
        return release?.Manifest;
    }

    public Stream? OpenAppUpdate(string app, string channel, string? version = null, string? sha256 = null)
    {
        ValidateAppChannel(app, channel);
        ValidateUpdatePin(version, sha256);
        var current = OpenVerifiedUpdate(app, AppUpdateDirectory(app, channel), channel, version, sha256);
        return current?.Content ?? (version is null ? null
            : OpenVerifiedUpdate(app, Path.Combine(updatesDirectory, channel), channel, version, sha256, archived: true)?.Content);
    }

    // Keep old test-channel URLs separate from beta: deployed test releases did
    // not use the beta version suffix and must not become beta candidates.
    public AppUpdateManifest? GetStudentUpdate(bool testChannel = false)
    {
        if (!testChannel) return GetAppUpdate("student", "release");
        var release = OpenVerifiedUpdate("student", Path.Combine(updatesDirectory, "test"), channel: null);
        using var content = release?.Content;
        return release?.Manifest;
    }

    public Stream? OpenStudentUpdate(bool testChannel = false, string? version = null, string? sha256 = null)
    {
        ValidateUpdatePin(version, sha256);
        return testChannel
            ? OpenVerifiedUpdate("student", Path.Combine(updatesDirectory, "test"), channel: null, version, sha256)?.Content
            : OpenAppUpdate("student", "release", version, sha256);
    }

    private static void ValidateUpdatePin(string? version, string? sha256)
    {
        if (version is null && sha256 is null) return;
        if (!AppReleaseVersion.IsValid(version) || sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Для выбора обновления нужны версия и SHA-256.");
    }

    internal static void ValidateAppChannel(string app, string channel)
    {
        if (app is not "student" and not "tutor") throw new ArgumentException("Неизвестное приложение обновления.", nameof(app));
        if (channel is not "release" and not "beta") throw new ArgumentException("Неизвестный канал обновления.", nameof(channel));
    }

    private string AppUpdateDirectory(string app, string channel)
    {
        var directory = Path.Combine(updatesDirectory, channel);
        // Only an absent release manifest permits migration fallback. A broken
        // canonical release must not silently select an older legacy artifact.
        return app == "student" && channel == "release"
            && !File.Exists(Path.Combine(directory, "student_manifest.json"))
            ? updatesDirectory : directory;
    }

    private (AppUpdateManifest Manifest, FileStream Content)? OpenVerifiedUpdate(string app, string directory, string? channel,
        string? version = null, string? sha256 = null, bool archived = false)
    {
        FileStream? content = null;
        try
        {
            var manifestPath = archived ? Path.Combine(directory, ".versions", app, version + ".json")
                : Path.Combine(directory, app + "_manifest.json");
            if (!File.Exists(manifestPath)) return null;
            var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(File.ReadAllText(manifestPath), UpdateJson);
            if (manifest is null || !AppReleaseVersion.IsValid(manifest.Version)
                || (channel is not null && AppReleaseVersion.ChannelFor(manifest.Version) != channel)
                || (version is not null && (manifest.Version != version || !string.Equals(manifest.Sha256, sha256, StringComparison.OrdinalIgnoreCase)))
                || string.IsNullOrWhiteSpace(manifest.Filename)
                || manifest.Filename is "." or ".."
                || manifest.Filename.IndexOfAny(['/', '\\', ':']) >= 0
                || manifest.Filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !StudentUpdateSignature.VerifyApp(app, manifest.Version, manifest.Size, manifest.Sha256, manifest.Signature))
                return null;
            content = new FileStream(Path.Combine(directory, manifest.Filename), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (content.Length != manifest.Size) return null;
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
            if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            content.Position = 0;
            var result = (manifest, content);
            content = null; // Transfer ownership of this verified handle to the caller.
            return result;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            return null;
        }
        finally { content?.Dispose(); }
    }

    /// <summary>
    /// Republishes a signed Student binary into its version's release/beta channel.
    /// </summary>
    public AppUpdateManifest PublishStudentUpdate(string sourceExePath, string version, string signature) =>
        PublishAppUpdate("student", sourceExePath, version, signature);

    public AppUpdateManifest PublishAppUpdate(string app, string sourceExePath, string version, string signature)
    {
        if (!AppReleaseVersion.IsValid(version))
            throw new ArgumentException("Некорректная версия обновления.", nameof(version));
        var channel = AppReleaseVersion.ChannelFor(version);
        ValidateAppChannel(app, channel);
        if (!File.Exists(sourceExePath))
            throw new FileNotFoundException("Не найден собранный файл приложения.", sourceExePath);

        using var source = new FileStream(sourceExePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var size = source.Length;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant();
        if (!StudentUpdateSignature.VerifyApp(app, version, size, hash, signature))
            throw new InvalidOperationException("Недействительная подпись обновления приложения.");
        var filename = $"KIBERone{(app == "student" ? "Student" : "Tutor")}-{hash}.exe";
        var directory = Path.Combine(updatesDirectory, channel);
        Directory.CreateDirectory(directory);
        var binaryPath = Path.Combine(directory, filename);
        var manifestPath = Path.Combine(directory, app + "_manifest.json");
        var versionsDirectory = Path.Combine(directory, ".versions", app);
        var versionPath = Path.Combine(versionsDirectory, version + ".json");
        var suffix = "." + Guid.NewGuid().ToString("N") + ".tmp";
        var manifest = new AppUpdateManifest(version, filename, size, hash, DateTimeOffset.UtcNow, signature);
        try
        {
            source.Position = 0;
            using (var output = new FileStream(binaryPath + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.CopyTo(output);
            lock (gate)
            {
                Directory.CreateDirectory(versionsDirectory);
                var previousPaths = Directory.EnumerateFiles(versionsDirectory, "*.json")
                    .Append(Path.Combine(AppUpdateDirectory(app, channel), app + "_manifest.json"))
                    .Where(File.Exists).Distinct().ToArray();
                AppUpdateManifest? sameVersion = null;
                foreach (var previousPath in previousPaths)
                {
                    var previous = JsonSerializer.Deserialize<AppUpdateManifest>(File.ReadAllText(previousPath), UpdateJson)
                        ?? throw new InvalidDataException("Повреждён журнал опубликованных версий.");
                    if (!AppReleaseVersion.IsValid(previous.Version) || AppReleaseVersion.ChannelFor(previous.Version) != channel)
                        throw new InvalidDataException("Неверный канал опубликованной версии.");
                    if (previous.Version == version)
                    {
                        if (previous.Size != size || !string.Equals(previous.Sha256, hash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Опубликованную версию нельзя использовать для другого файла.");
                        sameVersion = previous;
                    }
                    else if (!AppReleaseVersion.IsNewer(version, previous.Version))
                        throw new InvalidOperationException("Публикация более старой версии запрещена.");
                }
                if (File.Exists(binaryPath))
                {
                    using var existing = File.OpenRead(binaryPath);
                    if (existing.Length != size || !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(existing)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Неизменяемый файл обновления повреждён.");
                }
                else File.Move(binaryPath + suffix, binaryPath); // Never overwrite a hash-named artifact.
                if (sameVersion is not null)
                    manifest = manifest with { PublishedAt = sameVersion.PublishedAt };
                var manifestJson = JsonSerializer.Serialize(manifest, UpdateJson);
                // Commit the version reservation before changing the current pointer.
                // If publication is interrupted, only an identical retry is allowed.
                if (!File.Exists(versionPath))
                {
                    File.WriteAllText(versionPath + suffix, manifestJson);
                    File.Move(versionPath + suffix, versionPath);
                }
                File.WriteAllText(manifestPath + suffix, manifestJson);
                File.Move(manifestPath + suffix, manifestPath, true);
            }
        }
        finally
        {
            if (File.Exists(binaryPath + suffix)) File.Delete(binaryPath + suffix);
            if (File.Exists(manifestPath + suffix)) File.Delete(manifestPath + suffix);
            if (File.Exists(versionPath + suffix)) File.Delete(versionPath + suffix);
        }
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
