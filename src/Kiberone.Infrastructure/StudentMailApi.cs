using Kiberone.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kiberone.Infrastructure;

public static class StudentMailApi
{
    public static void Map(WebApplication app, ClassroomHubStore store, StudentMailboxStore? mailboxes)
    {
        app.MapGet("/api/mail/health", () => Results.Ok(new { ok = true, configured = mailboxes?.Enabled == true }));
        app.MapPost("/api/mail/accounts", (StudentMailProvisionRequest request, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (mailboxes is null || !mailboxes.Enabled) return Results.Json(new { error = "Почтовый сервис не настроен." }, statusCode: 503);
            try
            {
                var roster = store.GetAuthorized(request.Location, request.Password);
                var student = roster.Students.FirstOrDefault(x => x.Id == request.StudentId);
                if (student is null && request.Student is { } enrollment && request.Group is { } group)
                {
                    if (enrollment.Id != request.StudentId) return Results.BadRequest();
                    student = store.EnrollMailStudent(request.Location, request.Password, enrollment, group);
                }
                if (student is null) return Results.StatusCode(403);
                return Results.Ok(mailboxes.GetOrCreate(request.StudentId, student.LastName, student.FirstName));
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
            catch (InvalidOperationException) { return Results.Json(new { error = "Создание почты ещё не настроено." }, statusCode: 503); }
        });
        app.MapGet("/api/mail/inbox", async (HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (mailboxes is null || !mailboxes.Enabled) return Results.Json(new { error = "Почтовый сервис не настроен." }, statusCode: 503);
            if (!Guid.TryParse(context.Request.Headers["X-Mailbox-Id"], out var id)) return Results.Unauthorized();
            try { return Results.Ok(await mailboxes.ReadAsync(id, context.Request.Headers["X-Mailbox-Password"].ToString(), ct)); }
            catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        });
    }
}
