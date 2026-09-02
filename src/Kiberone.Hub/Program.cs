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
ClassroomHubApi.Map(app, store);
app.Run();
