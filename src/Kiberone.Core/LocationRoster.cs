namespace Kiberone.Core;

public sealed record LocationRosterSnapshot(
    string Location,
    DateTimeOffset ExportedAt,
    IReadOnlyList<LocationGroupSnapshot> Groups,
    IReadOnlyList<LocationStudentSnapshot> Students);

public sealed record LocationGroupSnapshot(
    Guid Id,
    string Name,
    string Module,
    string Topics,
    string Location,
    IReadOnlyList<LocationModuleSnapshot> Modules);

public sealed record LocationModuleSnapshot(
    Guid Id,
    string Name,
    string Start,
    string End,
    int Lessons,
    string Comment,
    int SortOrder);

public sealed record LocationStudentSnapshot(
    Guid Id,
    string LastName,
    string FirstName,
    int? Age,
    DateOnly? Birthday,
    Guid GroupId,
    string Comment,
    string PortfolioUrl,
    string CrmId,
    int Kiberons,
    int Xp);

public sealed record HubLocationStatus(string Name, DateTimeOffset? UpdatedAt, int Groups, int Students);

public sealed record LocationRosterUploadRequest(string Password, LocationRosterSnapshot Snapshot);
