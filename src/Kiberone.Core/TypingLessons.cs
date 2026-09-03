using System.Collections.ObjectModel;
using System.Text.Json;

namespace Kiberone.Core;

public enum LessonContentKind
{
    Letters,
    Words,
    Sentences,
    Code,
    Custom
}

public enum LessonLifecycle
{
    Draft,
    Published,
    Archived
}

public enum TypingSessionStatus
{
    Scheduled,
    Active,
    Paused,
    Finished,
    Cancelled
}

public enum ParticipantStatus
{
    Waiting,
    Typing,
    Paused,
    Finished,
    Offline
}

public sealed class TypingLessonTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Description { get; set; } = string.Empty;
    public LessonContentKind ContentKind { get; set; } = LessonContentKind.Custom;
    public string KeyboardLayout { get; set; } = "ru-RU";
    public int MinimumCharacters { get; set; } = 50;
    public int DurationMinutes { get; set; } = 10;
    public int Version { get; set; } = 1;
    public LessonLifecycle Lifecycle { get; set; } = LessonLifecycle.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TypingLessonStep> Steps { get; set; } = [];
}

public sealed class TypingLessonStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public TypingLessonTemplate? Lesson { get; set; }
    public int Order { get; set; }
    public required string Title { get; set; }
    public required string Text { get; set; }
    public int? TargetCpm { get; set; }
    public decimal? TargetAccuracy { get; set; }
}

public sealed class TypingSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LessonId { get; set; }
    public TypingLessonTemplate? Lesson { get; set; }
    public Guid GroupId { get; set; }
    public TypingSessionStatus Status { get; set; } = TypingSessionStatus.Scheduled;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TypingParticipant> Participants { get; set; } = [];
}

public sealed class TypingParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public TypingSession? Session { get; set; }
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Waiting;
    public int CurrentStep { get; set; }
    public int CorrectKeys { get; set; }
    public int WrongKeys { get; set; }
    public double ActiveSeconds { get; set; }
    public double PausedSeconds { get; set; }
    public string ProblemCharactersJson { get; set; } = "{}";
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TypingTelemetrySample> Samples { get; set; } = [];
}

public sealed class TypingTelemetrySample
{
    public long Id { get; set; }
    public Guid ParticipantId { get; set; }
    public TypingParticipant? Participant { get; set; }
    public int CorrectKeys { get; set; }
    public int WrongKeys { get; set; }
    public double ActiveSeconds { get; set; }
    public double PausedSeconds { get; set; }
    public int CurrentStep { get; set; }
    public ParticipantStatus Status { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record LessonStepDraft(string Title, string Text, int? TargetCpm = null, decimal? TargetAccuracy = null);

public sealed record CreateLessonRequest(
    string Name,
    string Description,
    LessonContentKind ContentKind,
    string KeyboardLayout,
    int MinimumCharacters,
    int DurationMinutes,
    IReadOnlyList<LessonStepDraft> Steps);

public sealed record UpdateLessonRequest(
    string Name,
    string Description,
    LessonContentKind ContentKind,
    string KeyboardLayout,
    int MinimumCharacters,
    int DurationMinutes,
    LessonLifecycle Lifecycle,
    IReadOnlyList<LessonStepDraft> Steps);

public sealed record StartTypingSessionRequest(Guid LessonId, Guid GroupId, IReadOnlyList<Guid> StudentIds);

public sealed record TelemetryUpdateRequest(
    Guid StudentId,
    int CurrentStep,
    int CorrectKeys,
    int WrongKeys,
    double ActiveSeconds,
    double PausedSeconds,
    ParticipantStatus Status,
    IReadOnlyDictionary<string, int>? ProblemCharacters);

public sealed record ParticipantMetrics(
    Guid StudentId,
    string StudentName,
    ParticipantStatus Status,
    int CurrentStep,
    int CorrectKeys,
    int WrongKeys,
    double Cpm,
    double Accuracy,
    double ActiveSeconds,
    double PausedSeconds,
    double Progress,
    IReadOnlyDictionary<string, int> ProblemCharacters,
    DateTimeOffset LastSeenAt);

public sealed record TypingSessionSnapshot(
    Guid SessionId,
    string LessonName,
    TypingSessionStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<ParticipantMetrics> Participants,
    double AverageCpm,
    double AverageAccuracy,
    int TotalCharacters);

public sealed record TypingLessonOffer(
    Guid Id,
    string Name,
    string Description,
    string ContentKind,
    string KeyboardLayout,
    int MinimumCharacters,
    int DurationMinutes,
    string Text);

public sealed record TypingWinner(Guid StudentId, string StudentName, string Category, string Value, int XpReward);

public static class TypingMetrics
{
    public static double Cpm(int correctKeys, double activeSeconds) =>
        activeSeconds <= 0 ? 0 : Math.Round(correctKeys * 60d / activeSeconds, 1);

    public static double Accuracy(int correctKeys, int wrongKeys)
    {
        var total = correctKeys + wrongKeys;
        return total == 0 ? 100 : Math.Round(correctKeys * 100d / total, 1);
    }

    public static double Progress(int correctKeys, int expectedCharacters) =>
        expectedCharacters <= 0 ? 0 : Math.Round(Math.Clamp(correctKeys * 100d / expectedCharacters, 0, 100), 1);

    public static IReadOnlyDictionary<string, int> ParseProblemCharacters(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());
        }
    }
}

