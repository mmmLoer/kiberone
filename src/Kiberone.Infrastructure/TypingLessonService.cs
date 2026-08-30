using System.Text.Json;
using Kiberone.Core;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Infrastructure;

public sealed class TypingLessonService(DbContextOptions<ClassroomDbContext> options)
{
    public async Task<IReadOnlyList<TypingLessonTemplate>> ListLessonsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = new ClassroomDbContext(options);
        // SQLite stores DateTimeOffset as text and cannot translate its comparison into ORDER BY.
        // Materialize the small lesson catalogue first, then order by the actual timestamp in memory.
        var lessons = await db.TypingLessons.AsNoTracking().Include(x => x.Steps)
            .ToListAsync(cancellationToken);
        return lessons.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<TypingLessonTemplate?> GetLessonAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = new ClassroomDbContext(options);
        return await db.TypingLessons.AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task<TypingLessonTemplate> CreateLessonAsync(CreateLessonRequest request, CancellationToken cancellationToken = default)
    {
        var errors = LessonValidator.Validate(request);
        if (errors.Count > 0) throw new LessonValidationException(errors);
        var lesson = new TypingLessonTemplate
        {
            Name = request.Name.Trim(),
            Description = request.Description.Trim(),
            ContentKind = request.ContentKind,
            KeyboardLayout = request.KeyboardLayout.Trim(),
            MinimumCharacters = request.MinimumCharacters,
            DurationMinutes = request.DurationMinutes,
            Steps = request.Steps.Select((step, index) => new TypingLessonStep
            {
                Order = index,
                Title = step.Title.Trim(),
                Text = step.Text,
                TargetCpm = step.TargetCpm,
                TargetAccuracy = step.TargetAccuracy
            }).ToList()
        };
        await using var db = new ClassroomDbContext(options);
        db.TypingLessons.Add(lesson);
        await db.SaveChangesAsync(cancellationToken);
        return lesson;
    }

    public async Task<TypingLessonTemplate?> UpdateLessonAsync(Guid id, UpdateLessonRequest request, CancellationToken cancellationToken = default)
    {
        var errors = LessonValidator.Validate(request);
        if (errors.Count > 0) throw new LessonValidationException(errors);
        await using var db = new ClassroomDbContext(options);
        var lesson = await db.TypingLessons.Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (lesson is null) return null;
        lesson.Name = request.Name.Trim();
        lesson.Description = request.Description.Trim();
        lesson.ContentKind = request.ContentKind;
        lesson.KeyboardLayout = request.KeyboardLayout.Trim();
        lesson.MinimumCharacters = request.MinimumCharacters;
        lesson.DurationMinutes = request.DurationMinutes;
        lesson.Lifecycle = request.Lifecycle;
        lesson.Version++;
        lesson.UpdatedAt = DateTimeOffset.UtcNow;
        db.TypingLessonSteps.RemoveRange(lesson.Steps);
        lesson.Steps = request.Steps.Select((step, index) => new TypingLessonStep
        {
            LessonId = lesson.Id,
            Order = index,
            Title = step.Title.Trim(),
            Text = step.Text,
            TargetCpm = step.TargetCpm,
            TargetAccuracy = step.TargetAccuracy
        }).ToList();
        await db.SaveChangesAsync(cancellationToken);
        return lesson;
    }

