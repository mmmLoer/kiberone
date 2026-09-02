using System.Text.Json;
using Kiberone.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kiberone.Infrastructure;

public static class ClassroomHubApi
{
    public static void Map(WebApplication app, ClassroomHubStore store)
    {
        app.MapGet("/api/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/locations", () => Results.Json(store.List()));
        app.MapGet("/api/locations/{location}/roster", (string location) => Results.Json(store.Get(location)));
        app.MapPut("/api/locations/{location}/roster", async (string location, HttpRequest request, CancellationToken ct) =>
        {
            var body = await JsonSerializer.DeserializeAsync<LocationRosterUploadRequest>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            if (body is null || body.Snapshot is null)
                return Results.BadRequest(new { error = "Нужны пароль и снимок локации." });
            try
            {
                var saved = store.Put(location, body.Password, body.Snapshot);
                return Results.Ok(saved);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "Неверный пароль локации." }, statusCode: 403);
            }
            catch (InvalidOperationException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
        });
    }
}
