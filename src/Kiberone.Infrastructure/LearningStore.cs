using System.Text.Json;
using Kiberone.Core;
namespace Kiberone.Infrastructure;
public sealed class LearningStore
{
    private readonly string directory;
    private readonly ClassroomHubStore hub;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public LearningStore(string directory, ClassroomHubStore hub)
    { this.directory = directory; this.hub = hub; Directory.CreateDirectory(directory); }
    public IReadOnlyList<SharedQuiz> List(LearningAuth auth)
    {
        hub.GetAuthorized(auth.Location, auth.Password);
        lock (gate) return Directory.EnumerateFiles(directory, "quiz-*.json").Select(p => JsonSerializer.Deserialize<SharedQuiz>(File.ReadAllText(p), Json)!).OrderBy(x => x.Document.Title).ToArray();
    }
    public SharedQuiz Publish(PublishQuizRequest request)
    {
        hub.GetAuthorized(request.Location, request.Password); Validate(request.Document);
        lock (gate)
        {
            var path = Path.Combine(directory, "quiz-" + request.Document.Id.ToString("N") + ".json");
            var old = File.Exists(path) ? JsonSerializer.Deserialize<SharedQuiz>(File.ReadAllText(path), Json) : null;
            if (old is not null && !string.Equals(old.Location, request.Location.Trim(), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException();
            if ((old?.Revision ?? 0) != request.ExpectedRevision) throw new LearningConflictException();
            var next = new SharedQuiz(request.Document.Id, request.Location.Trim(), (old?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, request.Document);
            next.Document.SharedRevision = next.Revision; Write(path, next); return next;
        }
    }
    public IReadOnlyList<TypingPersonalBest> Records(TypingRecordsRequest request)
    {
        var roster = hub.GetAuthorized(request.Location, request.Password);
        if (!roster.Students.Any(x => x.Id == request.StudentId)) throw new UnauthorizedAccessException();
        if (request.Records?.Count > 2000) throw new ArgumentException("Слишком много рекордов.");
        foreach (var r in request.Records ?? [])
            if (r is null || r.LessonKey is null || r.LessonKey.Length != 64 || r.LessonKey.Any(c => !Uri.IsHexDigit(c)) || r.CorrectKeys < 100
                || !double.IsFinite(r.Speed) || r.Speed <= 0 || r.Speed > 2000
                || !double.IsFinite(r.Accuracy) || r.Accuracy < 0 || r.Accuracy > 100) throw new ArgumentException("Некорректный рекорд.");
        lock (gate)
        {
            var path = Path.Combine(directory, "records-" + request.StudentId.ToString("N") + ".json");
            var current = File.Exists(path) ? JsonSerializer.Deserialize<List<TypingPersonalBest>>(File.ReadAllText(path), Json)! : [];
            var records = current.ToDictionary(x => x.LessonKey, StringComparer.OrdinalIgnoreCase);
            foreach (var item in request.Records ?? [])
            {
                if (records.TryGetValue(item.LessonKey, out var old)) records[item.LessonKey] = item with { Speed = Math.Max(old.Speed, item.Speed), Accuracy = Math.Max(old.Accuracy, item.Accuracy), CorrectKeys = Math.Max(old.CorrectKeys, item.CorrectKeys) };
                else records[item.LessonKey] = item;
            }
            if (request.Records?.Count > 0 && !current.SequenceEqual(records.Values)) Write(path, records.Values.ToArray());
            return records.Values.ToArray();
        }
    }
    private static void Write<T>(string path, T value)
    { var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, true); }
    public static void Validate(QuizDocument d)
    {
        if (d is null || d.Id == Guid.Empty || d.Format != "kiberone-quiz" || d.Version != 1 || string.IsNullOrWhiteSpace(d.Title) || d.Title.Length > 120
            || d.TimePerQuestionSeconds is < 5 or > 300 || d.XpReward is < 0 or > 1000 || d.Questions is null || d.Questions.Count is < 1 or > 100) throw new ArgumentException("Некорректная викторина.");
        foreach (var q in d.Questions)
            if (q is null || string.IsNullOrWhiteSpace(q.Text) || q.Text.Length is < 3 or > 500 || q.Options is null || q.Options.Count is < 2 or > 6
                || q.Options.Any(o => string.IsNullOrWhiteSpace(o) || o.Length > 500) || q.ResolvedCorrectIndices().Count == 0
                || (q.CorrectIndices?.Any(i => i < 0 || i >= q.Options.Count) ?? false)) throw new ArgumentException("Проверьте вопросы и правильные ответы.");
    }
}
public sealed class LearningConflictException : Exception;
