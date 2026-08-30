using Kiberone.Core;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Infrastructure;

public sealed class AuditService(DbContextOptions<ClassroomDbContext> options)
{
    public async Task WriteAsync(string category, string action, string actor, string target, string details, int statusCode, long durationMs, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        db.AuditEvents.Add(new AuditEvent
        {
            Category = Trim(category, 80), Action = Trim(action, 120), Actor = Trim(actor, 160), Target = Trim(target, 500),
            Details = Trim(details, 2000), StatusCode = statusCode, DurationMs = Math.Max(0, durationMs)
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(AuditQuery query, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var events = db.AuditEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(query.Category)) events = events.Where(x => x.Category == query.Category);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            events = events.Where(x => x.Action.Contains(search) || x.Actor.Contains(search) || x.Target.Contains(search) || x.Details.Contains(search));
        }
        var all = await events.ToListAsync(ct);
        return all.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(query.Limit, 1, 2000)).ToList();
    }

    private static string Trim(string? value, int max)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}
