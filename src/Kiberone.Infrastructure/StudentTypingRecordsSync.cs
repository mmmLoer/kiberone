using System.Net.Http.Json;
using System.Text.Json;
using Kiberone.Core;
namespace Kiberone.Infrastructure;
public sealed partial class StudentAgent
{
    private DateTimeOffset nextRecordsAt;
    public event Action<Guid, IReadOnlyList<TypingPersonalBest>>? TypingRecordsAvailable;
    private async Task SyncPersonalRecordsAsync(HttpClient http, CancellationToken ct, bool gateHeld = false)
    {
        if (studentId is not Guid owner || DateTimeOffset.UtcNow < nextRecordsAt) return;
        nextRecordsAt = DateTimeOffset.UtcNow.AddSeconds(15);
        if (!gateHeld) await sessionGate.WaitAsync(ct);
        try
        {
            if (studentId != owner) return;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "typing-records", owner.ToString("N"));
            var records = new List<TypingPersonalBest>();
            if (Directory.Exists(directory)) foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var key = Path.GetFileNameWithoutExtension(file);
                if (key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c))) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<StoredBest>(await File.ReadAllTextAsync(file, ct), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (record is not null && record.Speed is > 0 and <= 2000 && record.Accuracy is >= 0 and <= 100) records.Add(new TypingPersonalBest(key, record.Speed, record.Accuracy, Math.Max(100, record.CorrectKeys)));
                }
                catch (JsonException) { }
            }
            using var response = await http.PostAsJsonAsync("/typing/records", new SaveTypingRecordsRequest(records), JsonOptions, ct);
            response.EnsureSuccessStatusCode();
            var merged = await response.Content.ReadFromJsonAsync<List<TypingPersonalBest>>(JsonOptions, ct) ?? [];
            if (studentId == owner) TypingRecordsAvailable?.Invoke(owner, merged);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
        { System.Diagnostics.Trace.TraceWarning($"Typing records sync deferred: {error.GetType().Name}"); }
        finally { if (!gateHeld) sessionGate.Release(); }
    }
    private sealed record StoredBest(double Speed, double Accuracy, int CorrectKeys = 100);
}
