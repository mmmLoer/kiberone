using System.Collections.Concurrent;
using System.Text.Json;
using Kiberone.Core;
using Microsoft.EntityFrameworkCore;
namespace Kiberone.Infrastructure;
public sealed partial class QuizService
{
    private readonly ConcurrentDictionary<Guid, QuizRun> runs = new();
    private readonly SemaphoreSlim sequenceGate = new(1, 1);
    private CancellationTokenSource? sequenceLifetime;
    private Task? sequenceTask;
    private string RunDirectory
    {
        get
        {
            using var db = new ClassroomDbContext(options);
            var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
            return Path.GetFullPath(path) + ".quiz-runs";
        }
    }
    public async Task<QuizRun> StartDocumentAsync(QuizDocument document, IReadOnlyList<string> targetIds, CancellationToken ct = default)
    {
        LearningStore.Validate(document);
        var targets = clients.GetAll().Where(c => c.IsOnline && c.StudentId is not null && (targetIds.Contains("__all__") || targetIds.Contains(c.ClientId))).ToDictionary(c => c.ClientId, c => c.StudentId!.Value);
        if (targets.Count == 0) throw new LessonValidationException(["Нет подключённых учеников."]);
        await sequenceGate.WaitAsync(ct);
        try
        {
            if (runs.Values.Any(r => r.Targets.Keys.Intersect(targets.Keys).Any())) throw new LessonValidationException(["У этих учеников уже идёт викторина."]);
            var run = new QuizRun { Id = Guid.NewGuid(), Document = JsonSerializer.Deserialize<QuizDocument>(JsonSerializer.Serialize(document))!, Targets = targets };
            await LaunchQuestionAsync(run, ct); runs[run.Id] = run; SaveRun(run);
            return run;
        }
        finally { sequenceGate.Release(); }
    }
    public void EnableSequences()
    {
        if (sequenceTask is not null) return;
        Directory.CreateDirectory(RunDirectory);
        foreach (var file in Directory.EnumerateFiles(RunDirectory, "*.json"))
            try { var run = JsonSerializer.Deserialize<QuizRun>(File.ReadAllText(file)); if (run is not null) { run.NeedsReplay = true; runs[run.Id] = run; } } catch (Exception e) { System.Diagnostics.Trace.TraceError($"Quiz recovery failed: {e.GetType().Name}"); }
        sequenceLifetime = new CancellationTokenSource();
        sequenceTask = Task.Run(async () =>
        {
            while (!sequenceLifetime.IsCancellationRequested)
            {
                try { await TickSequencesAsync(sequenceLifetime.Token); await Task.Delay(1000, sequenceLifetime.Token); }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { System.Diagnostics.Trace.TraceError($"Quiz sequence failed: {e.GetType().Name}"); await Task.Delay(1000); }
            }
        });
    }
    public async Task StopSequencesAsync()
    { sequenceLifetime?.Cancel(); if (sequenceTask is not null) await sequenceTask; sequenceTask = null; sequenceLifetime?.Dispose(); sequenceLifetime = null; }
    public async Task TickSequencesAsync(CancellationToken ct = default)
    {
        await sequenceGate.WaitAsync(ct);
        try
        {
            foreach (var run in runs.Values.ToArray())
            {
                if (run.NeedsReplay)
                {
                    if (run.Deadline > DateTimeOffset.UtcNow && !run.Targets.Any(t => clients.GetAll().Any(c => c.ClientId == t.Key && c.StudentId == t.Value))) continue;
                    if (run.Deadline > DateTimeOffset.UtcNow) await ReplayQuestionAsync(run, ct);
                    run.NeedsReplay = false;
                }
                var answers = await GetAnswersAsync(run.SessionId, ct);
                if (run.Deadline > DateTimeOffset.UtcNow && !run.Targets.Keys.All(id => answers.Any(a => a.ClientId == id))) continue;
                await using (var db = new ClassroomDbContext(options))
                { var session = await db.QuizSessions.FindAsync([run.SessionId], ct); if (session is not null) session.IsActive = false; await db.SaveChangesAsync(ct); }
                var nextIndex = run.Index + 1;
                if (nextIndex >= run.Document.Questions.Count || !run.Targets.Any(t => clients.GetAll().Any(c => c.ClientId == t.Key && c.StudentId == t.Value)))
                {
                    runs.TryRemove(run.Id, out _); File.Delete(Path.Combine(RunDirectory, run.Id.ToString("N") + ".json"));
                    var current = run.Targets.Where(t => clients.GetAll().Any(c => c.ClientId == t.Key && c.StudentId == t.Value)).Select(t => t.Key).ToArray();
                    if (current.Length > 0) commands.Enqueue(new EnqueueCommandRequest(current, ClassroomCommandKinds.Notification, JsonSerializer.SerializeToElement(new { message = "Викторина завершена! Спасибо за участие.", quiz_finished = true }), 30));
                }
                else { var previousIndex = run.Index; run.Index = nextIndex; try { await LaunchQuestionAsync(run, ct); SaveRun(run); } catch { run.Index = previousIndex; throw; } }
            }
        }
        finally { sequenceGate.Release(); }
    }
    private async Task LaunchQuestionAsync(QuizRun run, CancellationToken ct)
    {
        var q = run.Document.Questions[run.Index];
        var targets = run.Targets.Where(t => clients.GetAll().Any(c => c.ClientId == t.Key && c.StudentId == t.Value)).Select(t => t.Key).ToArray();
        if (targets.Length == 0) throw new LessonValidationException(["Ученики отключились или сменили имя."]);
        var session = await StartAsync(new StartQuizRequest(q.Text, q.Options, q.CorrectIndex, run.Document.XpReward, targets, run.Document.TimePerQuestionSeconds, run.Document.ShuffleAnswers, run.Document.ShowFeedback, q.CorrectIndices, run.Index + 1, run.Document.Questions.Count), ct);
        run.SessionId = session.Id; run.Deadline = DateTimeOffset.UtcNow.AddSeconds(run.Document.TimePerQuestionSeconds);
    }
    private async Task ReplayQuestionAsync(QuizRun run, CancellationToken ct)
    {
        await using var db = new ClassroomDbContext(options);
        var session = await db.QuizSessions.FindAsync([run.SessionId], ct);
        if (session is null) { await LaunchQuestionAsync(run, ct); return; }
        var answered = await GetAnswersAsync(run.SessionId, ct);
        var targets = run.Targets.Where(t => clients.GetAll().Any(c => c.ClientId == t.Key && c.StudentId == t.Value)).Select(t => t.Key).Where(id => !answered.Any(a => a.ClientId == id)).ToArray();
        if (targets.Length == 0) return;
        commands.Enqueue(new EnqueueCommandRequest(targets, ClassroomCommandKinds.QuizStart, JsonSerializer.SerializeToElement(new { session_id = session.Id, question = session.Question, options = JsonSerializer.Deserialize<List<string>>(session.OptionsJson), allow_multiple = session.IsMultiple, question_number = run.Index + 1, question_count = run.Document.Questions.Count, time_limit_seconds = Math.Max(1, (int)(run.Deadline - DateTimeOffset.UtcNow).TotalSeconds) }), Math.Max(10, (int)(run.Deadline - DateTimeOffset.UtcNow).TotalSeconds)));
    }
    private void SaveRun(QuizRun run)
    { Directory.CreateDirectory(RunDirectory); var path = Path.Combine(RunDirectory, run.Id.ToString("N") + ".json"); File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(run)); File.Move(path + ".tmp", path, true); }
    private void ValidateSequenceAnswer(SubmitQuizAnswerRequest request)
    {
        var run = runs.Values.FirstOrDefault(r => r.SessionId == request.SessionId);
        if (run is null) return;
        if (!run.Targets.TryGetValue(request.ClientId, out var owner) || !clients.GetAll().Any(c => c.ClientId == request.ClientId && c.StudentId == owner) || DateTimeOffset.UtcNow >= run.Deadline)
            throw new LessonValidationException(["Время ответа истекло или ученик сменился."]);
    }
}
public sealed class QuizRun
{
    public Guid Id { get; set; }
    public required QuizDocument Document { get; set; }
    public Dictionary<string, Guid> Targets { get; set; } = [];
    public int Index { get; set; }
    public Guid SessionId { get; set; }
    public DateTimeOffset Deadline { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool NeedsReplay { get; set; }
}
