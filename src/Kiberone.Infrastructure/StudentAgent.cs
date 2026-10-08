using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Collections.Concurrent;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed record CommandExecutionResult(bool Succeeded, string? Error = null)
{
    public static CommandExecutionResult Success { get; } = new(true);
}

public sealed record StudentConnectionState(bool IsConnected, string Message, string? TutorAddress, DateTimeOffset ChangedAt);

public sealed partial class StudentAgent : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly CancellationTokenSource lifetime = new();
    private readonly string clientId;
    private readonly string clientSecret;
    private readonly string pcNumber;
    private readonly string watchFolder;
    private readonly StudentFileSyncClient fileSync;
    private DateTimeOffset nextSyncAt = DateTimeOffset.MinValue;
    private int syncSeconds = 300;
    private DateTimeOffset nextScreenAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextLessonsAt = DateTimeOffset.MinValue;
    private StudentUpdateInfo? availableUpdate;
    private bool updateRequested;
    private string? stagedUpdatePath;
    private StudentUpdateInfo? stagedUpdateInfo;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private CancellationTokenSource? syncCycle;
    private Guid? previousStudentId;
    private Guid? studentId;
    private readonly ConcurrentQueue<SubmitQuizAnswerRequest> quizAnswers = new();
    private readonly TypingAttemptOutbox typingAttempts;
    private readonly ConcurrentDictionary<Guid, (CommandExecutionResult Result, DateTimeOffset ExpiresAt)> handledCommands = [];
    private Task? loopTask;
    private ClientWebSocket? commandSocket;
    private int commandSocketLive;
    private readonly SemaphoreSlim commandExecutionGate = new(1, 1);
    private bool sessionOnline;
    private DiscoveryBeacon? currentBeacon;
    public async Task<StudentMailAccount> GetMailAccountAsync(CancellationToken ct = default)
    {
        var beacon = currentBeacon ?? throw new InvalidOperationException("Сначала подключитесь к тьютору и выберите своё имя.");
        using var http = new HttpClient { BaseAddress = new Uri($"http://{beacon.Host}:{beacon.Port}"), Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.Add("X-Sync-Token", beacon.Token);
        http.DefaultRequestHeaders.Add("X-Client-Id", clientId);
        http.DefaultRequestHeaders.Add("X-Client-Secret", clientSecret);
        await SendHeartbeatAsync(http, ct);
        using var response = await http.GetAsync("/mail/account", ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "Имя ученика ещё не подтверждено тьютором. Дождитесь подключения и обновите почту."
                : "Почта недоступна. Проверьте подключение и настройки почты у тьютора.";
            try
            {
                var details = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (details.ValueKind == JsonValueKind.Object && details.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
                    message = error.GetString()!;
            }
            catch (JsonException) { /* Some HTTP errors have an empty or HTML response body. */ }
            throw new InvalidOperationException(message);
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StudentMailAccount>(JsonOptions, ct) ?? throw new InvalidDataException("Почта не получена.");
    }
    private int rediscoverRequested;

    public StudentAgent(string? pcNumber = null, string? watchFolder = null)
    {
        clientId = ResolveClientId();
        clientSecret = new DeviceCredentials(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "device-secrets")).GetOrCreateSecret(clientId);
        typingAttempts = new TypingAttemptOutbox(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KIBERone Classroom", $"typing-attempts-{clientId}.json"));
        this.pcNumber = pcNumber ?? Environment.MachineName;
        this.watchFolder = watchFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KIBERone Projects");
        Directory.CreateDirectory(this.watchFolder);
        fileSync = new StudentFileSyncClient(clientId, this.watchFolder);
        fileSync.StateChanged += state => SyncStateChanged?.Invoke(state);
    }

    public Func<ClassroomCommand, CancellationToken, Task<CommandExecutionResult>>? CommandHandler { get; set; }
    public Func<byte[]?>? ScreenProvider { get; set; }
    public Func<bool>? FocusModeStateProvider { get; set; }
    public Func<IReadOnlyList<InstalledApplication>>? ApplicationInventoryProvider { get; set; }
    public event Action<ClassroomAccessPolicy>? AccessPolicyChanged;
    private string? lastAccessRevision;
    private DateTimeOffset nextInventoryAt;
    public Func<bool>? WatchdogStateProvider { get; set; }
    public Func<int?>? BatteryProvider { get; set; }
    public Func<ClassroomCommand, CommandExecutionResult>? VpnCommandHandler { get; set; }
    public Func<string, CommandExecutionResult>? LaunchInstaller { get; set; }
    public Func<string, CommandExecutionResult>? ApplyWallpaperFile { get; set; }
    /// <summary>Optional SYSTEM copy for Program Files Student updates (VPN bridge).</summary>
    public Func<string, string, StudentUpdateInfo, int, bool>? ApplyElevatedUpdate { get; set; }
    public Func<bool>? VpnStateProvider { get; set; }
    public Func<VpnRuntimeInfo>? VpnRuntimeProvider { get; set; }
    public Func<bool>? ScreenLockStateProvider { get; set; }
    public event Action<StudentConnectionState>? ConnectionChanged;
    public event Action<ClassroomCommand>? CommandReceived;
    public event Action<StudentSyncState>? SyncStateChanged;
    public event Action<StudentUpdateInfo>? UpdateAvailable;
    public event Action<string>? UpdateStateChanged;
    public event Action? UpdateFailed;
    private void ReportUpdateFailure(string message)
    {
        UpdateStateChanged?.Invoke(message);
        UpdateFailed?.Invoke();
    }
    public event Action<QuizResult>? QuizResultReceived;
    public event Action<string?>? ScreenStateChanged;
    public event Action<Guid, string>? TypingResultStateChanged;
    /// <summary>Raised after a verified update is staged; UI should exit so Apply can replace the exe.</summary>
    public event Action? UpdateRestartRequested;
    public event Action<IReadOnlyList<StudentSummary>>? StudentsAvailable;
    public event Action<IReadOnlyList<TypingLessonOffer>>? LessonsAvailable;
    public event Action<string?>? PreferredGroupChanged;
    public string? PreferredGroupName { get; private set; }

    public void RequestUpdateInstallation()
    {
        updateRequested = true;
        UpdateStateChanged?.Invoke(availableUpdate is null ? "Ожидаем информацию об обновлении…" : "Скачиваем обновление…");
    }

    public void SubmitQuizAnswer(Guid sessionId, int selectedIndex) => SubmitQuizAnswer(sessionId, [selectedIndex]);
    public void SubmitQuizAnswer(Guid sessionId, IReadOnlyList<int> selectedIndices)
    {
        var first = selectedIndices.Count > 0 ? selectedIndices[0] : -1;
        quizAnswers.Enqueue(new SubmitQuizAnswerRequest(sessionId, clientId, first, selectedIndices.ToArray()));
    }
    public void SubmitTypingAttempt(TypingAttemptDraft attempt)
    {
        if (studentId is Guid assigned)
        {
            try
            {
                typingAttempts.Enqueue(new SubmitTypingAttemptRequest(assigned, attempt));
            }
            catch (Exception error)
            {
                TypingResultStateChanged?.Invoke(attempt.AttemptId, $"Результат не сохранён: {error.Message}");
            }
        }
        else
            TypingResultStateChanged?.Invoke(attempt.AttemptId, "Результат не отправлен: сначала выберите ученика.");
    }
    public void AssignStudent(Guid id)
    {
        lastAccessRevision = null;
        if (previousStudentId is Guid previous && previous != id) fileSync.RestoreForNewStudent();
        studentId = id;
        fileSync.StudentId = id;
        nextRecordsAt = DateTimeOffset.MinValue;
        nextSyncAt = DateTimeOffset.MinValue;
    }

    private async Task SyncSessionAsync(HttpClient http, CancellationToken ct)
    {
        await sessionGate.WaitAsync(ct);
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Volatile.Write(ref syncCycle, cycle);
        try { await fileSync.SyncOnceAsync(http, cycle.Token); }
        finally { Volatile.Write(ref syncCycle, null); sessionGate.Release(); }
    }
    public async Task<string> LogoutAsync(CancellationToken ct = default)
    {
        try { Volatile.Read(ref syncCycle)?.Cancel(); } catch (ObjectDisposedException) { }
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waiting.CancelAfter(TimeSpan.FromSeconds(30));
        await sessionGate.WaitAsync(waiting.Token);
        try
        {
            if (studentId is not Guid owner) return "Выберите своё имя.";
            await fileSync.SaveSessionCheckpointAsync(owner, ct: ct);
            var sent = false;
            if (currentBeacon is { } beacon)
            {
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://{beacon.Host}:{beacon.Port}"), Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.Add("X-Sync-Token", beacon.Token);
                http.DefaultRequestHeaders.Add("X-Client-Id", clientId);
                http.DefaultRequestHeaders.Add("X-Client-Secret", clientSecret);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await SendHeartbeatCoreAsync(http, deadline.Token);
                    await FlushTypingAttemptsAsync(http, deadline.Token);
                    nextRecordsAt = DateTimeOffset.MinValue;
                    await SyncPersonalRecordsAsync(http, deadline.Token, gateHeld: true);

                    await fileSync.SyncOnceAsync(http, deadline.Token);
                    sent = await fileSync.SaveSessionCheckpointAsync(owner, ct: ct) is null;
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
                { System.Diagnostics.Trace.TraceWarning($"Logout sync deferred: {error.GetType().Name}"); }
            }
            previousStudentId = owner;
            studentId = null;
            fileSync.EndSession();
            // Publish the cleared identity before releasing the session gate. Presence
            // refreshes only the timestamp and cannot clear the Tutor's student binding.
            if (currentBeacon is { } logoutBeacon)
            {
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
                {
                    BaseAddress = new Uri($"http://{logoutBeacon.Host}:{logoutBeacon.Port}"),
                    Timeout = TimeSpan.FromSeconds(5)
                };
                http.DefaultRequestHeaders.Add("X-Sync-Token", logoutBeacon.Token);
                http.DefaultRequestHeaders.Add("X-Client-Id", clientId);
                http.DefaultRequestHeaders.Add("X-Client-Secret", clientSecret);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try { await SendHeartbeatCoreAsync(http, deadline.Token); }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or JsonException)
                { Trace.TraceWarning($"Logout binding update deferred: {error.GetType().Name}"); }
            }
            quizAnswers.Clear();
            nextSyncAt = DateTimeOffset.MinValue;
            return sent ? "Сохранения отправлены. Выберите своё имя." : "Изменения сохранены на этом компьютере. Выберите своё имя.";
        }
        finally { sessionGate.Release(); }
    }

    public void Start(string? hintAddress = null)
    {
        if (loopTask is not null) return;
        loopTask = RunAsync(hintAddress, lifetime.Token);
    }

    public void ForceRediscover() => Interlocked.Exchange(ref rediscoverRequested, 1);

    private async Task RunAsync(string? hintAddress, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var beacon = await DiscoveryClient.DiscoverAsync(TimeSpan.FromSeconds(8), hintAddress, cancellationToken);
            if (beacon is null)
            {
                Raise(false, "Класс пока не найден. Ищем…", null);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                continue;
            }

            var address = $"http://{beacon.Host}:{beacon.Port}";
            currentBeacon = beacon;
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(address),
                // File sync and Student updates need a long budget; short ops use linked CTS below.
                Timeout = TimeSpan.FromMinutes(10)
            };
            http.DefaultRequestHeaders.Add("X-Sync-Token", beacon.Token);
            http.DefaultRequestHeaders.Add("X-Client-Id", clientId);
            http.DefaultRequestHeaders.Add("X-Client-Secret", clientSecret);

            // Confirm HTTP before claiming "connected" — WISP/AP isolation often lets UDP through only.
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(TimeSpan.FromSeconds(4));
                using var health = await http.GetAsync("/health", probeCts.Token);
                health.EnsureSuccessStatusCode();
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Raise(false,
                    "Тьютор виден по Wi‑Fi, но HTTP к нему закрыт (часто изоляция клиентов / WISP). Отключите AP isolation на роутере.",
                    address);
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
                continue;
            }

            Raise(true, "Подключено к классу", address);
            sessionOnline = true;
            var consecutiveFailures = 0;
            var rosterLoaded = false;
            var nextRosterAt = DateTimeOffset.MinValue;
            Task? socketTask = null;
            await using var presence = new StudentPresenceLease(http, cancellationToken);
            while (!cancellationToken.IsCancellationRequested && consecutiveFailures < 5)
            {
                if (Interlocked.Exchange(ref rediscoverRequested, 0) == 1)
                {
                    Raise(false, "Повторный поиск Tutor…", null);
                    break;
                }

                try
                {
                    if (!rosterLoaded || DateTimeOffset.UtcNow >= nextRosterAt)
                    {
                        using (var rosterCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            rosterCts.CancelAfter(TimeSpan.FromSeconds(10));
                            await LoadRosterAsync(http, rosterCts.Token);
                        }
                        rosterLoaded = true;
                        // Refresh empty roster — location filter / late hub sync can fill in later.
                        nextRosterAt = DateTimeOffset.UtcNow.AddSeconds(20);
                    }
                    if (DateTimeOffset.UtcNow >= nextLessonsAt)
                    {
                        try
                        {
                            using var lessonsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            lessonsTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                            await LoadLessonsAsync(http, lessonsTimeout.Token);
                        }
                        catch (Exception error)
                        {
                            UpdateStateChanged?.Invoke($"Каталог уроков не обновился: {error.Message}");
                        }
                        nextLessonsAt = DateTimeOffset.UtcNow.AddSeconds(15);
                    }

                    using (var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        heartbeatCts.CancelAfter(TimeSpan.FromSeconds(15));
                        await SendHeartbeatAsync(http, heartbeatCts.Token);
                        try { await SyncPersonalRecordsAsync(http, heartbeatCts.Token); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        { Trace.TraceWarning("Personal records sync timed out; classroom heartbeat succeeded."); }
                    }

                    if (studentId is Guid assigned)
                        fileSync.StudentId = assigned;
                    socketTask = await EnsureCommandSocketAsync(beacon, http, socketTask, cancellationToken);
                    await PollCommandsAsync(http, cancellationToken);
                    await FlushQuizAnswersAsync(http, cancellationToken);
                    await FlushTypingAttemptsAsync(http, cancellationToken);
                    if (DateTimeOffset.UtcNow >= nextSyncAt)
                    {
                        try
                        {
                            await SyncSessionAsync(http, cancellationToken);
                        }
                        catch (Exception error)
                        {
                            // Keep the classroom session alive when one sync cycle fails.
                            SyncStateChanged?.Invoke(new StudentSyncState($"Синхронизация не удалась: {error.Message}", 0, DateTimeOffset.UtcNow));
                        }
                        nextSyncAt = DateTimeOffset.UtcNow.AddSeconds(syncSeconds);
                    }
                    if (ScreenProvider is not null && DateTimeOffset.UtcNow >= nextScreenAt)
                    {
                        try
                        {
                            await SendScreenAsync(http, cancellationToken);
                            ScreenStateChanged?.Invoke(null);
                        }
                        catch (Exception error)
                        {
                            // Do not fail the whole agent loop when a single screenshot upload fails.
                            ScreenStateChanged?.Invoke($"Снимок экрана не отправился: {error.Message}");
                        }
                        nextScreenAt = DateTimeOffset.UtcNow.AddSeconds(30);
                    }
                    if (updateRequested && availableUpdate is not null)
                    {
                        updateRequested = false;
                        try
                        {
                            if (stagedUpdatePath is not null && File.Exists(stagedUpdatePath))
                                UpdateRestartRequested?.Invoke();
                            else
                                await StageUpdateAsync(http, availableUpdate, cancellationToken);
                        }
                        catch (Exception error)
                        {
                            ReportUpdateFailure($"Обновление не установлено: {error.Message}");
                        }
                    }
                    if (!sessionOnline)
                    {
                        Raise(true, "Подключено к классу", address);
                        sessionOnline = true;
                    }
                    consecutiveFailures = 0;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= 2)
                    {
                        sessionOnline = false;
                        Raise(false, "Связь с классом прервалась. Пробуем ещё раз…", address);
                    }
                    else
                        SyncStateChanged?.Invoke(new StudentSyncState($"Временный сбой связи: {error.Message}", 0, DateTimeOffset.UtcNow));
                }
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            await CloseCommandSocketAsync();
            if (socketTask is not null)
            {
                try { await socketTask; } catch { }
            }
        }
    }

    private async Task SendHeartbeatAsync(HttpClient http, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken);
        try { await SendHeartbeatCoreAsync(http, cancellationToken); }
        finally { sessionGate.Release(); }
    }
    private async Task SendHeartbeatCoreAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var vpn = VpnRuntimeProvider?.Invoke();
        var heartbeat = new HeartbeatRequest(
            clientId,
            pcNumber,
            Environment.MachineName,
            fileSync.WatchFolder,
            BuildInfo.Version,
            studentId,
            null,
            new ClientRuntimeInfo(
                WatchdogStateProvider?.Invoke() ?? false,
                FocusModeStateProvider?.Invoke() ?? false,
                string.Empty,
                BatteryProvider?.Invoke(),
                vpn?.Connected ?? VpnStateProvider?.Invoke() ?? false,
                ScreenLockStateProvider?.Invoke() ?? false,
                vpn?.PingMs,
                vpn?.Region));
        using var response = await http.PostAsJsonAsync("/heartbeat", heartbeat, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var settings = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions, cancellationToken);
        if (settings is not null)
        {
            var policy = settings.AccessPolicy ?? ClassroomAccessPolicy.Empty;
            if (lastAccessRevision != policy.Revision)
            {
                AccessPolicyChanged?.Invoke(policy);
                lastAccessRevision = policy.Revision;
            }
            if (ApplicationInventoryProvider is not null && DateTimeOffset.UtcNow >= nextInventoryAt)
            {
                var inventory = await Task.Run(ApplicationInventoryProvider, cancellationToken);
                using var inventoryResponse = await http.PostAsJsonAsync("/apps/inventory", new ApplicationInventoryRequest(clientId, inventory), JsonOptions, cancellationToken);
                if (inventoryResponse.IsSuccessStatusCode) nextInventoryAt = DateTimeOffset.UtcNow.AddMinutes(2);
            }
            syncSeconds = Math.Clamp(settings.SyncSeconds, 5, 3600);
            if (!string.IsNullOrWhiteSpace(settings.PreferredGroupName)
                && !string.Equals(PreferredGroupName, settings.PreferredGroupName, StringComparison.Ordinal))
            {
                PreferredGroupName = settings.PreferredGroupName;
                PreferredGroupChanged?.Invoke(PreferredGroupName);
            }
            var previousFolder = fileSync.WatchFolder;
            var workspaceChanged = studentId is not null && ApplyStudentWorkspace(
                settings.SaveStudentName,
                settings.SaveModule,
                settings.SaveIgnoreFolders);
            if (workspaceChanged || !string.Equals(previousFolder, fileSync.WatchFolder, StringComparison.OrdinalIgnoreCase))
                nextSyncAt = DateTimeOffset.MinValue;
            if (settings.StudentUpdate is { } update && IsMatchingStudentUpdate(update))
            {
                availableUpdate = update;
                UpdateAvailable?.Invoke(update);
            }
            else
            {
                availableUpdate = null;
                if (settings.StudentUpdate is not null)
                    Trace.TraceWarning("Tutor advertised an invalid, older or wrong-channel Student update.");
            }
        }
    }

    private async Task PollCommandsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var commands = await http.GetFromJsonAsync<List<ClassroomCommand>>(
            $"/commands?client_id={Uri.EscapeDataString(clientId)}", JsonOptions, deadline.Token) ?? [];
        foreach (var command in commands)
            await ExecuteAndAckAsync(http, command, cancellationToken);
    }

    private async Task<Task?> EnsureCommandSocketAsync(DiscoveryBeacon beacon, HttpClient http, Task? receiveTask, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref commandSocketLive) == 1 && commandSocket?.State == WebSocketState.Open)
            return receiveTask;

        await CloseCommandSocketAsync();
        if (receiveTask is not null)
        {
            try { await receiveTask; } catch { }
        }

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Sync-Token", beacon.Token);
        socket.Options.SetRequestHeader("X-Client-Id", clientId);
        socket.Options.SetRequestHeader("X-Client-Secret", clientSecret);
        var uri = new Uri($"ws://{beacon.Host}:{beacon.Port}/ws?client_id={Uri.EscapeDataString(clientId)}");
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await socket.ConnectAsync(uri, connectCts.Token);
        }
        catch
        {
            socket.Dispose();
            Volatile.Write(ref commandSocketLive, 0);
            return null;
        }

        commandSocket = socket;
        Volatile.Write(ref commandSocketLive, 1);
        return ReceivePushedCommandsAsync(socket, http, cancellationToken);
    }

    private async Task ReceivePushedCommandsAsync(ClientWebSocket socket, HttpClient http, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Volatile.Write(ref commandSocketLive, 0);
                        return;
                    }
                    if (result.Count > 0)
                        message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                ClassroomCommand? command;
                try
                {
                    command = JsonSerializer.Deserialize<ClassroomCommand>(message.ToArray(), JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (command is null || command.Id == Guid.Empty) continue;
                await ExecuteAndAckAsync(http, command, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            Volatile.Write(ref commandSocketLive, 0);
        }
    }

    private async Task ExecuteAndAckAsync(HttpClient http, ClassroomCommand command, CancellationToken cancellationToken)
    {
        await commandExecutionGate.WaitAsync(cancellationToken);
        try
        {
            if (command.ExpiresAt <= DateTimeOffset.UtcNow) return;
            CommandExecutionResult result;
            if (handledCommands.TryGetValue(command.Id, out var cached)) result = cached.Result;
            else
            {
                CommandReceived?.Invoke(command);
                try
                {
                    if (command.Kind == ClassroomCommandKinds.SyncNow) nextSyncAt = DateTimeOffset.MinValue;
                    if (command.Kind == ClassroomCommandKinds.SetWorkspace)
                    {
                        await sessionGate.WaitAsync(cancellationToken);
                        try { if (studentId is not null) ApplyWorkspaceCommand(command); }
                        finally { sessionGate.Release(); }
                        nextSyncAt = DateTimeOffset.MinValue;
                    }
                    if (command.Kind == ClassroomCommandKinds.Configure && command.Payload.ValueKind == JsonValueKind.Object
                    && command.Payload.TryGetProperty("sync_seconds", out var seconds) && seconds.TryGetInt32(out var configured))
                    syncSeconds = Math.Clamp(configured, 15, 3600);
                    result = await TryHandleSoftwareCommandAsync(http, command, cancellationToken)
                    ?? await TryHandleVpnCommandAsync(command, cancellationToken)
                    ?? (CommandHandler is null
                    ? new CommandExecutionResult(false, "Обработчик команд не настроен.")
                    : await CommandHandler(command, cancellationToken));
                }
                catch (Exception error)
                {
                    result = new CommandExecutionResult(false, error.Message);
                }
                handledCommands[command.Id] = (result, command.ExpiresAt);
                TrimHandledCommands();
            }
            {
                using var acknowledgementTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                acknowledgementTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var acknowledgement = new CommandAcknowledgement(command.Id, result.Succeeded, result.Error);
                using var response = await http.PostAsJsonAsync(
                $"/commands/{command.Id}/ack?client_id={Uri.EscapeDataString(clientId)}",
                acknowledgement,
                JsonOptions,
                acknowledgementTimeout.Token);
                // A second transport can deliver an already acknowledged command.
                if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                response.EnsureSuccessStatusCode();
            }
        }
        finally { commandExecutionGate.Release(); }
    }


    private void ApplyWorkspaceCommand(ClassroomCommand command)
    {
        if (command.Payload.ValueKind != JsonValueKind.Object) return;
        command.Payload.TryGetProperty("module", out var moduleElement);
        command.Payload.TryGetProperty("student_name", out var nameElement);
        var module = moduleElement.ValueKind == JsonValueKind.String ? moduleElement.GetString() : null;
        var name = nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
        ApplyStudentWorkspace(name, module);
    }

    private bool ApplyStudentWorkspace(string? name, string? module, IReadOnlyList<string>? ignoreFolders = null)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var home = FileSyncService.StudentDesktopFolder(name);
            FileSyncService.PromoteLegacyModuleFolder(home, module);
            fileSync.SetWorkspace(home);
        }
        fileSync.SetIgnoredFolders(ignoreFolders);
        return fileSync.SetActiveModule(module);
    }

    private void TrimHandledCommands()
    {
        foreach (var item in handledCommands)
            if (item.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                handledCommands.TryRemove(item.Key, out _);
    }

    private async Task CloseCommandSocketAsync()
    {
        Volatile.Write(ref commandSocketLive, 0);
        var socket = Interlocked.Exchange(ref commandSocket, null);
        if (socket is null) return;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
            }
        }
        catch
        {
        }
        socket.Dispose();
    }

    private async Task LoadRosterAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var students = await http.GetFromJsonAsync<List<StudentSummary>>("/students", JsonOptions, cancellationToken) ?? [];
        try
        {
            using var health = await http.GetAsync("/health", cancellationToken);
            if (health.IsSuccessStatusCode)
            {
                await using var stream = await health.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("preferred_group", out var group) && group.ValueKind == JsonValueKind.String)
                {
                    var name = group.GetString();
                    if (!string.Equals(PreferredGroupName, name, StringComparison.Ordinal))
                    {
                        PreferredGroupName = name;
                        PreferredGroupChanged?.Invoke(PreferredGroupName);
                    }
                }
            }
        }
        catch
        {
        }

        StudentsAvailable?.Invoke(students);
    }

    private async Task LoadLessonsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var catalog = await http.GetFromJsonAsync<List<TypingLessonOffer>>("/typing/lessons", JsonOptions, cancellationToken) ?? [];
        LessonsAvailable?.Invoke(catalog);
    }

    private async Task SendScreenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var bytes = ScreenProvider?.Invoke();
        if (bytes is null || bytes.Length == 0) return;
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        // X-Client-Id is already on DefaultRequestHeaders — do not set it again or ASP.NET joins "id,id"
        // and the Tutor looks up the screen under a different hash.
        using var response = await http.PostAsync("/screen", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task FlushQuizAnswersAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (quizAnswers.TryPeek(out var answer))
        {
            try
            {
                using var response = await http.PostAsJsonAsync("/quiz/answer", answer, JsonOptions, timeout.Token);
                if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.Conflict)
                {
                    quizAnswers.TryDequeue(out _);
                    QuizResultReceived?.Invoke(new QuizResult(answer.SessionId, false, 0, "Этот вопрос уже завершён."));
                    continue;
                }
                if (!response.IsSuccessStatusCode) return;
                var result = await response.Content.ReadFromJsonAsync<QuizResult>(JsonOptions, timeout.Token);
                quizAnswers.TryDequeue(out _);
                if (result is not null) QuizResultReceived?.Invoke(result);
            }
            catch (Exception error) when (error is HttpRequestException or JsonException
                || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                Trace.TraceWarning($"Quiz answer retry: {error.GetType().Name}");
                return;
            }
        }
    }

    private async Task FlushTypingAttemptsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        while (studentId is Guid assigned
            && typingAttempts.TryGetNextForStudent(assigned, out var attempt) && attempt is not null)
        {
            try
            {
                using var response = await http.PostAsJsonAsync("/typing/attempts", attempt, JsonOptions, cancellationToken);
                response.EnsureSuccessStatusCode();
                typingAttempts.Acknowledge(attempt.Attempt.AttemptId);
                TypingResultStateChanged?.Invoke(attempt.Attempt.AttemptId, "Результат отправлен тьютору.");
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                TypingResultStateChanged?.Invoke(attempt.Attempt.AttemptId,
                    $"Результат ожидает отправки: {error.Message}");
                return;
            }
        }
    }

    private static bool IsMatchingStudentUpdate(StudentUpdateInfo update) =>
        AppReleaseVersion.IsValid(update.Version)
        && AppReleaseVersion.ChannelFor(update.Version) == BuildInfo.Channel
        && AppReleaseVersion.IsNewer(update.Version, BuildInfo.Version);

    private async Task StageUpdateAsync(HttpClient http, StudentUpdateInfo update, CancellationToken cancellationToken)
    {
        if (!IsMatchingStudentUpdate(update))
        {
            ReportUpdateFailure("Обновление не установлено: версия не новее текущей или относится к другому каналу.");
            return;
        }
        if (!StudentUpdateSignature.Verify(update.Version, update.Size, update.Sha256, update.Signature))
        {
            ReportUpdateFailure("Обновление не установлено: недействительная подпись.");
            return;
        }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "updates");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"student-{update.Version}-{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await http.GetAsync($"/update/student/file?channel={Uri.EscapeDataString(BuildInfo.Channel)}&version={Uri.EscapeDataString(update.Version)}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > update.Size) throw new InvalidOperationException("Размер обновления превышает манифест.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                if (total != update.Size) throw new InvalidOperationException("Размер обновления не совпадает с манифестом.");
            }
            string hash;
            await using (var verify = File.OpenRead(temporary))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SHA-256 обновления не совпадает с манифестом.");
            stagedUpdatePath = Path.ChangeExtension(temporary, ".exe");
            File.Move(temporary, stagedUpdatePath, true);
            stagedUpdateInfo = update;
            UpdateStateChanged?.Invoke("Обновление проверено. Перезапуск для установки…");
            UpdateRestartRequested?.Invoke();
        }
        catch (Exception error)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            ReportUpdateFailure($"Обновление не установлено: {error.Message}");
        }
    }

    /// <summary>
    /// Writes the apply-update script (and optionally copies via SYSTEM bridge into Program Files).
    /// Call before Shutdown so the script exists even if dispose is fire-and-forget.
    /// </summary>
    public bool PrepareStagedUpdate()
    {
        if (stagedUpdateInfo is null || !IsMatchingStudentUpdate(stagedUpdateInfo)) return false;
        if (stagedUpdatePath is null || !File.Exists(stagedUpdatePath)) return false;
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !IsStudentExecutablePath(current)) return false;

        var watchdogStop = Path.Combine(Path.GetTempPath(), "KIBERone-Classroom-Watchdog", "stop.flag");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(watchdogStop)!);
            File.WriteAllText(watchdogStop, "stop");
        }
        catch
        {
            // best effort
        }

        var appliedViaBridge = false;
        if (ApplyElevatedUpdate is not null)
        {
            try
            {
                appliedViaBridge = stagedUpdateInfo is not null &&
                    ApplyElevatedUpdate(stagedUpdatePath, current, stagedUpdateInfo, Environment.ProcessId);
            }
            catch
            {
                appliedViaBridge = false;
            }
        }

        if (!appliedViaBridge && !CanWriteUpdateTarget(current))
        {
            ReportUpdateFailure("Обновление скачано, но нет доступа к папке Student. Нужно обновить установленную системную службу через установщик.");
            return false;
        }

        var script = Path.Combine(Path.GetDirectoryName(stagedUpdatePath)!, $"apply-update-{Guid.NewGuid():N}.cmd");
        var pid = Environment.ProcessId;
        if (appliedViaBridge)
        {
            var marker = stagedUpdatePath + ".result";
            File.WriteAllLines(script,
            [
                "@echo off",
                "setlocal",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
                "if not errorlevel 1 (ping 127.0.0.1 -n 2 >NUL & goto wait)",
                "set attempts=0",
                ":waitresult",
                $"if exist \"{marker}\" goto checkresult",
                "set /a attempts+=1",
                "if %attempts% GEQ 180 goto failed",
                "ping 127.0.0.1 -n 2 >NUL",
                "goto waitresult",
                ":checkresult",
                $"findstr /x /c:\"ok\" \"{marker}\" >NUL",
                "if errorlevel 1 goto failed",
                $"del /F /Q \"{stagedUpdatePath}\" >NUL 2>&1",
                $"del /F /Q \"{marker}\" >NUL 2>&1",
                $"start \"\" \"{current}\"",
                "del \"%~f0\"",
                "exit /b 0",
                ":failed",
                $"if exist \"{marker}\" copy /Y \"{marker}\" \"%TEMP%\\kiberone-update-failed.txt\" >NUL",
                "if not exist \"%TEMP%\\kiberone-update-failed.txt\" echo UPDATE_BRIDGE_TIMEOUT>\"%TEMP%\\kiberone-update-failed.txt\"",
                $"start \"\" \"{current}\"",
                "del \"%~f0\"",
                "exit /b 1"
            ]);
        }
        else
        {
            File.WriteAllLines(script,
            [
                "@echo off",
                "setlocal",
                ":wait",
                $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
                "if not errorlevel 1 (ping 127.0.0.1 -n 2 >NUL & goto wait)",
                "echo stop>\"%TEMP%\\KIBERone-Classroom-Watchdog\\stop.flag\"",
                "ping 127.0.0.1 -n 2 >NUL",
                $"copy /Y \"{stagedUpdatePath}\" \"{current}\" >NUL",
                "if errorlevel 1 (",
                "  echo UPDATE_COPY_FAILED>%TEMP%\\kiberone-update-failed.txt",
                $"  start \"\" \"{current}\"",
                "  del \"%~f0\"",
                "  exit /b 1",
                ")",
                $"del /F /Q \"{stagedUpdatePath}\" >NUL 2>&1",
                $"start \"\" \"{current}\"",
                "del \"%~f0\""
            ]);
        }

        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(script);
        if (Process.Start(start) is null)
            return false;
        stagedUpdatePath = null;
        return true;
    }

    private static bool CanWriteUpdateTarget(string target)
    {
        try
        {
            var directory = Path.GetDirectoryName(target);
            if (string.IsNullOrWhiteSpace(directory)) return false;
            var probe = Path.Combine(directory, $".update-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsStudentExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Path APIs are OS-specific; installed Student paths are Windows-style even when unit-tested on macOS.
        var fileName = path.Replace('\\', '/');
        var slash = fileName.LastIndexOf('/');
        if (slash >= 0)
            fileName = fileName[(slash + 1)..];
        var name = Path.GetFileNameWithoutExtension(fileName);
        return name.Equals("Kiberone.Student", StringComparison.OrdinalIgnoreCase)
               || name.Equals("KIBERoneStudent", StringComparison.OrdinalIgnoreCase);
    }

    private void Raise(bool connected, string message, string? address) =>
        ConnectionChanged?.Invoke(new StudentConnectionState(connected, message, address, DateTimeOffset.UtcNow));

    private static string ResolveClientId()
    {
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(LocalAddressResolver.IsClassroomLanInterface)
            .Select(network => network.GetPhysicalAddress().ToString())
            .FirstOrDefault(value => value.Length >= 12);
        if (string.IsNullOrWhiteSpace(address))
        {
            address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback
                                  && network.OperationalStatus == OperationalStatus.Up
                                  && !LocalAddressResolver.IsTunnelName(network.Name)
                                  && !LocalAddressResolver.IsTunnelName(network.Description))
                .Select(network => network.GetPhysicalAddress().ToString())
                .FirstOrDefault(value => value.Length >= 12);
        }
        return string.IsNullOrWhiteSpace(address) ? $"host-{Environment.MachineName.ToLowerInvariant()}" : address.ToLowerInvariant();
    }

    private async Task<CommandExecutionResult?> TryHandleSoftwareCommandAsync(HttpClient http, ClassroomCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind == ClassroomCommandKinds.InstallStarterPack)
        {
            var runInstallers = command.Payload.TryGetProperty("run_installers", out var flag) && flag.ValueKind == JsonValueKind.True;
            UpdateStateChanged?.Invoke("Скачиваем стартовый пакет…");
            var destination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "KIBERone Start");
            var state = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KIBERone Classroom",
                "starter-applied.json");
            var result = await ClassroomSoftwarePush.InstallStarterPackAsync(
                http, destination, state, runInstallers, LaunchInstaller, message => UpdateStateChanged?.Invoke(message), cancellationToken);
            UpdateStateChanged?.Invoke(result.Succeeded ? "Стартовый пакет установлен." : result.Error ?? "Не удалось установить пакет.");
            return result;
        }

        if (command.Kind == ClassroomCommandKinds.SetWallpaper)
        {
            UpdateStateChanged?.Invoke("Ставим обои…");
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KIBERone Classroom",
                "wallpaper");
            var path = await ClassroomSoftwarePush.DownloadWallpaperAsync(http, directory, cancellationToken);
            var result = ApplyWallpaperFile?.Invoke(path)
                ?? new CommandExecutionResult(false, "Установка обоев недоступна на этом компьютере.");
            UpdateStateChanged?.Invoke(result.Succeeded ? "Обои установлены." : result.Error ?? "Не удалось поставить обои.");
            return result;
        }

        return null;
    }

    private async Task<CommandExecutionResult?> TryHandleVpnCommandAsync(ClassroomCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind is not (
            ClassroomCommandKinds.VpnConnect
            or ClassroomCommandKinds.VpnDisconnect
            or ClassroomCommandKinds.VpnStatus
            or ClassroomCommandKinds.VpnInstallConfig))
            return null;

        if (VpnCommandHandler is null)
            return new CommandExecutionResult(false, "VPN не настроен на этом ПК.");

        try
        {
            // VPN connect/health can exceed 45s on slow PCs; abandoning the task left VpnCommandGate locked.
            return await Task.Run(() => VpnCommandHandler(command), cancellationToken);
        }
        catch (Exception error)
        {
            return new CommandExecutionResult(false, error.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await CloseCommandSocketAsync();
        if (loopTask is not null)
        {
            try { await loopTask; } catch (OperationCanceledException) { }
        }
        lifetime.Dispose();
    }
}
