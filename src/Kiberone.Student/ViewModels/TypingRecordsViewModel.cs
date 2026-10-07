using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kiberone.Student.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private string personalSpeedRecord = "Рекорд: —";
    [ObservableProperty] private string personalAccuracyRecord = "Рекорд: —";
    private string? recordKey;
    private TypingPersonalRecord? personalRecord;
    private string? RecordPath()
    {
        if (IsLoginVisible || SelectedStudent is null || string.IsNullOrEmpty(TargetText)) return null;
        var lesson = (activeLessonId?.ToString("N") ?? LessonName) + "\n" + TargetText;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lesson)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "typing-records", SelectedStudent.Id.ToString("N"), hash + ".json");
    }
    private void UpdatePersonalRecords()
    {
        var path = RecordPath();
        if (path != recordKey)
        {
            recordKey = path; personalRecord = null;
            try { if (path is not null && File.Exists(path)) personalRecord = JsonSerializer.Deserialize<TypingPersonalRecord>(File.ReadAllText(path)); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"Typing record read failed: {error.GetType().Name}"); }
        }
        if (path is not null && IsLessonStarted && CorrectKeys >= 100 && activeTime.Elapsed.TotalSeconds > 0)
        {
            var next = new TypingPersonalRecord(Math.Max(personalRecord?.Speed ?? 0, Cpm), Math.Max(personalRecord?.Accuracy ?? 0, Accuracy));
            if (next != personalRecord)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(next));
                    File.Move(path + ".tmp", path, true);
                    personalRecord = next;
                }
                catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"Typing record save failed: {error.GetType().Name}"); }
            }
        }
        PersonalSpeedRecord = personalRecord is null ? "Рекорд: —" : $"Рекорд: {personalRecord.Speed:0} зн/мин";
        PersonalAccuracyRecord = personalRecord is null ? "Рекорд: —" : $"Рекорд: {personalRecord.Accuracy:0.#}%";
    }
}
public sealed record TypingPersonalRecord(double Speed, double Accuracy);
