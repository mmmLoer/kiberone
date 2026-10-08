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
            var student = new Kiberone.Core.Student { FirstName = "Софья", LastName = "Петрова", GroupId = group.Id };
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
    public async Task CompletedStudentAttempt_AppearsInStudentAndGroupStats_WithoutDuplicateOnRetry()
    {
        var service = new TypingLessonService(options);
        Guid studentId;
        Guid groupId;
        await using (var db = new ClassroomDbContext(options))
        {
            var group = new ClassroomGroup { Name = "Тестовая группа" };
            var student = new Kiberone.Core.Student { FirstName = "Анна", LastName = "Иванова", GroupId = group.Id };
            group.Students.Add(student);
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            studentId = student.Id;
            groupId = group.Id;
        }

        var attempt = new TypingAttemptDraft(
            Guid.NewGuid(), null, "Пробный урок", "тест", 4, 3, 1, 6, 2,
            new Dictionary<string, int> { ["т"] = 1 });
        var request = new SubmitTypingAttemptRequest(studentId, attempt);
        var first = await service.RecordAttemptAsync(request);
        var retry = await service.RecordAttemptAsync(request);
        var studentStats = await service.GetTypingStatsAsync(null, studentId, null);
        var groupStats = await service.GetTypingStatsAsync(groupId, null, null);

        Assert.Equal(attempt.AttemptId, first.SessionId);
        Assert.Equal(first.SessionId, retry.SessionId);
        Assert.Equal(TypingSessionStatus.Finished, first.Status);
        Assert.Equal(1, Assert.Single(studentStats.Points).Attempts);
        Assert.Equal(3, Assert.Single(groupStats.Points).TotalCorrectKeys);
        Assert.Empty(await service.ListCatalogAsync());
        await using var verify = new ClassroomDbContext(options);
        Assert.Single(await verify.TypingSessions.ToListAsync());
        Assert.Single(await verify.TypingTelemetry.ToListAsync());
    }

    [Fact]
    public async Task LiveLesson_ReusesOneLessonForTheWholeClass()
    {
        var service = new TypingLessonService(options);
        var first = await service.EnsureLiveLessonAsync("Общий урок", "текст для класса", 5);
        var second = await service.EnsureLiveLessonAsync("Общий урок", "текст для класса", 5);

        Assert.Equal(first, second);
        Assert.Empty(await service.ListCatalogAsync());
        await using var db = new ClassroomDbContext(options);
        Assert.Single(await db.TypingLessons.ToListAsync());
    }

    [Fact]
    public async Task TwoStudentsOnSameLiveLesson_AppearAsTwoAttemptsInOneChartPoint()
    {
        var service = new TypingLessonService(options);
        Guid groupId;
        Guid[] studentIds;
        await using (var db = new ClassroomDbContext(options))
        {
            var group = new ClassroomGroup { Name = "Общий класс" };
            var students = new[]
            {
                new Kiberone.Core.Student { FirstName = "Анна", LastName = "Иванова", GroupId = group.Id },
                new Kiberone.Core.Student { FirstName = "Борис", LastName = "Петров", GroupId = group.Id }
            };
            group.Students.AddRange(students);
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
            studentIds = students.Select(x => x.Id).ToArray();
        }

        var lessonId = await service.EnsureLiveLessonAsync("Задание класса", "текст", 5);
        foreach (var studentId in studentIds)
        {
            var attempt = new TypingAttemptDraft(Guid.NewGuid(), lessonId, "Задание класса", "текст", 5,
                5, 0, 10, 0, new Dictionary<string, int>());
            await service.RecordAttemptAsync(new SubmitTypingAttemptRequest(studentId, attempt));
        }

        var point = Assert.Single((await service.GetTypingStatsAsync(groupId, null, null)).Points);
        Assert.Equal(lessonId, point.LessonId);
        Assert.Equal(2, point.Attempts);
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

    [Fact]
    public async Task Telemetry_RejectsDecreasingCountersAndEmptyStudentList()
    {
        var service = new TypingLessonService(options);
        Guid groupId;
        Guid studentId;
        await using (var db = new ClassroomDbContext(options))
        {
            var group = new ClassroomGroup { Name = "Edge" };
            var student = new Kiberone.Core.Student { FirstName = "Ева", LastName = "Ким", GroupId = group.Id };
            group.Students.Add(student);
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            groupId = group.Id;
            studentId = student.Id;
        }

        var lesson = await service.CreateLessonAsync(new CreateLessonRequest(
            "Край", "Проверка", LessonContentKind.Custom, "ru-RU", 10, 5,
            [new LessonStepDraft("Шаг", "текст", 10, 80)]));
        await Assert.ThrowsAsync<LessonValidationException>(() =>
            service.StartSessionAsync(new StartTypingSessionRequest(lesson.Id, groupId, [])));
        var session = await service.StartSessionAsync(new StartTypingSessionRequest(lesson.Id, groupId, [studentId]));
        await service.RecordTelemetryAsync(session.Id,
            new TelemetryUpdateRequest(studentId, 0, 10, 1, 10, 0, ParticipantStatus.Typing, new Dictionary<string, int>()));
        await Assert.ThrowsAsync<LessonValidationException>(() => service.RecordTelemetryAsync(session.Id,
            new TelemetryUpdateRequest(studentId, 0, 5, 1, 10, 0, ParticipantStatus.Typing, new Dictionary<string, int>())));
    }

    [Fact]
    public async Task StarterLessons_AreNotLockedPresets()
    {
        await ClassroomDatabase.SeedDefaultsAsync(options);
        var service = new TypingLessonService(options);
        var lessons = await service.ListLessonsAsync();
        Assert.Equal(3, lessons.Count);
        Assert.Contains(lessons, x => x.Name == "Дурак и молния — без знаков");
        Assert.Contains(lessons, x => x.Name == "Дурак и молния — как в тексте");
        Assert.Contains(lessons, x => x.Name == "Fool and Lightning");
        Assert.All(lessons, lesson => Assert.False(TypingLessonCatalog.IsDefaultName(lesson.Name)));
    }

    [Fact]
    public async Task PlaceholderLessons_AreHiddenFromStudentUntilTutorAddsText()
    {
        await ClassroomDatabase.SeedDefaultsAsync(options);
        var service = new TypingLessonService(options);
        Assert.Empty(await service.ListCatalogAsync());

        var lesson = (await service.ListLessonsAsync()).First();
        var updated = await service.UpdateLessonAsync(lesson.Id, new UpdateLessonRequest(
            lesson.Name, lesson.Description, lesson.ContentKind, lesson.KeyboardLayout,
            lesson.MinimumCharacters, lesson.DurationMinutes, lesson.Lifecycle,
            [new LessonStepDraft("Текст", "Собственный текст для урока печати")]));

        Assert.NotNull(updated);
        Assert.Contains(await service.ListCatalogAsync(), x => x.Id == lesson.Id);
    }

    [Fact]
    public async Task StarterLessons_CanBeUpdatedByTutor()
    {
        await ClassroomDatabase.SeedDefaultsAsync(options);
        var service = new TypingLessonService(options);
        var lessons = await service.ListLessonsAsync();
        var preset = Assert.Single(lessons, x => x.Name == "Дурак и молния — без знаков");

        var updated = await service.UpdateLessonAsync(preset.Id, new UpdateLessonRequest(
            preset.Name, "changed", LessonContentKind.Custom, "ru-RU", 50, 10, LessonLifecycle.Published,
            [new LessonStepDraft("Текст", "новый текст")]));

        Assert.NotNull(updated);
        Assert.Equal("новый текст", TypingLessonCatalog.GetLessonText(updated!));
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