public static class LessonValidator
{
    public static IReadOnlyList<string> Validate(CreateLessonRequest request) => ValidateCore(
        request.Name,
        request.KeyboardLayout,
        request.MinimumCharacters,
        request.DurationMinutes,
        request.Steps);

    public static IReadOnlyList<string> Validate(UpdateLessonRequest request) => ValidateCore(
        request.Name,
        request.KeyboardLayout,
        request.MinimumCharacters,
        request.DurationMinutes,
        request.Steps);

    private static IReadOnlyList<string> ValidateCore(
        string name,
        string keyboardLayout,
        int minimumCharacters,
        int durationMinutes,
        IReadOnlyList<LessonStepDraft> steps)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) errors.Add("Название урока обязательно.");
        if (name.Trim().Length > 120) errors.Add("Название урока не должно превышать 120 символов.");
        if (string.IsNullOrWhiteSpace(keyboardLayout)) errors.Add("Необходимо выбрать раскладку.");
        if (minimumCharacters is < 1 or > 100_000) errors.Add("Минимум символов должен быть от 1 до 100000.");
        if (durationMinutes is < 1 or > 180) errors.Add("Длительность должна быть от 1 до 180 минут.");
        if (steps.Count == 0) errors.Add("Добавьте текст урока.");
        if (steps.Count > 1) errors.Add("Урок должен быть без этапов — один текст.");

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (string.IsNullOrWhiteSpace(step.Text)) errors.Add("Добавьте текст урока.");
            if (step.Text.Length > 50_000) errors.Add("Текст урока превышает 50000 символов.");
            if (step.TargetAccuracy is < 0 or > 100) errors.Add("Точность должна быть от 0 до 100%.");
            if (step.TargetCpm is < 1 or > 2_000) errors.Add("CPM должен быть от 1 до 2000.");
        }

        return errors;
    }
}

public static class WinnerSelector
{
    public static IReadOnlyList<TypingWinner> Select(
        IEnumerable<ParticipantMetrics> participants,
        int minimumCharacters)
    {
        var eligible = participants
            .Where(p => p.CorrectKeys >= minimumCharacters)
            .ToList();
        var winners = new List<TypingWinner>(3);
        var used = new HashSet<Guid>();

        AddWinner(
            eligible.OrderByDescending(p => p.Accuracy).ThenByDescending(p => p.CorrectKeys).ThenByDescending(p => p.Cpm),
            "Лучшая точность",
            p => $"{p.Accuracy:0.#}%",
            used,
            winners);

        AddWinner(
            eligible.OrderByDescending(p => p.Cpm).ThenByDescending(p => p.CorrectKeys).ThenByDescending(p => p.Accuracy),
            "Самая высокая скорость",
            p => $"{p.Cpm:0.#} CPM",
            used,
            winners);

        AddWinner(
            eligible.Where(p => p.Accuracy >= 80).OrderByDescending(p => p.CorrectKeys).ThenByDescending(p => p.Cpm).ThenByDescending(p => p.Accuracy),
            "Больше всего знаков",
            p => p.CorrectKeys.ToString(System.Globalization.CultureInfo.InvariantCulture),
            used,
            winners);

        return winners;
    }

    private static void AddWinner(
        IEnumerable<ParticipantMetrics> candidates,
        string category,
        Func<ParticipantMetrics, string> value,
        ISet<Guid> used,
        ICollection<TypingWinner> winners)
    {
        var selected = candidates.FirstOrDefault(candidate => !used.Contains(candidate.StudentId));
        if (selected is null) return;
        used.Add(selected.StudentId);
        winners.Add(new TypingWinner(selected.StudentId, selected.StudentName, category, value(selected), 20));
    }
}
