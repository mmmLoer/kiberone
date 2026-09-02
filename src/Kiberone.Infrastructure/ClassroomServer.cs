using System.Security.Cryptography;
using System.Text;
using Kiberone.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace Kiberone.Infrastructure;

public sealed record ClassroomServerOptions(string SyncToken, int Port = 8765)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SyncToken) || SyncToken.Length < 24)
            throw new InvalidOperationException("SyncToken должен содержать не менее 24 символов.");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("Некорректный TCP-порт.");
    }
}

public sealed class ClassroomLiveState
{
    public string? PreferredGroupName { get; set; }
    public string? LocationName { get; set; }
    public bool ShowAllLocations { get; set; }
    public int SyncSeconds { get; set; } = 300;
}

public sealed class ClassroomServer(
    ClassroomServerOptions serverOptions,
    TypingLessonService lessons,
    ClassroomService classroom,
    FileSyncService fileSync,
    AssetDistributionService assets,
    QuizService quizzes,
    AuditService audit,
    ClientRegistry clients,
    ReliableCommandQueue commands) : IAsyncDisposable
{
    private WebApplication? app;
    private readonly StudentCommandSockets commandSockets = new();
    private Action<ClassroomCommand, IReadOnlyList<string>>? queuedHandler;
    public ClassroomLiveState LiveState { get; set; } = new();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (app is not null) return;
        serverOptions.Validate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{serverOptions.Port}");
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower);
        app = builder.Build();
        app.UseExceptionHandler(handler => handler.Run(WriteErrorAsync));
        app.Use(async (context, next) =>
        {
            if (!ShouldAudit(context.Request)) { await next(context); return; }
            var timer = Stopwatch.StartNew();
            Exception? failure = null;
            try { await next(context); }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                timer.Stop();
                var actor = IsTutor(context) ? "tutor" : context.Request.Headers["X-Client-Id"].ToString();
                if (string.IsNullOrWhiteSpace(actor)) actor = context.Request.Query["client_id"].ToString();
                try
                {
                    await audit.WriteAsync(AuditCategory(context.Request.Path), context.Request.Method,
                        actor, context.Request.Path + context.Request.QueryString, failure?.Message ?? string.Empty,
                        failure is null ? context.Response.StatusCode : 500, timer.ElapsedMilliseconds, CancellationToken.None);
                }
                catch { }
            }
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/health")
            {
                await next(context);
                return;
            }
            var supplied = context.Request.Headers["X-Sync-Token"].ToString();
            if (string.IsNullOrEmpty(supplied) && context.Request.Path == "/ws")
                supplied = context.Request.Query["token"].ToString();
            if (!TokensMatch(supplied, serverOptions.SyncToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }, cancellationToken);
                return;
            }
            await next(context);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        queuedHandler = (command, targets) => commandSockets.Push(targets, command);
        commands.CommandQueued += queuedHandler;
        MapRoutes(app);
        await app.StartAsync(cancellationToken);
    }

    private void MapRoutes(WebApplication application)
    {
        application.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            service = "KIBERone Classroom",
            version = BuildInfo.Version,
            preferred_group = LiveState.PreferredGroupName,
            command_push = true
        }));
        application.MapPost("/heartbeat", async ([FromBody] HeartbeatRequest request, CancellationToken ct) =>
        {
            clients.Heartbeat(request);
            fileSync.BindClient(request.ClientId, request.StudentId);
            var home = request.StudentId is Guid studentId
                ? await fileSync.ResolveStudentHomeAsync(studentId, ct)
                : null;
            return Results.Ok(new HeartbeatResponse(
                true,
                DateTimeOffset.UtcNow,
                3,
                Math.Clamp(LiveState.SyncSeconds, 5, 3600),
                assets.GetUpdateFor(request.AppVersion),
                LiveState.PreferredGroupName,
                home?.Module,
                home?.DisplayName));
        });
        application.MapGet("/clients", (HttpContext context) =>
            IsTutor(context) ? Results.Ok(clients.GetAll()) : Results.Unauthorized());
        application.MapGet("/commands", (string client_id) => Results.Ok(commands.GetPending(client_id)));
        application.MapPost("/command", (HttpContext context, [FromBody] EnqueueCommandRequest request) =>
            IsTutor(context) ? Results.Ok(commands.Enqueue(request)) : Results.Unauthorized());
        application.MapPost("/commands/{id:guid}/ack", (Guid id, string client_id, [FromBody] CommandAcknowledgement acknowledgement) =>
        {
            if (id != acknowledgement.CommandId)
                throw new LessonValidationException(["ID команды в маршруте и теле не совпадают."]);
            return Results.Ok(commands.Acknowledge(client_id, acknowledgement));
        });
        application.MapGet("/command-receipts", (HttpContext context, int? limit) =>
            IsTutor(context) ? Results.Ok(commands.GetReceipts(limit ?? 200)) : Results.Unauthorized());
        application.Map("/ws", HandleCommandSocketAsync);
        application.MapGet("/typing/lessons", (CancellationToken ct) => lessons.ListLessonsAsync(ct));
        application.MapGet("/typing/lessons/{id:guid}", async (Guid id, CancellationToken ct) =>
            await lessons.GetLessonAsync(id, ct) is { } lesson ? Results.Ok(lesson) : Results.NotFound());
        application.MapPost("/typing/lessons", async ([FromBody] CreateLessonRequest request, CancellationToken ct) =>
        {
            var lesson = await lessons.CreateLessonAsync(request, ct);
            return Results.Created($"/typing/lessons/{lesson.Id}", lesson);
        });
        application.MapPut("/typing/lessons/{id:guid}", async (Guid id, [FromBody] UpdateLessonRequest request, CancellationToken ct) =>
            await lessons.UpdateLessonAsync(id, request, ct) is { } lesson ? Results.Ok(lesson) : Results.NotFound());
        application.MapPost("/typing/sessions", async ([FromBody] StartTypingSessionRequest request, CancellationToken ct) =>
        {
            var session = await lessons.StartSessionAsync(request, ct);
            return Results.Created($"/typing/sessions/{session.Id}", new { session.Id });
        });
        application.MapGet("/typing/sessions/{id:guid}", async (Guid id, CancellationToken ct) =>
            await lessons.GetSnapshotAsync(id, ct) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
        application.MapPost("/typing/sessions/{id:guid}/telemetry", async (Guid id, [FromBody] TelemetryUpdateRequest request, CancellationToken ct) =>
            await lessons.RecordTelemetryAsync(id, request, ct) is { } snapshot ? Results.Ok(snapshot) : Results.NotFound());
        application.MapPost("/typing/sessions/{id:guid}/finish", async (Guid id, CancellationToken ct) =>
            await lessons.FinishSessionAsync(id, ct) is { } result
                ? Results.Ok(new { result.Snapshot, result.Winners })
                : Results.NotFound());
        application.MapGet("/groups", (CancellationToken ct) => classroom.ListGroupsAsync(StudentLocationFilter(), ct));
        application.MapPost("/groups", async (HttpContext context, [FromBody] GroupDraft draft, CancellationToken ct) =>
            IsTutor(context) ? Results.Created("/groups", await classroom.CreateGroupAsync(draft, ct)) : Results.Unauthorized());
        application.MapPut("/groups/{id:guid}", async (HttpContext context, Guid id, [FromBody] GroupDraft draft, CancellationToken ct) =>
            !IsTutor(context) ? Results.Unauthorized() : await classroom.UpdateGroupAsync(id, draft, ct) is { } group ? Results.Ok(group) : Results.NotFound());
        application.MapDelete("/groups/{id:guid}", async (HttpContext context, Guid id, CancellationToken ct) =>
            !IsTutor(context) ? Results.Unauthorized() : await classroom.DeleteGroupAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        application.MapGet("/students", (Guid? group_id, string? query, CancellationToken ct) =>
            classroom.ListStudentsAsync(group_id, query, StudentLocationFilter(), ct));
        application.MapGet("/students/{id:guid}", async (Guid id, CancellationToken ct) =>
            await classroom.GetStudentAsync(id, ct) is { } student ? Results.Ok(student) : Results.NotFound());
        application.MapGet("/statistics/groups/{id:guid}", async (Guid id, CancellationToken ct) =>
            await classroom.GetGroupStatisticsAsync(id, ct) is { } stats ? Results.Ok(stats) : Results.NotFound());
        application.MapGet("/statistics/students/{id:guid}", async (Guid id, CancellationToken ct) =>
            await classroom.GetStudentStatisticsAsync(id, ct) is { } stats ? Results.Ok(stats) : Results.NotFound());
        application.MapPost("/students", async (HttpContext context, [FromBody] StudentDraft draft, CancellationToken ct) =>
            IsTutor(context) ? Results.Created("/students", await classroom.CreateStudentAsync(draft, ct)) : Results.Unauthorized());
        application.MapPut("/students/{id:guid}", async (HttpContext context, Guid id, [FromBody] StudentDraft draft, CancellationToken ct) =>
            !IsTutor(context) ? Results.Unauthorized() : await classroom.UpdateStudentAsync(id, draft, ct) is { } student ? Results.Ok(student) : Results.NotFound());
        application.MapDelete("/students/{id:guid}", async (HttpContext context, Guid id, CancellationToken ct) =>
            !IsTutor(context) ? Results.Unauthorized() : await classroom.DeleteStudentAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        application.MapPost("/grades", async (HttpContext context, [FromBody] GradeDraft draft, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await classroom.AddGradeAsync(draft, ct)) : Results.Unauthorized());
        application.MapPost("/check-in", ([FromBody] CheckInRequest request, CancellationToken ct) =>
            classroom.CheckInAsync(request.StudentId, request.Topic, request.PcNumber, request.ClientId, ct));
        application.MapGet("/achievements", (CancellationToken ct) => classroom.ListAchievementsAsync(ct));
        application.MapPost("/achievements", async (HttpContext context, [FromBody] AchievementDraft draft, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await classroom.CreateAchievementAsync(draft, ct)) : Results.Unauthorized());
        application.MapPost("/achievements/award", async (HttpContext context, [FromBody] AwardAchievementRequest request, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await classroom.AwardAchievementAsync(request, ct)) : Results.Unauthorized());
        application.MapPost("/kiberons/adjust", async (HttpContext context, [FromBody] AdjustKiberonsRequest request, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await classroom.AdjustKiberonsAsync(request, ct)) : Results.Unauthorized());
        application.MapGet("/store/items", (bool? include_out_of_stock, CancellationToken ct) => classroom.ListStoreItemsAsync(include_out_of_stock ?? false, ct));
        application.MapGet("/store/secret/{code}", async (string code, CancellationToken ct) =>
            await classroom.GetSecretItemAsync(code, ct) is { } item ? Results.Ok(item) : Results.NotFound());
        application.MapPost("/store/items", async (HttpContext context, [FromBody] StoreItemDraft draft, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await classroom.CreateStoreItemAsync(draft, ct)) : Results.Unauthorized());
        application.MapPost("/store/purchase", ([FromBody] PurchaseRequest request, CancellationToken ct) => classroom.PurchaseAsync(request, ct));
        application.MapPut("/store/orders/{id:guid}/status", async (HttpContext context, Guid id, [FromBody] UpdateOrderStatusRequest request, CancellationToken ct) =>
            !IsTutor(context) ? Results.Unauthorized() : await classroom.UpdateOrderStatusAsync(id, request, ct) is { } order ? Results.Ok(order) : Results.NotFound());
        application.MapPost("/sync/prepare", ([FromBody] SyncPrepareRequest request, CancellationToken ct) => fileSync.PrepareAsync(request, ct));
        application.MapGet("/sync/approval", async (string client_id, CancellationToken ct) =>
            await fileSync.GetApprovalAsync(client_id, ct) is { } approval ? Results.Ok(approval) : Results.NotFound());
        application.MapGet("/sync/approvals", async (HttpContext context, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await fileSync.ListPendingApprovalsAsync(ct)) : Results.Unauthorized());
        application.MapPost("/sync/approval/{id:guid}", async (HttpContext context, Guid id, [FromBody] SyncDecisionRequest request, CancellationToken ct) =>
            !IsTutor(context) ? Results.Unauthorized() : await fileSync.DecideAsync(id, string.IsNullOrWhiteSpace(request.Action) ? (request.Approved ? "update" : "restore") : request.Action, ct) is { } decision ? Results.Ok(decision) : Results.NotFound());
        application.MapPost("/sync/complete", async ([FromBody] SyncCompleteRequest request, CancellationToken ct) =>
        {
            await fileSync.CompleteAsync(request.ClientId, ct);
            return Results.Ok(new { ok = true });
        });
        application.MapPost("/upload", async (HttpContext context, CancellationToken ct) =>
        {
            var clientId = context.Request.Headers["X-Client-Id"].ToString();
            var path = context.Request.Headers["X-Relative-Path"].ToString();
            return Results.Ok(await fileSync.UploadAsync(clientId, path, context.Request.Body, ct));
        });
        application.MapPost("/delete", async ([FromBody] DeleteFileRequest request, CancellationToken ct) =>
        {
            await fileSync.DeleteAsync(request.ClientId, request.Path, ct);
            return Results.Ok(new { ok = true });
        });
        application.MapGet("/list", (string client_id, CancellationToken ct) => fileSync.ListFilesAsync(client_id, ct));
        application.MapGet("/download", async (string client_id, string path, CancellationToken ct) =>
            await fileSync.OpenDownloadAsync(client_id, path, ct) is { } stream
                ? Results.File(stream, "application/octet-stream", Path.GetFileName(path), enableRangeProcessing: true)
                : Results.NotFound());
        application.MapGet("/versions", async (HttpContext context, string client_id, string path, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await fileSync.ListVersionsAsync(client_id, path, ct)) : Results.Unauthorized());
        application.MapPost("/versions/restore", async (HttpContext context, [FromBody] RestoreVersionRequest request, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await fileSync.RestoreVersionAsync(request, ct)) : Results.Unauthorized());
        application.MapGet("/update/student", () => assets.GetStudentRelease() is { } release ? Results.Ok(release) : Results.NotFound());
        application.MapGet("/update/student/file", () => assets.OpenStudentUpdate() is { } stream
            ? Results.File(stream, "application/octet-stream", "KIBERoneStudent.exe", enableRangeProcessing: true)
            : Results.NotFound());
        application.MapGet("/starter-pack", () => assets.ListStarterPack());
        application.MapGet("/starter-pack/file", (string name) => assets.OpenStarterAsset(name) is { } asset
            ? Results.File(asset.Content, asset.ContentType, asset.FileName, enableRangeProcessing: true)
            : Results.NotFound());
        application.MapGet("/wallpaper", () => assets.OpenWallpaper() is { } wallpaper
            ? Results.File(wallpaper.Content, wallpaper.ContentType, wallpaper.FileName, enableRangeProcessing: true)
            : Results.NotFound());
        application.MapGet("/deploy/file", (string name) => assets.OpenDeployAsset(name) is { } stream
            ? Results.File(stream, "application/octet-stream", Path.GetFileName(name), enableRangeProcessing: true)
            : Results.NotFound());
        application.MapPost("/screen", async (HttpContext context, CancellationToken ct) =>
        {
            await assets.SaveScreenAsync(context.Request.Headers["X-Client-Id"].ToString(), context.Request.Body, ct);
            return Results.Ok(new { ok = true });
        });
        application.MapGet("/screen", (HttpContext context, string client_id) =>
            !IsTutor(context) ? Results.Unauthorized() : assets.OpenScreen(client_id) is { } stream
                ? Results.File(stream, "image/jpeg", enableRangeProcessing: true)
                : Results.NotFound());
        application.MapPost("/events/trigger", async ([FromBody] ClientEventRequest request, CancellationToken ct) =>
        {
            var studentId = clients.GetAll().FirstOrDefault(x => x.ClientId == request.ClientId)?.StudentId;
            if (studentId is null) return Results.Ok(new { awarded = false, reason = "К компьютеру не привязан ученик." });
            return Results.Ok(await classroom.TriggerSystemAchievementAsync(studentId.Value, request.Event, ct));
        });
        application.MapPost("/quiz/start", async (HttpContext context, [FromBody] StartQuizRequest request, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await quizzes.StartAsync(request, ct)) : Results.Unauthorized());
        application.MapPost("/quiz/answer", ([FromBody] SubmitQuizAnswerRequest request, CancellationToken ct) => quizzes.SubmitAsync(request, ct));
        application.MapGet("/quiz/{id:guid}/answers", async (HttpContext context, Guid id, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await quizzes.GetAnswersAsync(id, ct)) : Results.Unauthorized());
        application.MapGet("/audit", async (HttpContext context, string? category, string? search, int? limit, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await audit.ListAsync(new AuditQuery(category, search, limit ?? 300), ct)) : Results.Unauthorized());
    }

    private async Task HandleCommandSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "expected_websocket" });
            return;
        }

        var clientId = context.Request.Query["client_id"].ToString();
        if (string.IsNullOrWhiteSpace(clientId))
            clientId = context.Request.Headers["X-Client-Id"].ToString();
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 160)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "client_id_required" });
            return;
        }

        await commandSockets.AcceptAsync(context, clientId, commands, context.RequestAborted);
    }

    private static bool ShouldAudit(HttpRequest request)
    {
        if (request.Method is "GET" or "HEAD" or "OPTIONS") return false;
        var path = request.Path.Value ?? string.Empty;
        return path is not "/heartbeat" and not "/screen" && !path.StartsWith("/commands/", StringComparison.Ordinal);
    }

    private static string AuditCategory(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (value.StartsWith("/sync", StringComparison.Ordinal) || value is "/upload" or "/delete") return "Синхронизация";
        if (value.StartsWith("/store", StringComparison.Ordinal) || value.StartsWith("/kiberons", StringComparison.Ordinal)) return "Магазин";
        if (value.StartsWith("/typing", StringComparison.Ordinal)) return "Печать";
        if (value.StartsWith("/quiz", StringComparison.Ordinal)) return "Викторина";
        if (value.StartsWith("/command", StringComparison.Ordinal)) return "Команды";
        if (value.StartsWith("/student", StringComparison.Ordinal) || value.StartsWith("/group", StringComparison.Ordinal) || value.StartsWith("/achievement", StringComparison.Ordinal)) return "Ученики";
        return "Система";
    }

    private string? StudentLocationFilter() =>
        LiveState.ShowAllLocations ? null : LiveState.LocationName;

    private static bool IsTutor(HttpContext context) =>
        context.Request.Headers["X-Tutor"].ToString().Equals("1", StringComparison.Ordinal);

    private static bool TokensMatch(string supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static async Task WriteErrorAsync(HttpContext context)
    {
        var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        context.Response.StatusCode = error switch
        {
            LessonValidationException => StatusCodes.Status400BadRequest,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            InvalidOperationException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };
        var details = error is LessonValidationException validation ? validation.Errors : null;
        await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "server_error", details });
    }

    public async ValueTask DisposeAsync()
    {
        if (queuedHandler is not null)
        {
            commands.CommandQueued -= queuedHandler;
            queuedHandler = null;
        }
        await commandSockets.DisposeAsync();
        if (app is null) return;
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await app.StopAsync(stopTimeout.Token);
        }
        catch (OperationCanceledException) { }
        await app.DisposeAsync();
        app = null;
    }
}
