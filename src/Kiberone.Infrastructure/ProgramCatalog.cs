using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public static class ProgramCalendar
{
    public static GroupProgramModule? Pick(IReadOnlyList<GroupProgramModule> modules, DateOnly today)
    {
        if (modules.Count == 0) return null;
        var ordered = modules.OrderBy(x => x.StartDate).ThenBy(x => x.SortOrder).ToList();
        var covering = ordered.FirstOrDefault(x => today >= x.StartDate && today <= x.EndDate);
        if (covering is not null) return covering;
        return ordered.FirstOrDefault(x => x.StartDate > today) ?? ordered.Last();
    }
}

public static class ProgramCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<LocationProgram> Load2026()
    {
        var assembly = typeof(ProgramCatalog).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("program-2026-2027.json", StringComparison.OrdinalIgnoreCase))
            ?? assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("shb-2026-2027.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Не найден каталог программы 2026-2027.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        if (name.EndsWith("shb-2026-2027.json", StringComparison.OrdinalIgnoreCase))
        {
            var groups = JsonSerializer.Deserialize<List<ShbGroupProgram>>(json, Json) ?? [];
            return [new LocationProgram("ШБ", "ШБ 2026-2027", groups)];
        }

        return JsonSerializer.Deserialize<List<LocationProgram>>(json, Json) ?? [];
    }

    public static IReadOnlyList<string> LocationNames() =>
        Load2026().Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string ContentHash()
    {
        using var stream = OpenCatalogStream();
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hasher.AppendData(buffer.AsSpan(0, read));
        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private static Stream OpenCatalogStream()
    {
        var assembly = typeof(ProgramCatalog).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("program-2026-2027.json", StringComparison.OrdinalIgnoreCase))
            ?? assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("shb-2026-2027.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Не найден каталог программы 2026-2027.");
        return assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
    }
}

public sealed record LocationProgram(string Name, string Sheet, IReadOnlyList<ShbGroupProgram> Groups);
public sealed record ShbGroupProgram(string Name, IReadOnlyList<ShbModuleProgram> Modules);
public sealed record ShbModuleProgram(string Name, string Start, string End, int? Lessons, string? Comment);
