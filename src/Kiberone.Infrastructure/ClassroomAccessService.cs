using System.Text.Json;
using Kiberone.Core;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Infrastructure;

public sealed partial class ClassroomService
{
    public async Task<ClassroomAccessPolicy> GetStudentAccessPolicyAsync(Guid studentId, CancellationToken ct = default)
    {
        await using var db = new ClassroomDbContext(options);
        var json = await db.Students.Where(x => x.Id == studentId).Select(x => x.Group!.AccessPolicyJson).FirstOrDefaultAsync(ct);
        return ParseAccessPolicy(json);
    }

    public static ClassroomAccessPolicy ParseAccessPolicy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ClassroomAccessPolicy.Empty;
        try
        {
            var policy = JsonSerializer.Deserialize<ClassroomAccessPolicy>(json);
            return policy is null || policy.AllowedApps is null || policy.BlockedApps is null || policy.AllowedSites is null || policy.BlockedSites is null
                ? ClassroomAccessPolicy.Empty : policy;
        }
        catch (JsonException) { return ClassroomAccessPolicy.Empty; }
    }

    public async Task<string> SaveAccessPolicyAsync(Guid groupId, ClassroomAccessPolicy policy, CancellationToken ct = default)
    {
        if (policy.AllowedApps.Count + policy.BlockedApps.Count > 300 || policy.AllowedSites.Count + policy.BlockedSites.Count > 300)
            throw new ArgumentException("Слишком много правил.");
        if (policy.AllowedApps.Concat(policy.BlockedApps).Any(x => x.Length > 100 || x.Contains('/') || x.Contains('\\')))
            throw new ArgumentException("Укажите имя EXE, без пути.");
        var normalized = policy with
        {
            Revision = Guid.NewGuid().ToString("N"),
            AllowedApps = policy.AllowedApps.Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray(),
            BlockedApps = policy.BlockedApps.Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray(),
            AllowedSites = policy.AllowedSites.Select(SiteRule.Normalize).Distinct().ToArray(),
            BlockedSites = policy.BlockedSites.Select(SiteRule.Normalize).Distinct().ToArray()
        };
        await using var db = new ClassroomDbContext(options);
        var group = await db.Groups.FindAsync([groupId], ct) ?? throw new KeyNotFoundException("Группа не найдена.");
        group.AccessPolicyJson = JsonSerializer.Serialize(normalized);
        await db.SaveChangesAsync(ct);
        return group.AccessPolicyJson;
    }
}
