namespace Kiberone.Core;

public sealed class QuizSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Question { get; set; }
    public string OptionsJson { get; set; } = "[]";
    public int CorrectIndex { get; set; }
    public int XpReward { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class QuizAnswer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public required string ClientId { get; set; }
    public Guid? StudentId { get; set; }
    public int SelectedIndex { get; set; }
    public bool IsCorrect { get; set; }
    public int XpAwarded { get; set; }
    public DateTimeOffset AnsweredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record StartQuizRequest(
    string Question,
    IReadOnlyList<string> Options,
    int CorrectIndex,
    int XpReward,
    IReadOnlyList<string> ClientIds,
    int? TimeLimitSeconds = null,
    bool ShuffleAnswers = false,
    bool ShowFeedback = true);

public sealed record SubmitQuizAnswerRequest(Guid SessionId, string ClientId, int SelectedIndex);
public sealed record QuizResult(Guid SessionId, bool Correct, int XpAwarded, string Message);

public sealed class QuizDocument
{
    public string Format { get; set; } = "kiberone-quiz";
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "Новая викторина";
    public int TimePerQuestionSeconds { get; set; } = 30;
    public int XpReward { get; set; } = 10;
    public bool ShuffleAnswers { get; set; }
    public bool ShowFeedback { get; set; } = true;
    public List<QuizDocumentQuestion> Questions { get; set; } = [];
}

public sealed class QuizDocumentQuestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public string? MediaPath { get; set; }
    public List<string> Options { get; set; } = ["", "", "", ""];
    public int CorrectIndex { get; set; }
}
