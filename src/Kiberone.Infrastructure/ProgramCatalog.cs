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

    public static IReadOnlyList<ShbGroupProgram> LoadShb2026()
    {
        var assembly = typeof(ProgramCatalog).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith("shb-2026-2027.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Не найден каталог ШБ 2026-2027.");
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<List<ShbGroupProgram>>(json, Json) ?? [];
    }
}

public sealed record ShbGroupProgram(string Name, IReadOnlyList<ShbModuleProgram> Modules);
public sealed record ShbModuleProgram(string Name, string Start, string End, int? Lessons, string? Comment);
