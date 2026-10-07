namespace Kiberone.Core;

public sealed record ClassroomAccessPolicy(
    string Revision, bool Enabled, bool OnlyAllowedApps, IReadOnlyList<string> AllowedApps,
    IReadOnlyList<string> BlockedApps, bool OnlyAllowedSites, IReadOnlyList<string> AllowedSites,
    IReadOnlyList<string> BlockedSites)
{
    public static ClassroomAccessPolicy Empty { get; } = new("", false, false, [], [], false, [], []);
}

public sealed record InstalledApplication(string Executable, string Name, string? IconBase64 = null);
public sealed record ApplicationInventoryRequest(string ClientId, IReadOnlyList<InstalledApplication> Applications);
public sealed record ClassroomApplication(InstalledApplication Application, IReadOnlyList<string> ClientIds);

public static class SiteRule
{
    public static string Normalize(string value)
    {
        var input = value.Trim();
        if (input.Length > 2048 || !Uri.TryCreate(input.Contains("://") ? input : "https://" + input, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0)
            throw new ArgumentException("Укажите домен или HTTP(S)-адрес сайта.");
        return uri.IdnHost.ToLowerInvariant() + (uri.AbsolutePath == "/" ? "" : uri.AbsolutePath.TrimEnd('/'));
    }
}
