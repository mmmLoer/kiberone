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

public sealed record ClassroomServerOptions(string SyncToken, string TutorToken, int Port = 8765)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SyncToken) || SyncToken.Length < 24)
            throw new InvalidOperationException("SyncToken должен содержать не менее 24 символов.");
        if (string.IsNullOrWhiteSpace(TutorToken) || TutorToken.Length < 24)
            throw new InvalidOperationException("TutorToken должен содержать не менее 24 символов.");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("Некорректный TCP-порт.");
    }
}

public sealed class ClassroomLiveState
{
    public string? PreferredGroupName { get; set; }
    public string? LocationName { get; set; }
    public bool ShowAllLocations { get; set; }
    public int SyncSeconds { get; set; } = 300;
    public Func<Guid, CancellationToken, Task<StudentMailAccount>>? MailAccountProvider { get; set; }
    public Func<Guid, IReadOnlyList<TypingPersonalBest>, CancellationToken, Task<IReadOnlyList<TypingPersonalBest>>>? TypingRecordsProvider { get; set; }
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
    private readonly DeviceCredentials deviceCredentials = new(Path.Combine(assets.DataRoot, "device-pins"));
    private readonly StudentCommandSockets commandSockets = new();
    private Action<ClassroomCommand, IReadOnlyList<string>>? queuedHandler;
    private CancellationTokenSource? presenceCts;
    private Task? presenceTask;
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
                var actor = IsTutor(context) ? "tutor" : NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
                if (string.IsNullOrWhiteSpace(actor)) actor = context.Request.Query["client_id"].ToString();
                try
                {
                    await audit.WriteAsync(
                        "Синхронизация",
                        AuditAction(context.Request),
                        actor,
                        AuditTarget(context.Request),
                        failure?.Message ?? string.Empty,
                        failure is null ? context.Response.StatusCode : 500,
                        timer.ElapsedMilliseconds,
                        CancellationToken.None);
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
            if (!IsTutor(context))
            {
                var id = context.Request.Headers["X-Client-Id"].ToString();
                var secret = context.Request.Headers["X-Client-Secret"].ToString();
                // Query credentials support WebSocket clients that cannot set handshake headers.
                // A supplied header always wins; an invalid header cannot fall back to a query secret.
                if (context.Request.Path == "/ws")
                {
                    if (!context.Request.Headers.ContainsKey("X-Client-Id"))
                        id = context.Request.Query["client_id"].ToString();
                    if (!context.Request.Headers.ContainsKey("X-Client-Secret"))
                        secret = context.Request.Query["client_secret"].ToString();
                    context.Request.Headers["X-Client-Id"] = id;
                }
                if (!deviceCredentials.AuthenticateOrEnroll(id, secret))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "device_credential_required_or_rejected" });
                    return;
                }
                if (context.Request.Query.TryGetValue("client_id", out var target)
                    && !string.Equals(id, target.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
            }
            await next(context);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        queuedHandler = (command, targets) => commandSockets.Push(targets, command);
        commands.CommandQueued += queuedHandler;
        MapRoutes(app);
        presenceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        presenceTask = RunPresenceAuditLoopAsync(presenceCts.Token);
        await app.StartAsync(cancellationToken);
        quizzes.EnableSequences();
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
        application.MapPost("/presence", (HttpContext context) =>
            clients.Touch(context.Request.Headers["X-Client-Id"].ToString())
                ? Results.Ok() : Results.NotFound());
        application.MapPost("/heartbeat", async (HttpContext context, [FromBody] HeartbeatRequest request, CancellationToken ct) =>
        {
            if (!OwnsClient(context, request.ClientId)) return Results.StatusCode(403);
            clients.Heartbeat(request);
            await WritePresenceAuditsAsync(ct);
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
                home?.DisplayName,
                home?.ModuleFolders,
                request.StudentId is Guid accessStudentId ? await classroom.GetStudentAccessPolicyAsync(accessStudentId, ct) : ClassroomAccessPolicy.Empty));
        });
        application.MapGet("/clients", (HttpContext context) =>
            IsTutor(context) ? Results.Ok(clients.GetAll()) : Results.Unauthorized());
        application.MapGet("/mail/account", async (HttpContext context, CancellationToken ct) =>
        {
            var clientId = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            var client = clients.GetAll().FirstOrDefault(x => string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
            if (client?.StudentId is not Guid id) return Results.Unauthorized();
            if (LiveState.MailAccountProvider is null) return Results.Json(new { error = "Почта ещё не настроена тьютором." }, statusCode: 503);
            try { return Results.Ok(await LiveState.MailAccountProvider(id, ct)); }
            catch (InvalidOperationException error) { return Results.Json(new { error = error.Message }, statusCode: 503); }
            catch (HttpRequestException) { return Results.Json(new { error = "Почтовый сервер недоступен или отклонил запрос. Проверьте адрес и пароль локации у тьютора." }, statusCode: 502); }
        });
        application.MapPost("/apps/inventory", (HttpContext context, [FromBody] ApplicationInventoryRequest request) =>
        {
            var id = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            if (string.IsNullOrWhiteSpace(id) || !string.Equals(id, request.ClientId, StringComparison.OrdinalIgnoreCase)) return Results.Unauthorized();
            clients.SetApplicationInventory(id, request.Applications);
            return Results.Ok();
        });
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
        application.MapPost("/typing/records", async (HttpContext context, SaveTypingRecordsRequest request, CancellationToken ct) =>
        {
            var client = clients.GetAll().FirstOrDefault(x => x.ClientId == NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString()));
            if (client?.StudentId is not Guid student) return Results.Unauthorized();
            if (LiveState.TypingRecordsProvider is null) return Results.StatusCode(503);
            return Results.Ok(await LiveState.TypingRecordsProvider(student, request.Records, ct));
        });
        application.MapGet("/typing/lessons", async (CancellationToken ct) =>
            Results.Ok(await lessons.ListCatalogAsync(ct)));
        application.MapGet("/typing/lessons/{id:guid}", async (Guid id, CancellationToken ct) =>
            await lessons.GetLessonAsync(id, ct) is { } lesson ? Results.Ok(lesson) : Results.NotFound());
        application.MapPost("/typing/attempts", async (HttpContext context, [FromBody] SubmitTypingAttemptRequest request, CancellationToken ct) =>
        {
            var clientId = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            var bound = clients.GetAll().FirstOrDefault(x => string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(clientId) || bound?.StudentId != request.StudentId)
                return Results.Json(new { error = "Ученик не привязан к этому ПК." }, statusCode: 403);
            return Results.Ok(await lessons.RecordAttemptAsync(request, ct));
        });
        application.MapPost("/typing/lessons", async (HttpContext context, [FromBody] CreateLessonRequest request, CancellationToken ct) =>
        {
            if (!IsTutor(context)) return Results.Unauthorized();
            var lesson = await lessons.CreateLessonAsync(request, ct);
            return Results.Created($"/typing/lessons/{lesson.Id}", lesson);
        });
        application.MapPut("/typing/lessons/{id:guid}", async (HttpContext context, Guid id, [FromBody] UpdateLessonRequest request, CancellationToken ct) =>
            !IsTutor(context)
                ? Results.Unauthorized()
                : await lessons.UpdateLessonAsync(id, request, ct) is { } lesson ? Results.Ok(lesson) : Results.NotFound());
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
        application.MapPost("/check-in", async (HttpContext context, [FromBody] CheckInRequest request, CancellationToken ct) =>
        {
            var headerClient = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            if (string.IsNullOrWhiteSpace(headerClient)
                || !string.Equals(headerClient, request.ClientId, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "client_id не совпадает." }, statusCode: 403);
            var bound = clients.GetAll().FirstOrDefault(c =>
                string.Equals(c.ClientId, headerClient, StringComparison.OrdinalIgnoreCase));
            if (bound?.StudentId is Guid studentId && studentId != request.StudentId)
                return Results.Json(new { error = "student_id не привязан к этому ПК." }, statusCode: 403);
            return Results.Ok(await classroom.CheckInAsync(request.StudentId, request.Topic, request.PcNumber, request.ClientId, ct));
        });
        application.MapPost("/kiberons/adjust", async (HttpContext context, [FromBody] AdjustKiberonsRequest request, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await classroom.AdjustKiberonsAsync(request, ct)) : Results.Unauthorized());
        application.MapGet("/store/items", (bool? include_out_of_stock, CancellationToken ct) => classroom.ListStoreItemsAsync(include_out_of_stock ?? false, ct));
        application.MapGet("/store/secret/{code}", async (string code, CancellationToken ct) =>
            await classroom.GetSecretItemAsync(code, ct) is { } item ? Results.Ok(item) : Results.NotFound());
        application.MapPost("/store/items", async (HttpContext context, [FromBody] StoreItemDraft draft, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await classroom.CreateStoreItemAsync(draft, ct)) : Results.Unauthorized());
        application.MapPost("/store/purchase", async (HttpContext context, [FromBody] PurchaseRequest request, CancellationToken ct) =>
        {
            var headerClient = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            if (string.IsNullOrWhiteSpace(headerClient))
                return Results.Unauthorized();
            var bound = clients.GetAll().FirstOrDefault(c =>
                string.Equals(c.ClientId, headerClient, StringComparison.OrdinalIgnoreCase));
            if (bound?.StudentId is not Guid studentId || studentId != request.StudentId)
                return Results.Json(new { error = "Покупка только для ученика, привязанного к этому ПК." }, statusCode: 403);
            return Results.Ok(await classroom.PurchaseAsync(request, ct));
        });
        application.MapPut("/store/orders/{id:guid}/status", async (HttpContext context, Guid id, [FromBody] UpdateOrderStatusRequest request, CancellationToken ct) =>
            !IsTutor(context) ? Results.Unauthorized() : await classroom.UpdateOrderStatusAsync(id, request, ct) is { } order ? Results.Ok(order) : Results.NotFound());
        application.MapPost("/sync/prepare", async (HttpContext context, [FromBody] SyncPrepareRequest request, CancellationToken ct) =>
        {
            if (!OwnsClient(context, request.ClientId)) return Results.StatusCode(403);
            if (!IsTutor(context) && request.StudentId is Guid student
                && !clients.GetAll().Any(c => string.Equals(c.ClientId, request.ClientId, StringComparison.OrdinalIgnoreCase) && c.StudentId == student))
                return Results.StatusCode(403);
            return Results.Ok(await fileSync.PrepareAsync(request, ct));
        });
        application.MapGet("/sync/approval", async (string client_id, CancellationToken ct) =>
            await fileSync.GetApprovalAsync(client_id, ct) is { } approval ? Results.Ok(approval) : Results.NotFound());
        application.MapGet("/sync/approvals", async (HttpContext context, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await fileSync.ListPendingApprovalsAsync(ct)) : Results.Unauthorized());
        application.MapPost("/sync/approval/{id:guid}", async (HttpContext context, Guid id, [FromBody] SyncDecisionRequest request, CancellationToken ct) =>
            !IsTutor(context) ? Results.Unauthorized() : await fileSync.DecideAsync(id, string.IsNullOrWhiteSpace(request.Action) ? (request.Approved ? "update" : "restore") : request.Action, ct) is { } decision ? Results.Ok(decision) : Results.NotFound());
        application.MapPost("/sync/complete", async (HttpContext context, [FromBody] SyncCompleteRequest request, CancellationToken ct) =>
        {
            if (!OwnsClient(context, request.ClientId)) return Results.StatusCode(403);
            await fileSync.CompleteAsync(request.ClientId, ct);
            return Results.Ok(new { ok = true });
        });
        application.MapPost("/upload", async (HttpContext context, CancellationToken ct) =>
        {
            var clientId = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            if (string.IsNullOrWhiteSpace(clientId))
                return Results.Unauthorized();
            var path = ResolveUploadRelativePath(context);
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Нужен путь файла (query path или X-Relative-Path)." });
            return Results.Ok(await fileSync.UploadAsync(clientId, path, context.Request.Body, ct));
        });
        application.MapPost("/delete", async (HttpContext context, [FromBody] DeleteFileRequest request, CancellationToken ct) =>
        {
            var headerClient = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            if (string.IsNullOrWhiteSpace(headerClient)
                || !string.Equals(headerClient, request.ClientId, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "client_id не совпадает." }, statusCode: 403);
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
        application.MapGet("/projects/snapshots", async (HttpContext context, string client_id, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await fileSync.ListProjectSnapshotsAsync(client_id, ct)) : Results.Unauthorized());
        application.MapPost("/projects/snapshots/restore", async (HttpContext context, [FromBody] RestoreProjectSnapshotRequest request, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await fileSync.RestoreProjectSnapshotAsync(request, ct)) : Results.Unauthorized());
        application.MapGet("/update/student", (HttpContext context, string? channel) =>
        {
            var selection = ResolveStudentUpdateChannel(context, channel);
            if (selection.ErrorStatus != 0) return Results.StatusCode(selection.ErrorStatus);
            return assets.GetStudentRelease(selection.Channel) is { } release ? Results.Ok(release) : Results.NotFound();
        });
        application.MapGet("/update/student/file", (HttpContext context, string? channel, string? version) =>
        {
            var selection = ResolveStudentUpdateChannel(context, channel);
            if (selection.ErrorStatus != 0) return Results.StatusCode(selection.ErrorStatus);
            if (version is not null && (!AppReleaseVersion.IsValid(version)
                || AppReleaseVersion.ChannelFor(version) != selection.Channel)) return Results.BadRequest();
            return assets.OpenStudentUpdate(selection.Channel, version) is { } stream
                ? Results.File(stream, "application/octet-stream", "KIBERoneStudent.exe", enableRangeProcessing: true)
                : Results.NotFound();
        });
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
            var clientId = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            await assets.SaveScreenAsync(clientId, context.Request.Body, ct);
            return Results.Ok(new { ok = true });
        });
        application.MapGet("/screen", (HttpContext context, string client_id) =>
            !IsTutor(context) ? Results.Unauthorized() : assets.OpenScreen(client_id) is { } stream
                ? Results.File(stream, "image/jpeg", enableRangeProcessing: true)
                : Results.NotFound());
        application.MapPost("/quiz/start", async (HttpContext context, [FromBody] StartQuizRequest request, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await quizzes.StartAsync(request, ct)) : Results.Unauthorized());
        application.MapPost("/quiz/run", async (HttpContext context, StartQuizDocumentRequest request, CancellationToken ct) =>
            IsTutor(context) ? Results.Ok(await quizzes.StartDocumentAsync(request.Document, request.ClientIds, ct)) : Results.Unauthorized());
        application.MapPost("/quiz/answer", async (HttpContext context, [FromBody] SubmitQuizAnswerRequest request, CancellationToken ct) =>
        {
            var id = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
            if (!string.Equals(id, request.ClientId, StringComparison.Ordinal) || !clients.GetAll().Any(c => c.ClientId == id && c.StudentId is not null)) return Results.Unauthorized();
            return Results.Ok(await quizzes.SubmitAsync(request, ct));
        });
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

        clientId = NormalizeClientIdHeader(clientId);
        await commandSockets.AcceptAsync(
            context,
            clientId,
            commands,
            context.RequestAborted,
            onChannelOpened: () => audit.WriteAsync("Система", "Подключение канала", clientId, "/ws", "", 101, 0, CancellationToken.None),
            onChannelClosed: () => audit.WriteAsync("Система", "Отключение канала", clientId, "/ws", "", 200, 0, CancellationToken.None));
    }

    private (string? Channel, int ErrorStatus) ResolveStudentUpdateChannel(HttpContext context, string? channel)
    {
        // Authentication middleware has already checked the Tutor/device credentials.
        if (channel is not null && channel is not ("release" or "beta")) return (null, 400);
        if (IsTutor(context)) return channel is null ? (null, 400) : (channel, 0);
        var clientId = NormalizeClientIdHeader(context.Request.Headers["X-Client-Id"].ToString());
        var client = clients.GetAll().FirstOrDefault(x => string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
        if (client is null) return channel is null ? (null, 400) : (channel, 0);
        if (!AppReleaseVersion.IsValid(client.AppVersion)) return (null, 400);
        var currentChannel = AppReleaseVersion.ChannelFor(client.AppVersion);
        if (channel is not null && channel != currentChannel) return (null, 403);
        return (currentChannel, 0);
    }

    private async Task RunPresenceAuditLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await WritePresenceAuditsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WritePresenceAuditsAsync(CancellationToken ct)
    {
        foreach (var change in clients.DrainPresenceEvents())
        {
            var action = change.Online ? "Подключение" : "Отключение";
            var details = string.IsNullOrWhiteSpace(change.Hostname) ? string.Empty : change.Hostname;
            var target = string.IsNullOrWhiteSpace(change.PcNumber) ? change.ClientId : $"ПК {change.PcNumber}";
            try
            {
                await audit.WriteAsync("Система", action, change.ClientId, target, details, 200, 0, ct);
            }
            catch { }
        }
    }

    /// <summary>
    /// Audits only meaningful sync work — not polls, screenshots, telemetry, quizzes, or commands.
    /// </summary>
    private static bool ShouldAudit(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        var method = request.Method;
        if (path.Equals("/upload", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
        if (path.Equals("/delete", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
        if (path.Equals("/download", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(method)) return true;
        if (path.Equals("/versions/restore", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
        if (path.Equals("/sync/prepare", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
        if (path.Equals("/sync/complete", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
        if (path.StartsWith("/sync/approval/", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method)) return true;
        return false;
    }

    private static string AuditAction(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (path.Equals("/upload", StringComparison.OrdinalIgnoreCase)) return "Загрузка файла";
        if (path.Equals("/delete", StringComparison.OrdinalIgnoreCase)) return "Удаление файла";
        if (path.Equals("/download", StringComparison.OrdinalIgnoreCase)) return "Скачивание файла";
        if (path.Equals("/versions/restore", StringComparison.OrdinalIgnoreCase)) return "Восстановление версии";
        if (path.Equals("/sync/prepare", StringComparison.OrdinalIgnoreCase)) return "Подготовка синхронизации";
        if (path.Equals("/sync/complete", StringComparison.OrdinalIgnoreCase)) return "Завершение синхронизации";
        if (path.StartsWith("/sync/approval/", StringComparison.OrdinalIgnoreCase)) return "Решение по синхронизации";
        return request.Method;
    }

    private static string AuditTarget(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (path.Equals("/upload", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/download", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/delete", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/versions/restore", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = request.Query["path"].ToString();
            if (string.IsNullOrWhiteSpace(filePath))
                filePath = request.Headers["X-Relative-Path"].ToString();
            if (!string.IsNullOrWhiteSpace(filePath))
                return DecodeUploadPath(filePath);
        }
        return path + request.QueryString;
    }

    private string? StudentLocationFilter() =>
        LiveState.ShowAllLocations ? null : LiveState.LocationName;

    private bool OwnsClient(HttpContext context, string id) =>
        IsTutor(context) || string.Equals(context.Request.Headers["X-Client-Id"].ToString(), id, StringComparison.OrdinalIgnoreCase);

    private bool IsTutor(HttpContext context) =>
        context.Request.Headers["X-Tutor"].ToString().Equals("1", StringComparison.Ordinal)
        && TokensMatch(context.Request.Headers["X-Tutor-Token"].ToString(), serverOptions.TutorToken);

    private static string NormalizeClientIdHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        // HttpClient used to send X-Client-Id twice; ASP.NET joins values as "id,id".
        return value.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
    }

    internal static string ResolveUploadRelativePath(HttpContext context)
    {
        var fromQuery = context.Request.Query["path"].ToString();
        if (!string.IsNullOrWhiteSpace(fromQuery))
            return DecodeUploadPath(fromQuery);

        var fromHeader = context.Request.Headers["X-Relative-Path"].ToString();
        return string.IsNullOrWhiteSpace(fromHeader) ? fromHeader : DecodeUploadPath(fromHeader);
    }

    internal static string DecodeUploadPath(string value)
    {
        // Query values are usually already decoded by Kestrel; headers stay percent-encoded.
        if (value.IndexOf('%') < 0) return value;
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return value; }
    }

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
        await quizzes.StopSequencesAsync();
        if (presenceCts is not null)
        {
            try { presenceCts.Cancel(); } catch { }
            if (presenceTask is not null)
            {
                try { await presenceTask; } catch { }
            }
            presenceCts.Dispose();
            presenceCts = null;
            presenceTask = null;
        }
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
