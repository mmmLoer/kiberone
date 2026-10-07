namespace Kiberone.Core;

public sealed record StudentMailAccount(Guid StudentId, string Address, string Password, string ServerUrl = "https://nshub.pro/");
public sealed record StudentMailProvisionRequest(string Location, string Password, Guid StudentId, LocationStudentSnapshot? Student = null, LocationGroupSnapshot? Group = null);
public sealed record StudentMailMessage(string Id, string Sender, string Subject, string Body, DateTimeOffset ReceivedAt, bool IsNew, string? ConfirmationCode, IReadOnlyList<StudentMailLink>? Links = null, string? HtmlBody = null, DateTimeOffset? ExpiresAt = null);
public sealed record StudentMailLink(string Label, string Url);
public sealed record StudentMailService(string Name, string Url);
