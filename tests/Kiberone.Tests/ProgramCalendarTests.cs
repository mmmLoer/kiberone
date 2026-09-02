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
    public async Task ImportProgram_LoadsAllLocationsAndFiltersGroups()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiberone-shb-{Guid.NewGuid():N}.db");
        var options = ClassroomDatabase.CreateOptions(path);
        await ClassroomDatabase.InitializeAsync(options);
        var service = new ClassroomService(options);

        var count = await service.ImportShbProgramAsync();
        var shb = await service.ListGroupsAsync("ШБ");
        var aksakova = await service.ListGroupsAsync("АКСАКОВА 2");
        var saturday = shb.Single(x => x.Name == "Мл3Сб10 Школа будущего");
        var modules = await service.ListProgramModulesAsync(saturday.Id);
        var current = await service.ApplyCurrentModuleAsync(saturday.Id, DateOnly.Parse("2026-09-02"));

        Assert.Equal(129, count);
        Assert.Equal(12, ProgramCatalog.LocationNames().Count);
        Assert.Contains("АРТЕША", ProgramCatalog.LocationNames());
        Assert.Equal(8, shb.Count);
        Assert.True(aksakova.Count > 0);
        Assert.DoesNotContain(shb, x => x.Name == aksakova[0].Name);
        Assert.Equal("ШБ", saturday.Location);
        Assert.Contains(shb, x => x.Name == "Ср4Сб12 Школа будущего");
        Assert.Contains(shb, x => x.Name == "Ст4Вс14 Школа будущего");
        Assert.True(modules.Count >= 6);
        Assert.Equal("Figma", current?.Name);
        Assert.Equal("Figma", (await service.ListGroupsAsync("ШБ")).Single(x => x.Id == saturday.Id).Module);

        var again = await service.ImportShbProgramAsync();
        var modulesAfterRestart = await service.ListProgramModulesAsync(saturday.Id);
        Assert.Equal(129, again);
        Assert.Equal(modules.Count, modulesAfterRestart.Count);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public async Task ImportProgram_OnlyCreatesGroupsForSelectedLocation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiberone-loc-{Guid.NewGuid():N}.db");
        var options = ClassroomDatabase.CreateOptions(path);
        await ClassroomDatabase.InitializeAsync(options);
        var service = new ClassroomService(options);

        var count = await service.ImportShbProgramAsync("АКСАКОВА 2");
        await service.KeepOnlyLocationAsync("АКСАКОВА 2");
        var all = await service.ListGroupsAsync();
        var aksakova = await service.ListGroupsAsync("АКСАКОВА 2");
        var shb = await service.ListGroupsAsync("ШБ");

        Assert.Equal(aksakova.Count, count);
        Assert.Equal(all.Count, aksakova.Count);
        Assert.True(aksakova.Count > 0);
        Assert.Empty(shb);
        Assert.All(all, x => Assert.Equal("АКСАКОВА 2", x.Location));

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public async Task ImportProgramIfNeeded_SkipsUnchangedCatalog()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiberone-hash-{Guid.NewGuid():N}.db");
        var marker = path + ".hash";
        var options = ClassroomDatabase.CreateOptions(path);
        await ClassroomDatabase.InitializeAsync(options);
        var service = new ClassroomService(options);

        var first = await service.ImportProgramIfNeededAsync(marker);
        var second = await service.ImportProgramIfNeededAsync(marker);
        var forced = await service.ImportProgramIfNeededAsync(marker, true);

        Assert.True(first.Ran);
        Assert.Equal(129, first.Groups);
        Assert.False(second.Ran);
        Assert.True(forced.Ran);
        Assert.Equal(129, forced.Groups);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
        if (File.Exists(marker)) File.Delete(marker);
    }

    [Fact]
    public async Task ImportProgramIfNeeded_CompletesOnThreadPool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiberone-pool-{Guid.NewGuid():N}.db");
        var marker = path + ".hash";
        var options = ClassroomDatabase.CreateOptions(path);
        var result = await Task.Run(async () =>
        {
            await ClassroomDatabase.InitializeAsync(options);
            var service = new ClassroomService(options);
            return await service.ImportProgramIfNeededAsync(marker);
        });

        Assert.True(result.Ran);
        Assert.Equal(129, result.Groups);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
        if (File.Exists(marker)) File.Delete(marker);
    }

    private static GroupProgramModule Mod(string name, string start, string end) => new()
    {
        Name = name,
        StartDate = DateOnly.Parse(start),
        EndDate = DateOnly.Parse(end)
    };
}
