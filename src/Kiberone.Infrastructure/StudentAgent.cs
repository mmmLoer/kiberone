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

public sealed class StudentAgent : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly CancellationTokenSource lifetime = new();
    private readonly string clientId;
    private readonly string pcNumber;
    private readonly string watchFolder;
    private readonly StudentFileSyncClient fileSync;
    private DateTimeOffset nextSyncAt = DateTimeOffset.MinValue;
    private int syncSeconds = 300;
    private DateTimeOffset nextScreenAt = DateTimeOffset.MinValue;
    private StudentUpdateInfo? availableUpdate;
    private bool updateRequested;
    private string? stagedUpdatePath;
    private Guid? studentId;
    private readonly ConcurrentQueue<string> clientEvents = new();
    private readonly ConcurrentQueue<SubmitQuizAnswerRequest> quizAnswers = new();
    private readonly ConcurrentDictionary<Guid, byte> handledCommands = [];
    private Task? loopTask;
    private ClientWebSocket? commandSocket;
    private int commandSocketLive;

    public StudentAgent(string? pcNumber = null, string? watchFolder = null)
    {
        clientId = ResolveClientId();
        this.pcNumber = pcNumber ?? Environment.MachineName;
        this.watchFolder = watchFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KIBERone Projects");
        Directory.CreateDirectory(this.watchFolder);
        fileSync = new StudentFileSyncClient(clientId, this.watchFolder);
        fileSync.StateChanged += state => SyncStateChanged?.Invoke(state);
    }

    public Func<ClassroomCommand, CancellationToken, Task<CommandExecutionResult>>? CommandHandler { get; set; }
    public Func<byte[]?>? ScreenProvider { get; set; }
    public Func<bool>? FocusModeStateProvider { get; set; }
    public Func<bool>? WatchdogStateProvider { get; set; }
    public Func<int?>? BatteryProvider { get; set; }
    public Func<ClassroomCommand, CommandExecutionResult>? VpnCommandHandler { get; set; }
    public Func<string, CommandExecutionResult>? LaunchInstaller { get; set; }
    public Func<string, CommandExecutionResult>? ApplyWallpaperFile { get; set; }
    public Func<bool>? VpnStateProvider { get; set; }
    public Func<VpnRuntimeInfo>? VpnRuntimeProvider { get; set; }
    public Func<bool>? ScreenLockStateProvider { get; set; }
    public event Action<StudentConnectionState>? ConnectionChanged;
    public event Action<ClassroomCommand>? CommandReceived;
    public event Action<StudentSyncState>? SyncStateChanged;
    public event Action<StudentUpdateInfo>? UpdateAvailable;
    public event Action<string>? UpdateStateChanged;
    public event Action<IReadOnlyList<StudentSummary>>? StudentsAvailable;
    public event Action<string?>? PreferredGroupChanged;
    public string? PreferredGroupName { get; private set; }

    public void RequestUpdateInstallation()
    {
        updateRequested = true;
        UpdateStateChanged?.Invoke(availableUpdate is null ? "Ожидаем информацию об обновлении…" : "Скачиваем обновление…");
    }

    public void QueueClientEvent(string eventName) => clientEvents.Enqueue(eventName);
    public void SubmitQuizAnswer(Guid sessionId, int selectedIndex) => quizAnswers.Enqueue(new SubmitQuizAnswerRequest(sessionId, clientId, selectedIndex));
    public void AssignStudent(Guid id)
    {
        studentId = id;
        fileSync.StudentId = id;
    }

    public void Start(string? hintAddress = null)
    {
        if (loopTask is not null) return;
        loopTask = RunAsync(hintAddress, lifetime.Token);
    }

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
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri(address),
                Timeout = TimeSpan.FromSeconds(10)
            };
            http.DefaultRequestHeaders.Add("X-Sync-Token", beacon.Token);
            http.DefaultRequestHeaders.Add("X-Client-Id", clientId);
            Raise(true, "Подключено к классу", address);
            var consecutiveFailures = 0;
            var rosterLoaded = false;
            Task? socketTask = null;
            while (!cancellationToken.IsCancellationRequested && consecutiveFailures < 3)
            {
                try
                {
                    if (!rosterLoaded)
                    {
                        await LoadRosterAsync(http, cancellationToken);
                        rosterLoaded = true;
                    }
                    await SendHeartbeatAsync(http, cancellationToken);
                    if (studentId is Guid assigned)
                        fileSync.StudentId = assigned;
                    socketTask = await EnsureCommandSocketAsync(beacon, http, socketTask, cancellationToken);
                    if (Volatile.Read(ref commandSocketLive) == 0)
                        await PollCommandsAsync(http, cancellationToken);
                    await FlushClientEventsAsync(http, cancellationToken);
                    await FlushQuizAnswersAsync(http, cancellationToken);
                    if (DateTimeOffset.UtcNow >= nextSyncAt)
                    {
                        await fileSync.SyncOnceAsync(http, cancellationToken);
                        nextSyncAt = DateTimeOffset.UtcNow.AddSeconds(syncSeconds);
                    }
                    if (ScreenProvider is not null && DateTimeOffset.UtcNow >= nextScreenAt)
                    {
                        await SendScreenAsync(http, cancellationToken);
                        nextScreenAt = DateTimeOffset.UtcNow.AddSeconds(30);
                    }
                    if (updateRequested && availableUpdate is not null && stagedUpdatePath is null)
                    {
                        updateRequested = false;
                        await StageUpdateAsync(http, availableUpdate, cancellationToken);
                    }
                    consecutiveFailures = 0;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    consecutiveFailures++;
                    Raise(false, "Связь с классом прервалась. Пробуем ещё раз…", address);
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
            syncSeconds = Math.Clamp(settings.SyncSeconds, 5, 3600);
            if (!string.IsNullOrWhiteSpace(settings.PreferredGroupName)
                && !string.Equals(PreferredGroupName, settings.PreferredGroupName, StringComparison.Ordinal))
            {
                PreferredGroupName = settings.PreferredGroupName;
                PreferredGroupChanged?.Invoke(PreferredGroupName);
            }
            var previousFolder = fileSync.WatchFolder;
            if (!string.IsNullOrWhiteSpace(settings.SaveStudentName))
                fileSync.SetWorkspace(FileSyncService.StudentDesktopFolder(settings.SaveStudentName, settings.SaveModule));
            else if (!string.IsNullOrWhiteSpace(settings.SaveModule))
                fileSync.SetWorkspace(Path.Combine(watchFolder, FileSyncService.SanitizeFolderName(settings.SaveModule)));
            if (!string.Equals(previousFolder, fileSync.WatchFolder, StringComparison.OrdinalIgnoreCase))
                nextSyncAt = DateTimeOffset.MinValue;
            if (settings.StudentUpdate is not null)
            {
                availableUpdate = settings.StudentUpdate;
                UpdateAvailable?.Invoke(settings.StudentUpdate);
            }
        }
    }

    private async Task PollCommandsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var commands = await http.GetFromJsonAsync<List<ClassroomCommand>>(
            $"/commands?client_id={Uri.EscapeDataString(clientId)}", JsonOptions, cancellationToken) ?? [];
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
        var uri = new Uri($"ws://{beacon.Host}:{beacon.Port}/ws?client_id={Uri.EscapeDataString(clientId)}&token={Uri.EscapeDataString(beacon.Token)}");
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
        if (!handledCommands.TryAdd(command.Id, 0)) return;
        TrimHandledCommands();

        CommandReceived?.Invoke(command);
        CommandExecutionResult result;
        try
        {
            if (command.Kind == ClassroomCommandKinds.SyncNow) nextSyncAt = DateTimeOffset.MinValue;
            if (command.Kind == ClassroomCommandKinds.SetWorkspace)
            {
                ApplyWorkspaceCommand(command);
                nextSyncAt = DateTimeOffset.MinValue;
            }
            if (command.Kind == ClassroomCommandKinds.Configure && command.Payload.ValueKind == JsonValueKind.Object
                && command.Payload.TryGetProperty("sync_seconds", out var seconds) && seconds.TryGetInt32(out var configured))
                syncSeconds = Math.Clamp(configured, 15, 3600);
            result = await TryHandleSoftwareCommandAsync(http, command, cancellationToken)
                ?? TryHandleVpnCommand(command)
                ?? (CommandHandler is null
                    ? new CommandExecutionResult(false, "Обработчик команд не настроен.")
                    : await CommandHandler(command, cancellationToken));
        }
        catch (Exception error)
        {
            result = new CommandExecutionResult(false, error.Message);
        }
        var acknowledgement = new CommandAcknowledgement(command.Id, result.Succeeded, result.Error);
        using var response = await http.PostAsJsonAsync(
            $"/commands/{command.Id}/ack?client_id={Uri.EscapeDataString(clientId)}",
            acknowledgement,
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private void ApplyWorkspaceCommand(ClassroomCommand command)
    {
        if (command.Payload.ValueKind != JsonValueKind.Object) return;
        command.Payload.TryGetProperty("module", out var moduleElement);
        command.Payload.TryGetProperty("student_name", out var nameElement);
        var module = moduleElement.ValueKind == JsonValueKind.String ? moduleElement.GetString() : null;
        var name = nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
        if (!string.IsNullOrWhiteSpace(name))
            fileSync.SetWorkspace(FileSyncService.StudentDesktopFolder(name, module));
        else if (!string.IsNullOrWhiteSpace(module))
            fileSync.SetWorkspace(Path.Combine(watchFolder, FileSyncService.SanitizeFolderName(module)));
    }

    private void TrimHandledCommands()
    {
        if (handledCommands.Count < 400) return;
        foreach (var key in handledCommands.Keys.Take(handledCommands.Count - 200))
            handledCommands.TryRemove(key, out _);
    }

    private async Task CloseCommandSocketAsync()
    {
        Volatile.Write(ref commandSocketLive, 0);
        var socket = Interlocked.Exchange(ref commandSocket, null);
        if (socket is null) return;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
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

    private async Task SendScreenAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var bytes = ScreenProvider?.Invoke();
        if (bytes is null || bytes.Length == 0) return;
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Headers.Add("X-Client-Id", clientId);
        using var response = await http.PostAsync("/screen", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task FlushClientEventsAsync(HttpClient http, CancellationToken cancellationToken)
    {
        while (clientEvents.TryPeek(out var eventName))
        {
            using var response = await http.PostAsJsonAsync("/events/trigger", new ClientEventRequest(clientId, eventName), JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
            _ = clientEvents.TryDequeue(out _);
        }
    }

    private async Task FlushQuizAnswersAsync(HttpClient http, CancellationToken cancellationToken)
    {
        while (quizAnswers.TryPeek(out var answer))
        {
            using var response = await http.PostAsJsonAsync("/quiz/answer", answer, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<QuizResult>(JsonOptions, cancellationToken);
            UpdateStateChanged?.Invoke(result is null ? "Ответ викторины отправлен." : $"{result.Message} +{result.XpAwarded} XP");
            _ = quizAnswers.TryDequeue(out _);
        }
    }

    private async Task StageUpdateAsync(HttpClient http, StudentUpdateInfo update, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "updates");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"student-{update.Version}-{Guid.NewGuid():N}.tmp");
        try
        {
            using var response = await http.GetAsync("/update/student/file", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            await using var verify = File.OpenRead(temporary);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SHA-256 обновления не совпадает с манифестом.");
            stagedUpdatePath = Path.ChangeExtension(temporary, ".exe");
            File.Move(temporary, stagedUpdatePath, true);
            UpdateStateChanged?.Invoke("Обновление проверено. Закройте Student для установки.");
        }
        catch (Exception error)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            UpdateStateChanged?.Invoke($"Обновление не установлено: {error.Message}");
        }
    }

    private void ScheduleStagedUpdate()
    {
        if (stagedUpdatePath is null || !File.Exists(stagedUpdatePath)) return;
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !Path.GetFileName(current).Contains("KIBERoneStudent", StringComparison.OrdinalIgnoreCase)) return;
        var script = Path.Combine(Path.GetDirectoryName(stagedUpdatePath)!, $"apply-update-{Guid.NewGuid():N}.cmd");
        var pid = Environment.ProcessId;
        File.WriteAllLines(script,
        [
            "@echo off",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL",
            "if not errorlevel 1 (ping 127.0.0.1 -n 2 >NUL & goto wait)",
            $"move /Y \"{stagedUpdatePath}\" \"{current}\" >NUL",
            $"start \"\" \"{current}\"",
            "del \"%~f0\""
        ]);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(script);
        Process.Start(start);
    }

    private void Raise(bool connected, string message, string? address) =>
        ConnectionChanged?.Invoke(new StudentConnectionState(connected, message, address, DateTimeOffset.UtcNow));

    private static string ResolveClientId()
    {
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback && network.OperationalStatus == OperationalStatus.Up)
            .Select(network => network.GetPhysicalAddress().ToString())
            .FirstOrDefault(value => value.Length >= 12);
        return string.IsNullOrWhiteSpace(address) ? $"host-{Environment.MachineName.ToLowerInvariant()}" : address.ToLowerInvariant();
    }

    private async Task<CommandExecutionResult?> TryHandleSoftwareCommandAsync(HttpClient http, ClassroomCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind == ClassroomCommandKinds.InstallStarterPack)
        {
            var runInstallers = !command.Payload.TryGetProperty("run_installers", out var flag) || flag.ValueKind != JsonValueKind.False;
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

    private CommandExecutionResult? TryHandleVpnCommand(ClassroomCommand command)
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
            return VpnCommandHandler(command);
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
        ScheduleStagedUpdate();
        lifetime.Dispose();
    }
}
