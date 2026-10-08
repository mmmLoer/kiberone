using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

// Run server and client modes on separate test VMs. All data and credentials
// are isolated; this harness never invokes VPN or native desktop commands.
internal static class NetworkSmoke
{
    private const string Token = "isolated-vm-smoke-sync-token-20261008";
    private const string TutorToken = "isolated-vm-smoke-tutor-token-20261008";
    private const int Port = 18765;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static async Task RunAsync(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-network-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            if (args[0] == "server") await Server(root, deadline.Token);
            else if (args[0] == "client" && args.Length == 2) await Client(root, args[1], deadline.Token);
            else throw new ArgumentException("Use server or client <server IPv4>.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("NETWORK_FAIL " + error);
            Environment.ExitCode = 1;
        }
    }

    private static async Task Server(string root, CancellationToken ct)
    {
        var options = ClassroomDatabase.CreateOptions(Path.Combine(root, "classroom.db"));
        await ClassroomDatabase.InitializeAsync(options, ct);
        var classroom = new ClassroomService(options);
        await classroom.CreateGroupAsync(new GroupDraft("Network VM", "Python", "", "VM TEST"), ct);
        var sync = new FileSyncService(options, Path.Combine(root, "files"));
        var registry = new ClientRegistry();
        var commands = new ReliableCommandQueue(registry);
        await using var server = new ClassroomServer(new ClassroomServerOptions(Token, TutorToken, Port),
            new TypingLessonService(options), classroom, sync, new AssetDistributionService(root, Path.Combine(root, "assets")),
            new QuizService(options, registry, commands), new AuditService(options), registry, commands);
        await server.StartAsync(ct);
        Console.WriteLine("NETWORK_READY " + Environment.MachineName);
        while (!ct.IsCancellationRequested)
        {
            foreach (var approval in await sync.ListPendingApprovalsAsync(ct))
                await sync.DecideAsync(approval.Id, "update", ct);
            if (registry.Contains("vm-network-finished"))
            {
                Console.WriteLine("NETWORK_SERVER_PASS clients=" + registry.GetAll().Count);
                return;
            }
            await Task.Delay(150, ct);
        }
    }

