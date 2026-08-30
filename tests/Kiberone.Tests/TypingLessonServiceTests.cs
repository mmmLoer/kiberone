using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Tests;

public sealed class TypingLessonServiceTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kiberone-tests-{Guid.NewGuid():N}.db");
    private DbContextOptions<ClassroomDbContext> options = null!;

    public async Task InitializeAsync()
    {
        options = ClassroomDatabase.CreateOptions(databasePath);
        await ClassroomDatabase.InitializeAsync(options);
    }

    [Fact]
    public async Task CreateStartTelemetryFinish_IsPersistedEndToEnd()
    {
        var service = new TypingLessonService(options);
        Guid groupId;
        Guid studentId;
        await using (var db = new ClassroomDbContext(options))
        {
            var group = new ClassroomGroup { Name = "Python 01" };
            var student = new Student { FirstName = "Софья", LastName = "Петрова", GroupId = group.Id };
            group.Students.Add(student);
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
            studentId = student.Id;
        }
        var lesson = await service.CreateLessonAsync(new CreateLessonRequest(
            "Циклы Python", "Практика кода", LessonContentKind.Code, "en-US", 10, 5,
            [new LessonStepDraft("Цикл for", "for i in range(10):\n    print(i)", 60, 90)]));
        var session = await service.StartSessionAsync(new StartTypingSessionRequest(lesson.Id, groupId, [studentId]));

        var live = await service.RecordTelemetryAsync(session.Id,
            new TelemetryUpdateRequest(studentId, 0, 30, 2, 30, 4, ParticipantStatus.Finished, new Dictionary<string, int> { ["("] = 2 }));
        var finished = await service.FinishSessionAsync(session.Id);

        Assert.NotNull(live);
        Assert.Equal(60, live.Participants[0].Cpm);
        Assert.Equal(93.8, live.Participants[0].Accuracy);
        Assert.Equal(1, finished?.Winners.Count);
        await using var verify = new ClassroomDbContext(options);
        Assert.Equal(20, await verify.Students.Where(x => x.Id == studentId).Select(x => x.Xp).SingleAsync());
        Assert.Single(await verify.TypingTelemetry.ToListAsync());
    }

    [Fact]
    public async Task ListLessons_OrdersDateTimeOffsetFieldsWithoutSqliteTranslation()
    {
        var service = new TypingLessonService(options);
        var first = await service.CreateLessonAsync(new CreateLessonRequest(
            "Первый урок", "Проверка сортировки", LessonContentKind.Custom, "ru-RU", 10, 5,
            [new LessonStepDraft("Шаг", "Первый", 10, 80)]));
        var second = await service.CreateLessonAsync(new CreateLessonRequest(
            "Второй урок", "Проверка сортировки", LessonContentKind.Custom, "ru-RU", 10, 5,
            [new LessonStepDraft("Шаг", "Второй", 10, 80)]));

        await using (var db = new ClassroomDbContext(options))
        {
            var firstEntity = await db.TypingLessons.SingleAsync(x => x.Id == first.Id);
            firstEntity.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync();
        }

        var lessons = await service.ListLessonsAsync();

        Assert.Equal(first.Id, lessons[0].Id);
        Assert.Contains(lessons, x => x.Id == second.Id);
    }

    public async Task DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath)) File.Delete(databasePath);
        if (File.Exists(databasePath + "-shm")) File.Delete(databasePath + "-shm");
        if (File.Exists(databasePath + "-wal")) File.Delete(databasePath + "-wal");
    }
}
