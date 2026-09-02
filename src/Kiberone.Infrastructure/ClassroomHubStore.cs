using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class ClassroomHubStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string rosterDirectory;
    private readonly IReadOnlyDictionary<string, LocationSecretRecord> secrets;
    private readonly object gate = new();

    public ClassroomHubStore(string dataDirectory, IEnumerable<LocationSecretRecord> secrets)
    {
        rosterDirectory = Path.Combine(dataDirectory, "rosters");
        Directory.CreateDirectory(rosterDirectory);
        this.secrets = secrets
            .Where(x => !string.IsNullOrWhiteSpace(x.Location))
            .ToDictionary(x => x.Location.Trim(), x => x, StringComparer.OrdinalIgnoreCase);
    }

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
}