    private static async Task Client(string root, string address, CancellationToken ct)
    {
        var checks = new List<string>();
        void Check(bool passed, string label)
        { if (!passed) throw new InvalidOperationException(label); checks.Add(label); Console.WriteLine("PASS " + label); }
        HttpClient Http(string id, bool tutor = false)
        {
            var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            { BaseAddress = new Uri($"http://{address}:{Port}"), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("X-Sync-Token", Token);
            http.DefaultRequestHeaders.Add("X-Client-Id", id);
            http.DefaultRequestHeaders.Add("X-Client-Secret", new DeviceCredentials(Path.Combine(root, "secrets")).GetOrCreateSecret(id));
            if (tutor) { http.DefaultRequestHeaders.Add("X-Tutor", "1"); http.DefaultRequestHeaders.Add("X-Tutor-Token", TutorToken); }
            return http;
        }
        async Task Register(HttpClient http, string id, Guid? owner = null)
        {
            using var response = await http.PostAsJsonAsync("/heartbeat", new HeartbeatRequest(id, "VM", Environment.MachineName,
                root, BuildInfo.Version, owner, null, new ClientRuntimeInfo(false, false, "", null)), Json, ct);
            response.EnsureSuccessStatusCode();
        }
        using var tutor = Http("vm-test-tutor", true);
        using var pupil = Http("vm-network-student");
        using var bad = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = pupil.BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
        using var unauthorized = await bad.GetAsync("/groups", ct);
        Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Unauthenticated request rejected");
        var groups = await pupil.GetFromJsonAsync<List<ClassroomGroup>>("/groups", Json, ct);
        Check(groups?.Single().Name == "Network VM", "Group catalogue received through HTTP");
        using var created = await tutor.PostAsJsonAsync("/students", new StudentDraft("Сеть", "Ученик", 10, groups![0].Id, "", "", ""), Json, ct);
        created.EnsureSuccessStatusCode();
        var student = await created.Content.ReadFromJsonAsync<Student>(Json, ct) ?? throw new InvalidOperationException("No student");
        await Register(pupil, "vm-network-student", student.Id);
        using var websocket = new ClientWebSocket();
        websocket.Options.SetRequestHeader("X-Sync-Token", Token);
        websocket.Options.SetRequestHeader("X-Client-Id", "vm-network-student");
        websocket.Options.SetRequestHeader("X-Client-Secret", new DeviceCredentials(Path.Combine(root, "secrets")).GetOrCreateSecret("vm-network-student"));
        await websocket.ConnectAsync(new Uri($"ws://{address}:{Port}/ws?client_id=vm-network-student"), ct);
        using var enqueue = await tutor.PostAsJsonAsync("/command", new EnqueueCommandRequest(["vm-network-student"],
            ClassroomCommandKinds.Notification, JsonSerializer.SerializeToElement(new { message = "VM network" })), Json, ct);
        enqueue.EnsureSuccessStatusCode();
        var command = await enqueue.Content.ReadFromJsonAsync<ClassroomCommand>(Json, ct) ?? throw new InvalidOperationException("No command");
        var buffer = new byte[16384];
        var received = await websocket.ReceiveAsync(buffer, ct);
        var pushed = JsonSerializer.Deserialize<ClassroomCommand>(buffer.AsSpan(0, received.Count), Json);
        Check(pushed?.Id == command.Id, "WebSocket command delivered");
        websocket.Abort();
        var pending = await pupil.GetFromJsonAsync<List<ClassroomCommand>>("/commands?client_id=vm-network-student", Json, ct);
        Check(pending?.Single().Id == command.Id, "HTTP fallback preserves command after WebSocket disconnect");
        using var ack = await pupil.PostAsJsonAsync($"/commands/{command.Id}/ack?client_id=vm-network-student", new CommandAcknowledgement(command.Id, true), Json, ct);
        ack.EnsureSuccessStatusCode();
        Check((await pupil.GetFromJsonAsync<List<ClassroomCommand>>("/commands?client_id=vm-network-student", Json, ct))?.Count == 0, "Acknowledged command is removed");
        var folder = Path.Combine(root, "workspace");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "проект.py"), "print('две виртуалки')", ct);
        var client = new StudentFileSyncClient("vm-network-student", folder) { StudentId = student.Id };
        for (var i = 0; i < 12; i++) { await client.SyncOnceAsync(pupil, ct); await Task.Delay(200, ct); }
        var recoveredFolder = Path.Combine(root, "recovered");
        var recovered = new StudentFileSyncClient("vm-network-other-pc", recoveredFolder) { StudentId = student.Id };
        using var second = Http("vm-network-other-pc");
        await Register(second, "vm-network-other-pc", student.Id);
        for (var i = 0; i < 12; i++) { await recovered.SyncOnceAsync(second, ct); await Task.Delay(200, ct); }
        Check(File.Exists(Path.Combine(recoveredFolder, "проект.py")) && await File.ReadAllTextAsync(Path.Combine(recoveredFolder, "проект.py"), ct) == "print('две виртуалки')", "Project upload and recovery through remote Tutor");
        await Parallel.ForEachAsync(Enumerable.Range(0, 160), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (i, _) =>
        { using var simulated = Http("vm-load-" + i); await Register(simulated, "vm-load-" + i); });
        using var clientsResponse = await tutor.GetAsync("/clients", ct);
        clientsResponse.EnsureSuccessStatusCode();
        using var clients = JsonDocument.Parse(await clientsResponse.Content.ReadAsStringAsync(ct));
        Check(clients.RootElement.GetArrayLength() >= 162, "160 distinct simulated clients registered through HTTP");
        await Register(pupil, "vm-network-finished");
        var report = new { Machine = Environment.MachineName, Server = address, Passed = checks.Count, Checks = checks };
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report), ct);
        Console.WriteLine("NETWORK_PASS " + JsonSerializer.Serialize(report));
    }
}