    public async Task<TypingSession> StartSessionAsync(StartTypingSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.StudentIds.Count == 0) throw new LessonValidationException(["Выберите хотя бы одного ученика."]);
        await using var db = new ClassroomDbContext(options);
        var lesson = await db.TypingLessons.Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == request.LessonId, cancellationToken)
            ?? throw new KeyNotFoundException("Урок не найден.");
        var distinctStudentIds = request.StudentIds.Distinct().ToList();
        var students = await db.Students.Where(x => distinctStudentIds.Contains(x.Id) && x.GroupId == request.GroupId)
            .ToListAsync(cancellationToken);
        if (students.Count != distinctStudentIds.Count)
            throw new LessonValidationException(["Некоторые ученики не найдены в выбранной группе."]);
        var session = new TypingSession
        {
            LessonId = lesson.Id,
            GroupId = request.GroupId,
            Status = TypingSessionStatus.Active,
            StartedAt = DateTimeOffset.UtcNow,
            Participants = students.Select(student => new TypingParticipant
            {
                StudentId = student.Id,
                Status = ParticipantStatus.Waiting
            }).ToList()
        };
        db.TypingSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<TypingSessionSnapshot?> RecordTelemetryAsync(Guid sessionId, TelemetryUpdateRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = new ClassroomDbContext(options);
        var session = await LoadSessionAsync(db, sessionId, true, cancellationToken);
        if (session is null) return null;
        if (session.Status is TypingSessionStatus.Finished or TypingSessionStatus.Cancelled)
            throw new InvalidOperationException("Завершённая сессия не принимает телеметрию.");
        var participant = session.Participants.SingleOrDefault(x => x.StudentId == request.StudentId)
            ?? throw new KeyNotFoundException("Ученик не назначен на эту сессию.");
        if (request.CorrectKeys < participant.CorrectKeys || request.WrongKeys < participant.WrongKeys || request.ActiveSeconds < participant.ActiveSeconds)
            throw new LessonValidationException(["Счётчики телеметрии не могут уменьшаться."]);
        if (request.ActiveSeconds < 0 || request.PausedSeconds < 0)
            throw new LessonValidationException(["Время не может быть отрицательным."]);
        participant.CurrentStep = Math.Clamp(request.CurrentStep, 0, Math.Max(0, (session.Lesson?.Steps.Count ?? 1) - 1));
        participant.CorrectKeys = request.CorrectKeys;
        participant.WrongKeys = request.WrongKeys;
        participant.ActiveSeconds = request.ActiveSeconds;
        participant.PausedSeconds = request.PausedSeconds;
        participant.Status = request.Status;
        participant.ProblemCharactersJson = JsonSerializer.Serialize(request.ProblemCharacters ?? new Dictionary<string, int>());
        participant.LastSeenAt = DateTimeOffset.UtcNow;
        if (request.Status == ParticipantStatus.Finished && participant.CompletedAt is null)
            participant.CompletedAt = DateTimeOffset.UtcNow;
        db.TypingTelemetry.Add(new TypingTelemetrySample
        {
            ParticipantId = participant.Id,
            CorrectKeys = request.CorrectKeys,
            WrongKeys = request.WrongKeys,
            ActiveSeconds = request.ActiveSeconds,
            PausedSeconds = request.PausedSeconds,
            CurrentStep = participant.CurrentStep,
            Status = request.Status
        });
        await db.SaveChangesAsync(cancellationToken);
        return MapSnapshot(session);
    }

    public async Task<TypingSessionSnapshot?> GetSnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = new ClassroomDbContext(options);
        var session = await LoadSessionAsync(db, sessionId, false, cancellationToken);
        return session is null ? null : MapSnapshot(session);
    }

    public async Task<(TypingSessionSnapshot Snapshot, IReadOnlyList<TypingWinner> Winners)?> FinishSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = new ClassroomDbContext(options);
        var session = await LoadSessionAsync(db, sessionId, true, cancellationToken);
        if (session is null) return null;
        session.Status = TypingSessionStatus.Finished;
        session.FinishedAt ??= DateTimeOffset.UtcNow;
        foreach (var participant in session.Participants.Where(x => x.Status != ParticipantStatus.Finished))
        {
            participant.Status = ParticipantStatus.Finished;
            participant.CompletedAt ??= session.FinishedAt;
        }
        var snapshot = MapSnapshot(session);
        var winners = WinnerSelector.Select(snapshot.Participants, session.Lesson?.MinimumCharacters ?? 1);
        foreach (var winner in winners)
        {
            var student = session.Participants.Single(x => x.StudentId == winner.StudentId).Student;
            if (student is not null) student.Xp += winner.XpReward;
        }
        await db.SaveChangesAsync(cancellationToken);
        return (MapSnapshot(session), winners);
    }

    private static Task<TypingSession?> LoadSessionAsync(ClassroomDbContext db, Guid sessionId, bool tracking, CancellationToken cancellationToken)
    {
        var query = db.TypingSessions
            .Include(x => x.Lesson).ThenInclude(x => x!.Steps)
            .Include(x => x.Participants).ThenInclude(x => x.Student)
            .AsQueryable();
        if (!tracking) query = query.AsNoTracking();
        return query.SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
    }

    private static TypingSessionSnapshot MapSnapshot(TypingSession session)
    {
        var expected = session.Lesson?.Steps.Sum(x => x.Text.Length) ?? 0;
        var metrics = session.Participants.Select(participant => new ParticipantMetrics(
            participant.StudentId,
            participant.Student?.DisplayName ?? "Неизвестный ученик",
            participant.Status,
            participant.CurrentStep,
            participant.CorrectKeys,
            participant.WrongKeys,
            TypingMetrics.Cpm(participant.CorrectKeys, participant.ActiveSeconds),
            TypingMetrics.Accuracy(participant.CorrectKeys, participant.WrongKeys),
            participant.ActiveSeconds,
            participant.PausedSeconds,
            TypingMetrics.Progress(participant.CorrectKeys, expected),
            TypingMetrics.ParseProblemCharacters(participant.ProblemCharactersJson),
            participant.LastSeenAt)).ToList();
        return new TypingSessionSnapshot(
            session.Id,
            session.Lesson?.Name ?? "Урок",
            session.Status,
            session.StartedAt,
            session.FinishedAt,
            metrics,
            metrics.Count == 0 ? 0 : Math.Round(metrics.Average(x => x.Cpm), 1),
            metrics.Count == 0 ? 100 : Math.Round(metrics.Average(x => x.Accuracy), 1),
            metrics.Sum(x => x.CorrectKeys));
    }
}

public sealed class LessonValidationException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
