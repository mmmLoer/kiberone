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
        app.MapGet("/api/vpn/regions", () => Results.Json(store.ListVpnRegions()));
        app.MapPost("/api/vpn/regions/{region}/peers", async (string region, HttpRequest request, CancellationToken ct) =>
        {
            var body = await JsonSerializer.DeserializeAsync<VpnPeerDownloadRequest>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Location) || string.IsNullOrWhiteSpace(body.Password))
                return Results.BadRequest(new { error = "Нужны локация и пароль." });
            try
            {
                return Results.Json(store.GetVpnPeers(region, body.Location, body.Password));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "Неверный пароль локации." }, statusCode: 403);
            }
        });
        app.MapPut("/api/vpn/regions/{region}/peers", async (string region, HttpRequest request, CancellationToken ct) =>
        {
            var body = await JsonSerializer.DeserializeAsync<VpnPeerUploadRequest>(request.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            if (body is null || string.IsNullOrWhiteSpace(body.Location) || string.IsNullOrWhiteSpace(body.Password) || body.Files is null)
                return Results.BadRequest(new { error = "Нужны пароль, локация и файлы конфигов." });
            try
            {
                return Results.Json(store.PutVpnPeers(region, body.Location, body.Password, body.Files));
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
        app.MapGet("/api/update/student", () => store.GetStudentUpdate() is { } manifest ? Results.Json(manifest) : Results.NotFound());
        app.MapGet("/api/update/student/file", () => store.OpenStudentUpdate() is { } stream
            ? Results.File(stream, "application/octet-stream", "KIBERoneStudent.exe", enableRangeProcessing: true)
            : Results.NotFound());
    }
}

public sealed record VpnPeerDownloadRequest(string Location, string Password);

public sealed record VpnPeerUploadRequest(string Location, string Password, IReadOnlyList<VpnPeerFile> Files);
