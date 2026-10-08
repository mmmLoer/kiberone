using System.Text.Json;
using Kiberone.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kiberone.Infrastructure;

public static class ClassroomHubApi
{
    public static void Map(WebApplication app, ClassroomHubStore store, StudentMailboxStore? mailboxes = null)
    {
        StudentMailApi.Map(app, store, mailboxes);
        app.MapGet("/api/health", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/locations", () => Results.Json(store.List()));
        app.MapGet("/api/locations/{location}/roster", (string location, HttpRequest request) =>
        {
            var password = request.Headers["X-Location-Password"].ToString();
            if (string.IsNullOrWhiteSpace(password))
                password = request.Query["password"].ToString();
            if (string.IsNullOrWhiteSpace(password))
                return Results.Json(new { error = "Нужен пароль локации." }, statusCode: 401);
            try
            {
                return Results.Json(store.GetAuthorized(location, password));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new { error = "Неверный пароль локации." }, statusCode: 403);
            }
        });
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
        app.MapPost("/api/vpn/reservations/lookup", (VpnPeerReservationLookup request) =>
        {
            try { return Results.Json(store.GetVpnReservation(request.Location, request.Password, request.ClientId)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
        app.MapPost("/api/vpn/reservations", (VpnPeerReservationRequest request) =>
        {
            try { return Results.Ok(store.ReserveVpnPeer(request)); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (VpnPeerConflictException error) { return Results.Conflict(new { error = error.Message }); }
            catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
        });
        app.MapGet("/api/update/{application}", (string application, HttpRequest request) =>
        {
            var channel = request.Query["channel"].ToString();
            if (string.IsNullOrEmpty(channel)) channel = "release";
            AppUpdateManifest? manifest;
            try
            {
                manifest = application == "student" && channel == "test"
                    ? store.GetStudentUpdate(testChannel: true) : store.GetAppUpdate(application, channel);
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            return manifest is null
                ? Results.NotFound()
                : Results.Json(manifest, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
        });
        app.MapGet("/api/update/{application}/file", (string application, HttpRequest request) =>
        {
            var channel = request.Query["channel"].ToString();
            if (string.IsNullOrEmpty(channel)) channel = "release";
            var version = request.Query.ContainsKey("version") ? request.Query["version"].ToString() : null;
            var sha256 = request.Query.ContainsKey("sha256") ? request.Query["sha256"].ToString() : null;
            Stream? stream;
            try
            {
                stream = application == "student" && channel == "test"
                    ? store.OpenStudentUpdate(testChannel: true, version, sha256) : store.OpenAppUpdate(application, channel, version, sha256);
            }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            return stream is not null
                ? Results.File(stream, "application/octet-stream", application == "student" ? "KIBERoneStudent.exe" : "KIBERoneTutor.exe", enableRangeProcessing: true)
                : version is not null || sha256 is not null
                    ? Results.Conflict(new { error = "Запрошенное обновление больше не доступно. Получите новый манифест." })
                    : Results.NotFound();
        });
        app.MapGet("/api/update/installers", () => Results.Json(store.ListInstallerZips()));
    }
}

public sealed record VpnPeerDownloadRequest(string Location, string Password);

public sealed record VpnPeerUploadRequest(string Location, string Password, IReadOnlyList<VpnPeerFile> Files);

public sealed record VpnPeerReservationLookup(string Location, string Password, string ClientId);
public sealed record VpnPeerReservationRequest(string Location, string Password, string ClientId,
    string RegionId, string Slot, string Fingerprint);
public sealed record VpnPeerReservation(string Location, string ClientId, string RegionId, string Slot, string Fingerprint);
public sealed class VpnPeerConflictException(string message) : Exception(message);
