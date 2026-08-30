using System.Net.Http.Json;
using System.Net.NetworkInformation;
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
    private Task? loopTask;

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
    public Func<ClassroomCommand, CommandExecutionResult>? VpnCommandHandler { get; set; }
    public Func<bool>? VpnStateProvider { get; set; }
    public event Action<StudentConnectionState>? ConnectionChanged;
    public event Action<ClassroomCommand>? CommandReceived;
    public event Action<StudentSyncState>? SyncStateChanged;
    public event Action<StudentUpdateInfo>? UpdateAvailable;
    public event Action<string>? UpdateStateChanged;
    public event Action<IReadOnlyList<StudentSummary>>? StudentsAvailable;

    public void RequestUpdateInstallation()
    {
        updateRequested = true;
        UpdateStateChanged?.Invoke(availableUpdate is null ? "Ожидаем информацию об обновлении…" : "Скачиваем обновление…");
    }

    public void QueueClientEvent(string eventName) => clientEvents.Enqueue(eventName);
    public void SubmitQuizAnswer(Guid sessionId, int selectedIndex) => quizAnswers.Enqueue(new SubmitQuizAnswerRequest(sessionId, clientId, selectedIndex));
    public void AssignStudent(Guid id) => studentId = id;

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
                Raise(false, "Тьютор не найден. Повторяем поиск…", null);
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
            Raise(true, "Подключено к тьютору", address);
            var consecutiveFailures = 0;
            var rosterLoaded = false;
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
                    Raise(false, $"Связь нестабильна: {error.Message}", address);
                }
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }

    private async Task SendHeartbeatAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var heartbeat = new HeartbeatRequest(
            clientId,
            pcNumber,
            Environment.MachineName,
            watchFolder,
            BuildInfo.Version,
            studentId,
            null,
            new ClientRuntimeInfo(
                WatchdogStateProvider?.Invoke() ?? false,
                FocusModeStateProvider?.Invoke() ?? false,
                string.Empty,
                null,
                VpnStateProvider?.Invoke() ?? false));
        using var response = await http.PostAsJsonAsync("/heartbeat", heartbeat, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var settings = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions, cancellationToken);
        if (settings is not null)
        {
            syncSeconds = Math.Clamp(settings.SyncSeconds, 15, 3600);
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
        {
            CommandReceived?.Invoke(command);
            CommandExecutionResult result;
            try
            {
                if (command.Kind == ClassroomCommandKinds.SyncNow) nextSyncAt = DateTimeOffset.MinValue;
                if (command.Kind == ClassroomCommandKinds.Configure && command.Payload.TryGetProperty("sync_seconds", out var seconds) && seconds.TryGetInt32(out var configured))
                    syncSeconds = Math.Clamp(configured, 15, 3600);
                result = TryHandleVpnCommand(command)
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
    }

    private async Task LoadRosterAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var students = await http.GetFromJsonAsync<List<StudentSummary>>("/students", JsonOptions, cancellationToken) ?? [];
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
        if (loopTask is not null)
        {
            try { await loopTask; } catch (OperationCanceledException) { }
        }
        ScheduleStagedUpdate();
        lifetime.Dispose();
    }
}
