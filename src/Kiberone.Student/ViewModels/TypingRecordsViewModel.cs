using Kiberone.Core;
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
        var hash = TypingRecordKey.For(LessonName, TargetText);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "typing-records", SelectedStudent.Id.ToString("N"));
        var path = Path.Combine(directory, hash + ".json");
        if (!File.Exists(path))
        {
            var oldHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((activeLessonId?.ToString("N") ?? LessonName) + "\n" + TargetText)));
            var old = Path.Combine(directory, oldHash + ".json");
            try { if (File.Exists(old)) File.Copy(old, path, false); } catch (IOException) { }
        }
        return path;
    }
    public void MergePersonalRecords(Guid owner, IReadOnlyList<TypingPersonalBest> records)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "typing-records", owner.ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var item in records)
        {
            if (item.LessonKey.Length != 64 || item.LessonKey.Any(c => !Uri.IsHexDigit(c))) continue;
            var path = Path.Combine(directory, item.LessonKey + ".json");
            try
            {
                var old = File.Exists(path) ? JsonSerializer.Deserialize<TypingPersonalRecord>(File.ReadAllText(path)) : null;
                var merged = new TypingPersonalRecord(Math.Max(old?.Speed ?? 0, item.Speed), Math.Max(old?.Accuracy ?? 0, item.Accuracy), Math.Max(old?.CorrectKeys ?? 100, item.CorrectKeys));
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(merged)); File.Move(path + ".tmp", path, true);
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"Typing record merge failed: {error.GetType().Name}"); }
        }
        if (!IsLoginVisible && SelectedStudent?.Id == owner) { recordKey = null; UpdatePersonalRecords(); }
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
            var next = new TypingPersonalRecord(Math.Max(personalRecord?.Speed ?? 0, Cpm), Math.Max(personalRecord?.Accuracy ?? 0, Accuracy), 100);
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
public sealed record TypingPersonalRecord(double Speed, double Accuracy, int CorrectKeys = 100);
