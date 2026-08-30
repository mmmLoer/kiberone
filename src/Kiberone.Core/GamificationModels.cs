namespace Kiberone.Core;

public enum KiberonTransactionKind
{
    Award,
    Purchase,
    Refund,
    Adjustment
}

public enum StoreOrderStatus
{
    Pending,
    Approved,
    Issued,
    Rejected,
    Cancelled
}

public sealed class Achievement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = "star";
    public int XpReward { get; set; }
    public int KiberonReward { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StudentAchievement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public Guid AchievementId { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset AwardedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class KiberonTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public int Amount { get; set; }
    public int BalanceAfter { get; set; }
    public KiberonTransactionKind Kind { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? ReferenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StoreItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public int Price { get; set; }
    public int Stock { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsSecret { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StoreOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public Guid StoreItemId { get; set; }
    public int PricePaid { get; set; }
    public StoreOrderStatus Status { get; set; } = StoreOrderStatus.Pending;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record GroupDraft(string Name, string Module, string Topics);
public sealed record StudentDraft(
    string LastName,
    string FirstName,
    int? Age,
    Guid GroupId,
    string Comment,
    string PortfolioUrl,
    string CrmId,
    DateOnly? Birthday = null,
    int? Kiberons = null,
    int? Xp = null);
public sealed record GradeDraft(Guid StudentId, Guid? ClassroomSessionId, int Value, string Note);
public sealed record AchievementDraft(string Code, string Name, string Description, string Icon, int XpReward, int KiberonReward);
public sealed record AwardAchievementRequest(Guid StudentId, Guid AchievementId, string Note);
public sealed record AdjustKiberonsRequest(Guid StudentId, int Amount, string Reason);
public sealed record StoreItemDraft(string Sku, string Name, string Description, int Price, int Stock, bool IsSecret);
public sealed record PurchaseRequest(Guid StudentId, Guid StoreItemId);
public sealed record UpdateOrderStatusRequest(StoreOrderStatus Status, string Note);
public sealed record CheckInRequest(Guid StudentId, string Topic, string PcNumber, string ClientId);
public sealed record StudentSummary(
    Guid Id,
    string DisplayName,
    int? Age,
    Guid GroupId,
    string GroupName,
    int Kiberons,
    int Xp,
    int Level,
    DateOnly? Birthday = null,
    string LastName = "",
    string FirstName = "");
public sealed record StudentProfile(Student Student, IReadOnlyList<Grade> Grades, IReadOnlyList<StudentAchievement> Achievements, IReadOnlyList<KiberonTransaction> KiberonHistory, IReadOnlyList<StoreOrder> Orders);
public sealed record PurchaseResult(StoreOrder Order, int BalanceAfter, int StockAfter);
public sealed record GroupStatistics(Guid GroupId, string GroupName, int StudentCount, double AverageGrade, int TotalXp, int TotalKiberons, int SessionCount, int AchievementCount);
public sealed record StudentStatistics(Guid StudentId, string DisplayName, string GroupName, int Level, int Xp, int Kiberons, double AverageGrade, int GradeCount, int SessionCount, int AchievementCount, int PurchaseCount);
