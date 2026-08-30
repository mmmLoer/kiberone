namespace Kiberone.Core;

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Category { get; set; }
    public required string Action { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record AuditQuery(string? Category, string? Search, int Limit = 300);
