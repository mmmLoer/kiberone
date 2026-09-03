using System.Reflection;
using System.Text;

namespace Kiberone.Core;

public sealed record VpnRegionInfo(
    string Id,
    string Name,
    string CheckHost,
    bool IsPrimary,
    int PeerCount = 0,
    string PublicHost = "",
    int WgPort = 51821,
    int StatusPort = 9108)
{
    public string StatusBaseUrl => string.IsNullOrWhiteSpace(PublicHost)
        ? string.Empty
        : $"http://{PublicHost}:{StatusPort}";
}

public sealed record VpnPeerFile(string FileName, string Content);

public sealed record VpnPeerPack(string RegionId, string RegionName, string CheckHost, IReadOnlyList<VpnPeerFile> Files);

public sealed record VpnProbeConfig(string RegionId, string FileName, string Content);

public sealed record AppUpdateManifest(string Version, string Filename, long Size, string Sha256, DateTimeOffset PublishedAt);

public static class VpnRegionCatalog
{
    public const string AutoName = "Авто";
    public const string FranceId = "path-fr"; // historical id; UI name is Germany
    public const string NetherlandsId = "path-nl";
    public const string PrimaryId = FranceId;
    public const string SecondaryId = NetherlandsId;

    public static IReadOnlyList<VpnRegionInfo> All { get; } =
    [
        new(FranceId, "Германия", "51.89.174.71", true, PublicHost: "193.233.220.158", WgPort: 51822, StatusPort: 9108),
        new(NetherlandsId, "Нидерланды", "193.235.147.228", false, PublicHost: "80.90.188.85", WgPort: 51823, StatusPort: 9108)
    ];

    public static VpnRegionInfo Primary => All[0];

    public static IReadOnlyList<string> Names() => [AutoName, .. All.Select(region => region.Name)];

    public static bool IsAuto(string? idOrName) =>
        string.IsNullOrWhiteSpace(idOrName)
        || idOrName.Trim().Equals(AutoName, StringComparison.OrdinalIgnoreCase)
        || idOrName.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

    public static VpnRegionInfo Resolve(string? idOrName)
    {
        if (IsAuto(idOrName))
            return Primary;

        var key = idOrName!.Trim();
        if (key is "vpn-1" or "Сервер 1" or "Англия" or "England" or "France" or "path-de" or "Germany")
            return All[0];
        if (key is "vpn-2" or "Сервер 2")
            return All[1];

        return All.FirstOrDefault(region =>
                   string.Equals(region.Id, key, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(region.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? Primary;
    }

    public static VpnRegionInfo Other(string? idOrName)
    {
        var current = Resolve(idOrName);
        return All.First(region => !string.Equals(region.Id, current.Id, StringComparison.OrdinalIgnoreCase));
    }

    public static string CacheFolder(string regionId)
    {
        var safe = string.Join("_", Resolve(regionId).Id.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KIBERone",
            "Tutor",
            "vpn",
            safe);
    }
}

public static class VpnProbeConfigs
{
    public static IReadOnlyList<VpnProbeConfig> All { get; } = Load();

    public static bool AreReady =>
        VpnRegionCatalog.All.All(region => ForRegionPool(region.Id, rotate: false).Count >= 2)
        && All.All(probe => !IsPlaceholder(probe.Content));

    public static bool IsPlaceholder(string content) =>
        content.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);

    public static VpnProbeConfig? ForRegion(string regionId) =>
        ForRegionPool(regionId).FirstOrDefault();

    public static IReadOnlyList<VpnProbeConfig> ForRegionPool(string regionId, bool rotate = true)
    {
        var id = VpnRegionCatalog.Resolve(regionId).Id;
        var probes = All
            .Where(probe => string.Equals(probe.RegionId, id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(probe => probe.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!rotate || probes.Count <= 1)
            return probes;

        var offset = Math.Abs(HashCode.Combine(Environment.MachineName, Environment.UserName, id)) % probes.Count;
        return probes.Skip(offset).Concat(probes.Take(offset)).ToList();
    }

    private static IReadOnlyList<VpnProbeConfig> Load()
    {
        var assembly = typeof(VpnProbeConfigs).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Data.vpn-probe-", StringComparison.OrdinalIgnoreCase)
                           && name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(resource =>
            {
                using var stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Не найден встроенный VPN-пробник {resource}.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var content = reader.ReadToEnd();
                var fileName = resource[(resource.LastIndexOf(".Data.", StringComparison.OrdinalIgnoreCase) + 6)..];
                var region = VpnConfigText.ReadComment(content, "Kiberone-Region") ?? VpnRegionCatalog.PrimaryId;
                return new VpnProbeConfig(region, fileName, content);
            })
            .ToList();
    }
}

public static class VpnConfigText
{
    public static string? ReadAssignment(string content, string key)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(key))
            return null;

        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('='))
                continue;
            var parts = trimmed.Split('=', 2);
            if (parts.Length != 2)
                continue;
            if (parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return parts[1].Trim();
        }

        return null;
    }

    public static string? ReadComment(string content, string key)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(key))
            return null;

        var prefix = $"# {key}:";
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].Trim();
        }

        return null;
    }

    public static string? CheckHost(string content, string? fallback = null)
    {
        var fromComment = ReadComment(content, "Kiberone-CheckHost");
        if (!string.IsNullOrWhiteSpace(fromComment))
            return fromComment;

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }
}
