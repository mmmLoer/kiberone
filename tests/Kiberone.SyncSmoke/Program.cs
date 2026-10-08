using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;

if (args.Length > 0)
{
    await NetworkSmoke.RunAsync(args);
    return;
}

// This executable runs the production Student sync client and Tutor server in an
// isolated directory on a Windows VM. It does not start the GUI or VPN services.
var root = Path.Combine(Path.GetTempPath(), "kiberone-vm-sync-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<string>();
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var ct = deadline.Token;
    var options = ClassroomDatabase.CreateOptions(Path.Combine(root, "classroom.db"));
    await ClassroomDatabase.InitializeAsync(options, ct);
    var classroom = new ClassroomService(options);
    var group = await classroom.CreateGroupAsync(new GroupDraft("VM smoke", "Python", "", "VM TEST"), ct);
    var student = await classroom.CreateStudentAsync(new StudentDraft("Проверка", "Виртуалка", 10, group.Id, "", "", ""), ct);
    var secondStudent = await classroom.CreateStudentAsync(new StudentDraft("Другой", "Ученик", 10, group.Id, "", "", ""), ct);
    var sync = new FileSyncService(options, Path.Combine(root, "tutor-files"));
    var registry = new ClientRegistry();
    var commands = new ReliableCommandQueue(registry);
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    using var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    await using var server = new ClassroomServer(new ClassroomServerOptions(token, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), port),
        new TypingLessonService(options), classroom, sync, new AssetDistributionService(root, Path.Combine(root, "assets")),
        new QuizService(options, registry, commands), new AuditService(options), registry, commands);
    await server.StartAsync(ct);
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    var clientId = "vm-" + Guid.NewGuid().ToString("N");
    var workspace = Path.Combine(root, "student-files");
    var client = new StudentFileSyncClient(clientId, workspace) { StudentId = student.Id };
    using var http = Http(clientId);
    await Register(http, clientId, student.Id, workspace);
    var serverFolder = sync.GetClientFolderPath(clientId);
    var script = "проект/main.py";
    var note = "проект/заметки.txt";
    var blob = "assets/data.bin";
    var binary = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
    await Write(script, "print('Версия 1')\n");
    await Write(note, "Первая заметка\n");
    Directory.CreateDirectory(Path.Combine(workspace, "assets"));
    await File.WriteAllBytesAsync(Local(blob), binary, ct);
    await Write("node_modules/ignored.txt", "not a project file");
    await Write(".git/ignored.txt", "not a project file");
    await Cycle(client, http, clientId);
    Check(await File.ReadAllTextAsync(Remote(script), ct) == "print('Версия 1')\n", "UTF-8 nested file upload");
    Check((await File.ReadAllBytesAsync(Remote(blob), ct)).SequenceEqual(binary), "Binary file upload (4096 bytes)");
    Check(!File.Exists(Remote("node_modules/ignored.txt")) && !File.Exists(Remote(".git/ignored.txt")), "Generated folders excluded");
    var initial = (await sync.GetGitHistoryAsync(clientId, ct)).First();
    var initialCount = (await sync.GetGitHistoryAsync(clientId, ct)).Count;
    await Cycle(client, http, clientId);
    Check((await sync.GetGitHistoryAsync(clientId, ct)).Count == initialCount, "Unchanged sync creates no redundant Git commit");

    await Write(script, "print('Версия 2')\n");
    File.Delete(Local(note));
    await Write("new.txt", "new file");
    await Cycle(client, http, clientId);
    var modified = (await sync.GetGitHistoryAsync(clientId, ct)).First();
    Check(modified.Sha != initial.Sha && !File.Exists(Remote(note)), "Modification and deletion committed");
    Check((await sync.GetGitDiffAsync(clientId, modified.Sha, ct)).Contains("Версия 2"), "Git diff contains actual edit");
    await sync.ChangeGitProjectAsync(clientId, "restore", initial.Sha, ct);
    await Cycle(client, http, clientId);
    Check(await File.ReadAllTextAsync(Local(script), ct) == "print('Версия 1')\n"
        && File.Exists(Local(note)) && !File.Exists(Local("new.txt")), "Git restore is delivered to Student, including deletions");
    Check((await File.ReadAllBytesAsync(Local(blob), ct)).SequenceEqual(binary), "Binary content survives Git restore");

    await sync.CreateGitBranchAsync(clientId, "experiment", ct);
    await sync.ChangeGitProjectAsync(clientId, "checkout", "experiment", ct);
    await Cycle(client, http, clientId);
    await Write("branch.txt", "experiment work");
    await Cycle(client, http, clientId);
    await sync.ChangeGitProjectAsync(clientId, "checkout", "main", ct);
    await Cycle(client, http, clientId);
    Check(!File.Exists(Local("branch.txt")), "Switching branch removes branch-only file on Student");
    await sync.ChangeGitProjectAsync(clientId, "merge", "experiment", ct);
    await Cycle(client, http, clientId);
    Check(await File.ReadAllTextAsync(Local("branch.txt"), ct) == "experiment work", "Merged branch is delivered to Student");

    await sync.CreateGitBranchAsync(clientId, "conflict", ct);
    await sync.ChangeGitProjectAsync(clientId, "checkout", "conflict", ct);
    await Cycle(client, http, clientId);
    await Write(script, "print('branch change')\n");
    await Cycle(client, http, clientId);
    await sync.ChangeGitProjectAsync(clientId, "checkout", "main", ct);
    await Cycle(client, http, clientId);
    await Write(script, "print('main change')\n");
    await Cycle(client, http, clientId);
    var refused = false;
    try { await sync.ChangeGitProjectAsync(clientId, "merge", "conflict", ct); }
    catch (InvalidOperationException) { refused = true; }
    Check(refused && await File.ReadAllTextAsync(Remote(script), ct) == "print('main change')\n", "Conflicting merge preserves current files");

    await Write("offline.txt", "unsent work");
    var checkpoint = await client.SaveSessionCheckpointAsync(student.Id, Path.Combine(root, "checkpoints"), ct);
    Check(checkpoint is not null && await File.ReadAllTextAsync(Path.Combine(checkpoint, "files", "offline.txt"), ct) == "unsent work", "Offline logout checkpoint preserves unsent work");
    await Cycle(client, http, clientId);
    var restarted = new StudentFileSyncClient(clientId, workspace) { StudentId = student.Id };
    var beforeRestart = (await sync.GetGitHistoryAsync(clientId, ct)).Count;
    await Cycle(restarted, http, clientId);
    Check((await sync.GetGitHistoryAsync(clientId, ct)).Count == beforeRestart, "Restart reuses cache without redundant changes");

    var otherPcId = "vm-second-" + Guid.NewGuid().ToString("N");
    var otherPc = new StudentFileSyncClient(otherPcId, Path.Combine(root, "second-pc")) { StudentId = student.Id };
    using var otherHttp = Http(otherPcId);
    await Register(otherHttp, otherPcId, student.Id, otherPc.WatchFolder);
    await Cycle(otherPc, otherHttp, otherPcId);
    Check(await File.ReadAllTextAsync(Path.Combine(otherPc.WatchFolder, "offline.txt"), ct) == "unsent work", "Same pupil recovers project on a different PC");

    var strangerId = "vm-stranger-" + Guid.NewGuid().ToString("N");
    var stranger = new StudentFileSyncClient(strangerId, Path.Combine(root, "other-student")) { StudentId = secondStudent.Id };
    using var strangerHttp = Http(strangerId);
    await Register(strangerHttp, strangerId, secondStudent.Id, stranger.WatchFolder);
    await Cycle(stranger, strangerHttp, strangerId);
    Check(!Directory.EnumerateFiles(stranger.WatchFolder, "*", SearchOption.AllDirectories).Any(), "Different pupil does not receive another pupil's files");

    var report = new { Passed = results.Count, Machine = Environment.MachineName, Root = root, Checks = results, InitialCommit = initial.Sha, ModifiedCommit = modified.Sha };
    await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
    Console.WriteLine("SMOKE_PASS " + JsonSerializer.Serialize(report));

    string Local(string path) => Path.Combine(workspace, path.Replace('/', Path.DirectorySeparatorChar));
    string Remote(string path) => Path.Combine(serverFolder, path.Replace('/', Path.DirectorySeparatorChar));
    async Task Write(string path, string text)
    { Directory.CreateDirectory(Path.GetDirectoryName(Local(path))!); await File.WriteAllTextAsync(Local(path), text, ct); }
    void Check(bool success, string label)
    { if (!success) throw new InvalidOperationException(label); results.Add(label); Console.WriteLine("PASS " + label); }
    HttpClient Http(string id)
    {
        var connection = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
        connection.DefaultRequestHeaders.Add("X-Sync-Token", token);
        connection.DefaultRequestHeaders.Add("X-Client-Id", id);
        connection.DefaultRequestHeaders.Add("X-Client-Secret", new DeviceCredentials(Path.Combine(root, "secrets")).GetOrCreateSecret(id));
        return connection;
    }
    async Task Register(HttpClient connection, string id, Guid owner, string folder)
    {
        using var response = await connection.PostAsJsonAsync("/heartbeat", new HeartbeatRequest(id, "VM", Environment.MachineName, folder, BuildInfo.Version,
            owner, null, new ClientRuntimeInfo(false, false, "", null)), json, ct);
        response.EnsureSuccessStatusCode();
    }
    async Task Cycle(StudentFileSyncClient pupil, HttpClient connection, string id)
    {
        for (var i = 0; i < 8; i++)
        {
            await pupil.SyncOnceAsync(connection, ct);
            var approval = await sync.GetApprovalAsync(id, ct);
            if (approval?.Status == SyncApprovalStatus.Completed) return;
            if (approval?.Status == SyncApprovalStatus.Pending) await sync.DecideAsync(approval.Id, "update", ct);
        }
        throw new InvalidOperationException("Sync did not complete: " + id);
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("SMOKE_FAIL " + root + "\n" + error);
    await File.WriteAllTextAsync(Path.Combine(root, "failure.txt"), error.ToString());
    Environment.ExitCode = 1;
}
