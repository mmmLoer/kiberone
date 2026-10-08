using System.Text.RegularExpressions;

namespace Kiberone.Core;

public static partial class AppReleaseVersion
{
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(b)?$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value)
        && System.Version.TryParse(value.TrimEnd('b'), out _);

    public static string ChannelFor(string version) => IsValid(version)
        ? version.EndsWith('b') ? "beta" : "release"
        : throw new ArgumentException("Invalid application version.", nameof(version));

    public static bool IsNewer(string candidate, string current) => IsValid(candidate)
        && IsValid(current) && ChannelFor(candidate) == ChannelFor(current)
        && System.Version.Parse(candidate.TrimEnd('b')) > System.Version.Parse(current.TrimEnd('b'));
}
