using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Kiberone.Core;

public sealed record VpnRegionInfo(string Id, string Name, string CheckHost, bool IsPrimary, int PeerCount = 0);

public sealed record VpnPeerFile(string FileName, string Content);

public sealed record VpnPeerPack(string RegionId, string RegionName, string CheckHost, IReadOnlyList<VpnPeerFile> Files);

public sealed record VpnProbeConfig(string RegionId, string FileName, string Content);

public sealed record AppUpdateManifest(string Version, string Filename, long Size, string Sha256, DateTimeOffset PublishedAt);

public static class VpnRegionCatalog
{
    public const string PrimaryId = "vpn-1";
    public const string SecondaryId = "vpn-2";

    public static IReadOnlyList<VpnRegionInfo> All { get; } =
    [
        new(PrimaryId, "Сервер 1", "1.1.1.1", true),
        new(SecondaryId, "Сервер 2", "1.1.1.1", false)
    ];

    public static VpnRegionInfo Primary => All[0];

    public static IReadOnlyList<string> Names() => All.Select(region => region.Name).ToList();

    public static VpnRegionInfo Resolve(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
            return Primary;

        var key = idOrName.Trim();
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

    public static bool AreReady => All.Count >= 2 && All.All(probe => !IsPlaceholder(probe.Content));

    public static bool IsPlaceholder(string content) =>
        content.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<VpnProbeConfig> Load()
    {
        var assembly = typeof(VpnProbeConfigs).Assembly;
        return new[] { "vpn-probe-1.conf", "vpn-probe-2.conf" }
            .Select(name =>
            {
                var resource = assembly.GetManifestResourceNames()
                    .First(x => x.EndsWith(name, StringComparison.OrdinalIgnoreCase));
                using var stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Не найден встроенный VPN-пробник {name}.");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var content = reader.ReadToEnd();
                var region = VpnConfigText.ReadComment(content, "Kiberone-Region") ?? VpnRegionCatalog.PrimaryId;
                return new VpnProbeConfig(region, name, content);
            })
            .ToList();
    }
}

public static partial class VpnConfigText
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

        var endpoint = ReadAssignment(content, "Endpoint");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var host = EndpointHost().Match(endpoint);
            if (host.Success)
                return host.Groups["host"].Value.Trim('[', ']');
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    [GeneratedRegex(@"^(?<host>\[[^\]]+\]|[^:]+):(?<port>\d+)$")]
    private static partial Regex EndpointHost();
}
