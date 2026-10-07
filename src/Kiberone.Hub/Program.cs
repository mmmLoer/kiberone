using System.Text.Json;
using Kiberone.Infrastructure;

var dataDirectory = Environment.GetEnvironmentVariable("KIBERONE_HUB_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "kiberone-hub");
var secretsPath = Environment.GetEnvironmentVariable("KIBERONE_HUB_SECRETS")
    ?? Path.Combine(dataDirectory, "location-secrets.json");
Directory.CreateDirectory(dataDirectory);

if (!File.Exists(secretsPath))
    throw new FileNotFoundException("Не найден файл паролей локаций.", secretsPath);

var secrets = JsonSerializer.Deserialize<List<LocationSecretRecord>>(File.ReadAllText(secretsPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
    ?? throw new InvalidOperationException("Пустой файл паролей локаций.");
var store = new ClassroomHubStore(dataDirectory, secrets);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("KIBERONE_HUB_URL") ?? "http://0.0.0.0:8787");
var app = builder.Build();
var mailboxes = new StudentMailboxStore(dataDirectory, Environment.GetEnvironmentVariable("KIBERONE_MAILDIR_ROOT"));
var provisionScript = Environment.GetEnvironmentVariable("KIBERONE_MAIL_PROVISION_SCRIPT");
if (!string.IsNullOrWhiteSpace(provisionScript))
    mailboxes.ProvisionMailbox = username =>
    {
        var info = new System.Diagnostics.ProcessStartInfo(provisionScript) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(username);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("Не удалось создать ящик.");
        if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Создание ящика заняло слишком много времени."); }
        if (process.ExitCode != 0) throw new IOException("Почтовый сервер не создал ящик.");
    };
ClassroomHubApi.Map(app, store, mailboxes);
GithubPushReleaseHook.Map(app);
app.Run();
