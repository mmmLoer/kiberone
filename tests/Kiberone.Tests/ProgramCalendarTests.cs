using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class ProgramCalendarTests
{
    [Fact]
    public void Pick_UsesCoveringDates_ThenUpcoming_ThenLast()
    {
        var modules = new[]
        {
            Mod("Figma", "2026-09-05", "2026-10-10"),
            Mod("Tilda", "2026-10-17", "2026-10-24"),
            Mod("Spline", "2027-02-27", "2027-04-10")
        };

        Assert.Equal("Figma", ProgramCalendar.Pick(modules, DateOnly.Parse("2026-09-02"))?.Name);
        Assert.Equal("Figma", ProgramCalendar.Pick(modules, DateOnly.Parse("2026-09-05"))?.Name);
        Assert.Equal("Tilda", ProgramCalendar.Pick(modules, DateOnly.Parse("2026-10-20"))?.Name);
        Assert.Equal("Spline", ProgramCalendar.Pick(modules, DateOnly.Parse("2026-11-01"))?.Name);
        Assert.Equal("Spline", ProgramCalendar.Pick(modules, DateOnly.Parse("2027-06-01"))?.Name);
    }

    [Fact]
    public async Task ImportShbProgram_CreatesSchoolOfFutureGroups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiberone-shb-{Guid.NewGuid():N}.db");
        var options = ClassroomDatabase.CreateOptions(path);
        await ClassroomDatabase.InitializeAsync(options);
        var service = new ClassroomService(options);

        var count = await service.ImportShbProgramAsync();
        var groups = await service.ListGroupsAsync();
        var saturday = groups.Single(x => x.Name == "Мл3Сб10 Школа будущего");
        var modules = await service.ListProgramModulesAsync(saturday.Id);
        var current = await service.ApplyCurrentModuleAsync(saturday.Id, DateOnly.Parse("2026-09-02"));

        Assert.Equal(8, count);
        Assert.Contains(groups, x => x.Name == "Ср4Сб12 Школа будущего");
        Assert.Contains(groups, x => x.Name == "Ст4Вс14 Школа будущего");
        Assert.True(modules.Count >= 6);
        Assert.Equal("Figma", current?.Name);
        Assert.Equal("Figma", (await service.ListGroupsAsync()).Single(x => x.Id == saturday.Id).Module);

        var again = await service.ImportShbProgramAsync();
        var modulesAfterRestart = await service.ListProgramModulesAsync(saturday.Id);
        Assert.Equal(8, again);
        Assert.Equal(modules.Count, modulesAfterRestart.Count);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    private static GroupProgramModule Mod(string name, string start, string end) => new()
    {
        Name = name,
        StartDate = DateOnly.Parse(start),
        EndDate = DateOnly.Parse(end)
    };
}
