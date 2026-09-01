namespace Kiberone.Core;

public sealed class ClassroomGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Module { get; set; } = string.Empty;
    public string Topics { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Student> Students { get; set; } = [];
    public List<GroupProgramModule> ProgramModules { get; set; } = [];
}

public sealed class GroupProgramModule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public ClassroomGroup? Group { get; set; }
    public required string Name { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int LessonCount { get; set; }
    public string Comment { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public sealed class Student
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string LastName { get; set; }
    public required string FirstName { get; set; }
    public int? Age { get; set; }
    public DateOnly? Birthday { get; set; }
    public Guid GroupId { get; set; }
    public ClassroomGroup? Group { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string PortfolioUrl { get; set; } = string.Empty;
    public string CrmId { get; set; } = string.Empty;
    public int Kiberons { get; set; }
    public int Xp { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string DisplayName => $"{LastName} {FirstName}";
    public int Level => (Xp / 100) + 1;
}

public sealed class Grade
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public Guid? ClassroomSessionId { get; set; }
    public int Value { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ClassroomSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string PcNumber { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
