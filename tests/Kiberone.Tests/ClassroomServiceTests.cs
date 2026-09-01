using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Tests;

public sealed class ClassroomServiceTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kiberone-classroom-{Guid.NewGuid():N}.db");
    private DbContextOptions<ClassroomDbContext> options = null!;
    private ClassroomService service = null!;
    private Guid studentId;

    public async Task InitializeAsync()
    {
        options = ClassroomDatabase.CreateOptions(databasePath);
        await ClassroomDatabase.InitializeAsync(options);
        service = new ClassroomService(options);
        var group = await service.CreateGroupAsync(new GroupDraft("Unity 01", "GameDev", "Основы Unity"));
        var student = await service.CreateStudentAsync(new StudentDraft("Иванов", "Максим", 12, group.Id, "", "", "crm-1"));
        studentId = student.Id;
    }

    [Fact]
    public async Task Achievement_IsAwardedOnlyOnce_WithSingleReward()
    {
        var achievement = await service.CreateAchievementAsync(new AchievementDraft("first_win", "Первая победа", "", "cup", 75, 12));

        var first = await service.AwardAchievementAsync(new AwardAchievementRequest(studentId, achievement.Id, "Турнир"));
        var second = await service.AwardAchievementAsync(new AwardAchievementRequest(studentId, achievement.Id, "Повтор"));

        Assert.Equal(first.Id, second.Id);
        await using var db = new ClassroomDbContext(options);
        var student = await db.Students.SingleAsync(x => x.Id == studentId);
        Assert.Equal(75, student.Xp);
        Assert.Equal(12, student.Kiberons);
        Assert.Single(await db.StudentAchievements.ToListAsync());
        Assert.Single(await db.KiberonTransactions.ToListAsync());
    }

    [Fact]
    public async Task Balance_CannotBecomeNegative()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AdjustKiberonsAsync(new AdjustKiberonsRequest(studentId, -1, "Штраф")));

        Assert.Contains("не может быть отрицательным", error.Message);
        await using var db = new ClassroomDbContext(options);
        Assert.Equal(0, await db.Students.Where(x => x.Id == studentId).Select(x => x.Kiberons).SingleAsync());
        Assert.Empty(await db.KiberonTransactions.ToListAsync());
    }

    [Fact]
    public async Task PurchaseAndRejection_AreAtomicAndRefundStockAndBalance()
    {
        await service.AdjustKiberonsAsync(new AdjustKiberonsRequest(studentId, 50, "Стартовый баланс"));
        var item = await service.CreateStoreItemAsync(new StoreItemDraft("sticker", "Наклейка", "", 30, 2, false));

        var purchase = await service.PurchaseAsync(new PurchaseRequest(studentId, item.Id));
        Assert.Equal(20, purchase.BalanceAfter);
        Assert.Equal(1, purchase.StockAfter);
        var order = await service.UpdateOrderStatusAsync(purchase.Order.Id, new UpdateOrderStatusRequest(StoreOrderStatus.Rejected, "Нет в классе"));

        Assert.Equal(StoreOrderStatus.Rejected, order?.Status);
        await using var db = new ClassroomDbContext(options);
        Assert.Equal(50, await db.Students.Where(x => x.Id == studentId).Select(x => x.Kiberons).SingleAsync());
        Assert.Equal(2, await db.StoreItems.Where(x => x.Id == item.Id).Select(x => x.Stock).SingleAsync());
        Assert.Equal(3, await db.KiberonTransactions.CountAsync());
    }

    [Fact]
    public async Task SecretItem_IsNotListed_AndRequiresExactCode()
    {
        var secret = await service.CreateStoreItemAsync(new StoreItemDraft("sys_mr_67", "Мистер 67", "Секрет", 67, 1, true));

        Assert.Empty(await service.ListStoreItemsAsync());
        Assert.Null(await service.GetSecretItemAsync("wrong"));
        Assert.Equal(secret.Id, (await service.GetSecretItemAsync("sys_mr_67"))?.Id);
    }

    [Fact]
    public async Task RosterGradeAndCheckIn_ArePersisted()
    {
        var grade = await service.AddGradeAsync(new GradeDraft(studentId, null, 5, "Отлично"));
        var checkIn = await service.CheckInAsync(studentId, "Циклы", "PC-07", "client-07");
        var profile = await service.GetStudentAsync(studentId);

        Assert.Equal(5, grade.Value);
        Assert.Equal("PC-07", checkIn.PcNumber);
        Assert.Single(profile!.Grades);
    }

    [Fact]
    public async Task SystemAchievement_IsSecretAndIdempotent()
    {
        var first = await service.TriggerSystemAchievementAsync(studentId, "games_addict");
        var second = await service.TriggerSystemAchievementAsync(studentId, "games_addict");

        Assert.Equal(first.Id, second.Id);
        Assert.Empty(await service.ListAchievementsAsync());
        await using var db = new ClassroomDbContext(options);
        Assert.Equal(25, await db.Students.Where(x => x.Id == studentId).Select(x => x.Xp).SingleAsync());
        Assert.Single(await db.StudentAchievements.ToListAsync());
    }

    [Fact]
    public async Task Statistics_AggregateGradesSessionsAndProgress()
    {
        await service.AddGradeAsync(new GradeDraft(studentId, null, 4, "Хорошо"));
        await service.AddGradeAsync(new GradeDraft(studentId, null, 5, "Отлично"));
        await service.CheckInAsync(studentId, "Python", "PC-1", "client-1");
        var student = await service.GetStudentStatisticsAsync(studentId);
        var group = await service.GetGroupStatisticsAsync(student!.GroupName == "Unity 01"
            ? (await service.ListGroupsAsync()).Single().Id
            : Guid.Empty);

        Assert.Equal(4.5, student.AverageGrade);
        Assert.Equal(1, student.SessionCount);
        Assert.NotNull(group);
        Assert.Equal(1, group!.StudentCount);
        Assert.Equal(4.5, group.AverageGrade);
    }

    [Fact]
    public async Task StudentAndShop_RejectInvalidEdgeInputs()
    {
        var group = (await service.ListGroupsAsync()).Single();
        await Assert.ThrowsAsync<LessonValidationException>(() =>
            service.CreateStudentAsync(new StudentDraft("", "Максим", 12, group.Id, "", "", "")));
        await Assert.ThrowsAsync<LessonValidationException>(() =>
            service.CreateStudentAsync(new StudentDraft("Иванов", "Максим", 4, group.Id, "", "", "")));
        await Assert.ThrowsAsync<LessonValidationException>(() =>
            service.AddGradeAsync(new GradeDraft(studentId, null, 6, "")));
        await Assert.ThrowsAsync<LessonValidationException>(() =>
            service.AdjustKiberonsAsync(new AdjustKiberonsRequest(studentId, 0, "ноль")));
        await Assert.ThrowsAsync<LessonValidationException>(() =>
            service.TriggerSystemAchievementAsync(studentId, "unknown_event"));

        var item = await service.CreateStoreItemAsync(new StoreItemDraft("pen", "Ручка", "", 40, 1, false));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PurchaseAsync(new PurchaseRequest(studentId, item.Id)));
        Assert.Contains("Недостаточно", error.Message);
        await using var db = new ClassroomDbContext(options);
        Assert.Equal(1, await db.StoreItems.Where(x => x.Id == item.Id).Select(x => x.Stock).SingleAsync());
        Assert.Equal(0, await db.Students.Where(x => x.Id == studentId).Select(x => x.Kiberons).SingleAsync());
    }

    public async Task DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
            if (File.Exists(path)) File.Delete(path);
    }
}

public sealed class ClassroomDatabaseSeedTests : IAsyncLifetime
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kiberone-seed-{Guid.NewGuid():N}.db");
    private DbContextOptions<ClassroomDbContext> options = null!;

    public async Task InitializeAsync()
    {
        options = ClassroomDatabase.CreateOptions(databasePath);
        await ClassroomDatabase.InitializeAsync(options);
    }

    [Fact]
    public async Task SeedDefaults_IsIdempotentAndMakesCoreFeaturesUsable()
    {
        await ClassroomDatabase.SeedDefaultsAsync(options);
        await ClassroomDatabase.SeedDefaultsAsync(options);

        await using var db = new ClassroomDbContext(options);
        Assert.Single(await db.Groups.ToListAsync());
        Assert.Equal(3, await db.Students.CountAsync());
        Assert.Equal(2, await db.TypingLessons.CountAsync());
        Assert.Equal(3, await db.Achievements.CountAsync());
        Assert.Equal(3, await db.StoreItems.CountAsync());
    }

    public async Task DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
            if (File.Exists(path)) File.Delete(path);
    }
}
