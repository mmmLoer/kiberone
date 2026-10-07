using System.Diagnostics;
using System.Text.Json;
using Kiberone.Infrastructure;

var data = Environment.GetEnvironmentVariable("KIBERONE_HUB_DATA") ?? "/var/lib/kiberone-hub";
var secretsPath = Environment.GetEnvironmentVariable("KIBERONE_HUB_SECRETS") ?? Path.Combine(data, "location-secrets.json");
var secrets = JsonSerializer.Deserialize<List<LocationSecretRecord>>(File.ReadAllText(secretsPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))
    ?? throw new InvalidOperationException("Не настроены пароли локаций.");
var store = new ClassroomHubStore(data, secrets);
var mailboxes = new StudentMailboxStore(data, Environment.GetEnvironmentVariable("KIBERONE_MAILDIR_ROOT"));
var script = Environment.GetEnvironmentVariable("KIBERONE_MAIL_PROVISION_SCRIPT")
    ?? throw new InvalidOperationException("Не настроено создание ящиков.");
mailboxes.ProvisionAlias = (username, alias) =>
{
    var info = new ProcessStartInfo(script) { UseShellExecute = false, CreateNoWindow = true };
    info.ArgumentList.Add(username);
    info.ArgumentList.Add(alias);
    using var process = Process.Start(info) ?? throw new IOException("Не удалось создать ящик.");
    if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Создание ящика заняло слишком много времени."); }
    if (process.ExitCode == 3) throw new MailboxAliasUnavailableException();
    if (process.ExitCode != 0) throw new IOException("Почтовый сервер не создал ящик.");
};
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("KIBERONE_MAIL_API_LISTEN") ?? "http://127.0.0.1:8790");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
var app = builder.Build();
StudentMailApi.Map(app, store, mailboxes);
var cleanup = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    do
    {
        try { mailboxes.PurgeExpired(); }
        catch (Exception error) { app.Logger.LogError(error, "Mailbox retention cleanup failed"); }
    } while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping));
});
app.Run();
