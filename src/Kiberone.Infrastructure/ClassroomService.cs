using Kiberone.Core;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Infrastructure;

public sealed class ClassroomService(DbContextOptions<ClassroomDbContext> options)
{
    public async Task<IReadOnlyList<ClassroomGroup>> ListGroupsAsync(CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        return await db.Groups.AsNoTracking().Include(x => x.Students).OrderBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<ClassroomGroup> CreateGroupAsync(GroupDraft draft, CancellationToken ct = default)
    {
        var name = Required(draft.Name, "Название группы", 120);
        await using var db = new ClassroomDbContext(options);
        if (await db.Groups.AnyAsync(x => x.Name == name, ct))
            throw new InvalidOperationException("Группа с таким названием уже существует.");
        var group = new ClassroomGroup
        {
            Name = name,
            Module = Trim(draft.Module, 160),
            Topics = Trim(draft.Topics, 1000)
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<ClassroomGroup?> UpdateGroupAsync(Guid id, GroupDraft draft, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var group = await db.Groups.FindAsync([id], ct);
        if (group is null) return null;
        var name = Required(draft.Name, "Название группы", 120);
        if (await db.Groups.AnyAsync(x => x.Id != id && x.Name == name, ct))
            throw new InvalidOperationException("Группа с таким названием уже существует.");
        group.Name = name;
        group.Module = Trim(draft.Module, 160);
        group.Topics = Trim(draft.Topics, 1000);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<IReadOnlyList<StudentSummary>> ListStudentsAsync(Guid? groupId = null, string? query = null, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var students = db.Students.AsNoTracking().Include(x => x.Group).AsQueryable();
        if (groupId is not null) students = students.Where(x => x.GroupId == groupId);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalized = query.Trim();
            students = students.Where(x => x.FirstName.Contains(normalized) || x.LastName.Contains(normalized));
        }
        return await students.OrderBy(x => x.LastName).ThenBy(x => x.FirstName)
            .Select(x => new StudentSummary(
                x.Id,
                x.LastName + " " + x.FirstName,
                x.Age,
                x.GroupId,
                x.Group != null ? x.Group.Name : string.Empty,
                x.Kiberons,
                x.Xp,
                (x.Xp / 100) + 1,
                x.Birthday,
                x.LastName,
                x.FirstName))
            .ToListAsync(ct);
    }

    public async Task<Student> CreateStudentAsync(StudentDraft draft, CancellationToken ct = default)
    {
        ValidateStudent(draft);
        await using var db = new ClassroomDbContext(options);
        if (!await db.Groups.AnyAsync(x => x.Id == draft.GroupId, ct)) throw new KeyNotFoundException("Группа не найдена.");
        var student = ToStudent(draft);
        if (draft.Kiberons is >= 0) student.Kiberons = draft.Kiberons.Value;
        if (draft.Xp is >= 0) student.Xp = draft.Xp.Value;
        db.Students.Add(student);
        await db.SaveChangesAsync(ct);
        return student;
    }

    public async Task<Student?> UpdateStudentAsync(Guid id, StudentDraft draft, CancellationToken ct = default)
    {
        ValidateStudent(draft);
        await using var db = new ClassroomDbContext(options);
        var student = await db.Students.FindAsync([id], ct);
        if (student is null) return null;
        if (!await db.Groups.AnyAsync(x => x.Id == draft.GroupId, ct)) throw new KeyNotFoundException("Группа не найдена.");
        student.LastName = draft.LastName.Trim();
        student.FirstName = draft.FirstName.Trim();
        student.Age = draft.Age;
        student.Birthday = draft.Birthday;
        student.GroupId = draft.GroupId;
        student.Comment = Trim(draft.Comment, 2000);
        student.PortfolioUrl = Trim(draft.PortfolioUrl, 500);
        student.CrmId = Trim(draft.CrmId, 120);
        if (draft.Xp is >= 0) student.Xp = draft.Xp.Value;
        if (draft.Kiberons is int targetKiberons)
        {
            var delta = targetKiberons - student.Kiberons;
            if (delta != 0)
                AddKiberons(db, student, delta, KiberonTransactionKind.Adjustment, "Редактирование карточки тьютором", null);
        }
        await db.SaveChangesAsync(ct);
        return student;
    }

    public async Task<bool> DeleteStudentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var student = await db.Students.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (student is null) return false;

        var orders = await db.StoreOrders.Where(x => x.StudentId == id).ToListAsync(ct);
        db.StoreOrders.RemoveRange(orders);
        db.Grades.RemoveRange(await db.Grades.Where(x => x.StudentId == id).ToListAsync(ct));
        db.ClassroomSessions.RemoveRange(await db.ClassroomSessions.Where(x => x.StudentId == id).ToListAsync(ct));
        db.StudentAchievements.RemoveRange(await db.StudentAchievements.Where(x => x.StudentId == id).ToListAsync(ct));
        db.KiberonTransactions.RemoveRange(await db.KiberonTransactions.Where(x => x.StudentId == id).ToListAsync(ct));
        var quizAnswers = await db.QuizAnswers.Where(x => x.StudentId == id).ToListAsync(ct);
        db.QuizAnswers.RemoveRange(quizAnswers);
        db.Students.Remove(student);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<StudentProfile?> GetStudentAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var student = await db.Students.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (student is null) return null;
        var grades = (await db.Grades.AsNoTracking().Where(x => x.StudentId == id).ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).ToList();
        var achievements = (await db.StudentAchievements.AsNoTracking().Where(x => x.StudentId == id).ToListAsync(ct)).OrderByDescending(x => x.AwardedAt).ToList();
        var history = (await db.KiberonTransactions.AsNoTracking().Where(x => x.StudentId == id).ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).Take(200).ToList();
        var orders = (await db.StoreOrders.AsNoTracking().Where(x => x.StudentId == id).ToListAsync(ct)).OrderByDescending(x => x.CreatedAt).ToList();
        return new StudentProfile(student, grades, achievements, history, orders);
    }

    public async Task<GroupStatistics?> GetGroupStatisticsAsync(Guid groupId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var group = await db.Groups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == groupId, ct);
        if (group is null) return null;
        var studentIds = await db.Students.AsNoTracking().Where(x => x.GroupId == groupId).Select(x => x.Id).ToListAsync(ct);
        var grades = await db.Grades.AsNoTracking().Where(x => studentIds.Contains(x.StudentId)).Select(x => x.Value).ToListAsync(ct);
        var totalXp = await db.Students.AsNoTracking().Where(x => x.GroupId == groupId).SumAsync(x => x.Xp, ct);
        var totalKiberons = await db.Students.AsNoTracking().Where(x => x.GroupId == groupId).SumAsync(x => x.Kiberons, ct);
        var sessions = await db.ClassroomSessions.AsNoTracking().CountAsync(x => studentIds.Contains(x.StudentId), ct);
        var achievements = await db.StudentAchievements.AsNoTracking().CountAsync(x => studentIds.Contains(x.StudentId), ct);
        return new GroupStatistics(group.Id, group.Name, studentIds.Count, grades.Count == 0 ? 0 : Math.Round(grades.Average(), 2), totalXp, totalKiberons, sessions, achievements);
    }

    public async Task<StudentStatistics?> GetStudentStatisticsAsync(Guid studentId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var student = await db.Students.AsNoTracking().Include(x => x.Group).SingleOrDefaultAsync(x => x.Id == studentId, ct);
        if (student is null) return null;
        var grades = await db.Grades.AsNoTracking().Where(x => x.StudentId == studentId).Select(x => x.Value).ToListAsync(ct);
        var sessions = await db.ClassroomSessions.AsNoTracking().CountAsync(x => x.StudentId == studentId, ct);
        var achievements = await db.StudentAchievements.AsNoTracking().CountAsync(x => x.StudentId == studentId, ct);
        var purchases = await db.StoreOrders.AsNoTracking().CountAsync(x => x.StudentId == studentId, ct);
        return new StudentStatistics(student.Id, student.DisplayName, student.Group?.Name ?? string.Empty, student.Level, student.Xp, student.Kiberons,
            grades.Count == 0 ? 0 : Math.Round(grades.Average(), 2), grades.Count, sessions, achievements, purchases);
    }

    public async Task<Grade> AddGradeAsync(GradeDraft draft, CancellationToken ct = default)
    {
        if (draft.Value is < 1 or > 5) throw new LessonValidationException(["Оценка должна быть от 1 до 5."]);
        await using var db = new ClassroomDbContext(options);
        if (!await db.Students.AnyAsync(x => x.Id == draft.StudentId, ct)) throw new KeyNotFoundException("Ученик не найден.");
        var grade = new Grade { StudentId = draft.StudentId, ClassroomSessionId = draft.ClassroomSessionId, Value = draft.Value, Note = Trim(draft.Note, 500) };
        db.Grades.Add(grade);
        await db.SaveChangesAsync(ct);
        return grade;
    }

    public async Task<ClassroomSession> CheckInAsync(Guid studentId, string topic, string pcNumber, string clientId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        if (!await db.Students.AnyAsync(x => x.Id == studentId, ct)) throw new KeyNotFoundException("Ученик не найден.");
        var session = new ClassroomSession { StudentId = studentId, Topic = Trim(topic, 300), PcNumber = Trim(pcNumber, 40), ClientId = Trim(clientId, 120) };
        db.ClassroomSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<IReadOnlyList<Achievement>> ListAchievementsAsync(CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        return await db.Achievements.AsNoTracking().Where(x => x.IsActive && !x.Code.StartsWith("sys_")).OrderBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<Achievement> CreateAchievementAsync(AchievementDraft draft, CancellationToken ct = default)
    {
        var code = Required(draft.Code, "Код достижения", 64).ToLowerInvariant();
        if (draft.XpReward < 0 || draft.KiberonReward < 0) throw new LessonValidationException(["Награда не может быть отрицательной."]);
        await using var db = new ClassroomDbContext(options);
        if (await db.Achievements.AnyAsync(x => x.Code == code, ct)) throw new InvalidOperationException("Код достижения уже используется.");
        var achievement = new Achievement { Code = code, Name = Required(draft.Name, "Название достижения", 120), Description = Trim(draft.Description, 1000), Icon = Trim(draft.Icon, 64), XpReward = draft.XpReward, KiberonReward = draft.KiberonReward };
        db.Achievements.Add(achievement);
        await db.SaveChangesAsync(ct);
        return achievement;
    }

    public async Task<StudentAchievement> AwardAchievementAsync(AwardAchievementRequest request, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var student = await db.Students.SingleOrDefaultAsync(x => x.Id == request.StudentId, ct) ?? throw new KeyNotFoundException("Ученик не найден.");
        var achievement = await db.Achievements.SingleOrDefaultAsync(x => x.Id == request.AchievementId && x.IsActive, ct) ?? throw new KeyNotFoundException("Достижение не найдено.");
        var existing = await db.StudentAchievements.SingleOrDefaultAsync(x => x.StudentId == request.StudentId && x.AchievementId == request.AchievementId, ct);
        if (existing is not null) return existing;
        var award = new StudentAchievement { StudentId = student.Id, AchievementId = achievement.Id, Note = Trim(request.Note, 500) };
        student.Xp = checked(student.Xp + achievement.XpReward);
        if (achievement.KiberonReward > 0) AddKiberons(db, student, achievement.KiberonReward, KiberonTransactionKind.Award, $"Достижение: {achievement.Name}", award.Id);
        db.StudentAchievements.Add(award);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return award;
    }

    public async Task<StudentAchievement> TriggerSystemAchievementAsync(Guid studentId, string eventName, CancellationToken ct = default)
    {
        var definition = eventName switch
        {
            "games_addict" => ("sys_game_addict", "Игроман", "Фокус-режим закрыл три игровых окна.", "gamepad", 25),
            "watchdog_survivor" => ("sys_watchdog_survivor", "Неудержимый", "Student был восстановлен watchdog.", "shield", 25),
            _ => throw new LessonValidationException(["Неизвестное системное событие."])
        };
        Guid achievementId;
        await using (var db = new ClassroomDbContext(options))
        {
            if (!await db.Students.AnyAsync(x => x.Id == studentId, ct)) throw new KeyNotFoundException("Ученик не найден.");
            var achievement = await db.Achievements.SingleOrDefaultAsync(x => x.Code == definition.Item1, ct);
            if (achievement is null)
            {
                achievement = new Achievement { Code = definition.Item1, Name = definition.Item2, Description = definition.Item3, Icon = definition.Item4, XpReward = definition.Item5 };
                db.Achievements.Add(achievement);
                await db.SaveChangesAsync(ct);
            }
            achievementId = achievement.Id;
        }
        return await AwardAchievementAsync(new AwardAchievementRequest(studentId, achievementId, definition.Item3), ct);
    }

    public async Task<KiberonTransaction> AdjustKiberonsAsync(AdjustKiberonsRequest request, CancellationToken ct = default)
    {
        if (request.Amount == 0) throw new LessonValidationException(["Изменение баланса не может быть нулевым."]);
        var reason = Required(request.Reason, "Причина", 500);
        await using var db = new ClassroomDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var student = await db.Students.SingleOrDefaultAsync(x => x.Id == request.StudentId, ct) ?? throw new KeyNotFoundException("Ученик не найден.");
        var entry = AddKiberons(db, student, request.Amount, KiberonTransactionKind.Adjustment, reason, null);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return entry;
    }

    public async Task<IReadOnlyList<StoreItem>> ListStoreItemsAsync(bool includeOutOfStock = false, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var query = db.StoreItems.AsNoTracking().Where(x => x.IsActive && !x.IsSecret);
        if (!includeOutOfStock) query = query.Where(x => x.Stock > 0);
        return await query.OrderBy(x => x.Price).ThenBy(x => x.Name).ToListAsync(ct);
    }

    public async Task<StoreItem?> GetSecretItemAsync(string code, CancellationToken ct = default)
    {
        var normalized = Trim(code, 64).ToLowerInvariant();
        if (normalized != "sys_mr_67") return null;
        await using var db = new ClassroomDbContext(options);
        return await db.StoreItems.AsNoTracking().SingleOrDefaultAsync(x => x.Sku == normalized && x.IsSecret && x.IsActive, ct);
    }

    public async Task<StoreItem> CreateStoreItemAsync(StoreItemDraft draft, CancellationToken ct = default)
    {
        var sku = Required(draft.Sku, "Артикул", 64).ToLowerInvariant();
        if (draft.Price < 0 || draft.Stock < 0) throw new LessonValidationException(["Цена и остаток не могут быть отрицательными."]);
        await using var db = new ClassroomDbContext(options);
        if (await db.StoreItems.AnyAsync(x => x.Sku == sku, ct)) throw new InvalidOperationException("Артикул уже используется.");
        var item = new StoreItem { Sku = sku, Name = Required(draft.Name, "Название товара", 120), Description = Trim(draft.Description, 1000), Price = draft.Price, Stock = draft.Stock, IsSecret = draft.IsSecret };
        db.StoreItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task<PurchaseResult> PurchaseAsync(PurchaseRequest request, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var student = await db.Students.SingleOrDefaultAsync(x => x.Id == request.StudentId, ct) ?? throw new KeyNotFoundException("Ученик не найден.");
        var item = await db.StoreItems.SingleOrDefaultAsync(x => x.Id == request.StoreItemId && x.IsActive, ct) ?? throw new KeyNotFoundException("Товар не найден.");
        if (item.Stock <= 0) throw new InvalidOperationException("Товар закончился.");
        if (student.Kiberons < item.Price) throw new InvalidOperationException("Недостаточно киберонов.");
        item.Stock--;
        var order = new StoreOrder { StudentId = student.Id, StoreItemId = item.Id, PricePaid = item.Price };
        AddKiberons(db, student, -item.Price, KiberonTransactionKind.Purchase, $"Покупка: {item.Name}", order.Id);
        db.StoreOrders.Add(order);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new PurchaseResult(order, student.Kiberons, item.Stock);
    }

    public async Task<StoreOrder?> UpdateOrderStatusAsync(Guid orderId, UpdateOrderStatusRequest request, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var order = await db.StoreOrders.SingleOrDefaultAsync(x => x.Id == orderId, ct);
        if (order is null) return null;
        if (order.Status is StoreOrderStatus.Rejected or StoreOrderStatus.Cancelled or StoreOrderStatus.Issued)
            throw new InvalidOperationException("Завершённый заказ нельзя изменить.");
        if (request.Status is StoreOrderStatus.Rejected or StoreOrderStatus.Cancelled)
        {
            var student = await db.Students.SingleAsync(x => x.Id == order.StudentId, ct);
            var item = await db.StoreItems.SingleAsync(x => x.Id == order.StoreItemId, ct);
            item.Stock++;
            AddKiberons(db, student, order.PricePaid, KiberonTransactionKind.Refund, "Возврат за отменённый заказ", order.Id);
        }
        order.Status = request.Status;
        order.Note = Trim(request.Note, 500);
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return order;
    }

    private static KiberonTransaction AddKiberons(ClassroomDbContext db, Student student, int amount, KiberonTransactionKind kind, string reason, Guid? referenceId)
    {
        var next = checked(student.Kiberons + amount);
        if (next < 0) throw new InvalidOperationException("Баланс киберонов не может быть отрицательным.");
        student.Kiberons = next;
        var entry = new KiberonTransaction { StudentId = student.Id, Amount = amount, BalanceAfter = next, Kind = kind, Reason = reason, ReferenceId = referenceId };
        db.KiberonTransactions.Add(entry);
        return entry;
    }

    private static Student ToStudent(StudentDraft draft) => new()
    {
        LastName = draft.LastName.Trim(),
        FirstName = draft.FirstName.Trim(),
        Age = draft.Age,
        Birthday = draft.Birthday,
        GroupId = draft.GroupId,
        Comment = Trim(draft.Comment, 2000),
        PortfolioUrl = Trim(draft.PortfolioUrl, 500),
        CrmId = Trim(draft.CrmId, 120)
    };

    private static void ValidateStudent(StudentDraft draft)
    {
        Required(draft.LastName, "Фамилия", 120);
        Required(draft.FirstName, "Имя", 120);
        if (draft.Age is < 5 or > 100) throw new LessonValidationException(["Возраст должен быть от 5 до 100 лет."]);
    }

    private static string Required(string value, string field, int max)
    {
        var result = Trim(value, max);
        if (string.IsNullOrWhiteSpace(result)) throw new LessonValidationException([$"{field}: обязательное поле."]);
        return result;
    }

    private static string Trim(string? value, int max)
    {
        var result = value?.Trim() ?? string.Empty;
        return result.Length <= max ? result : result[..max];
    }
}
