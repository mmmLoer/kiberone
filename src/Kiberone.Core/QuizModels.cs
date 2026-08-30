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

public sealed record StartQuizRequest(string Question, IReadOnlyList<string> Options, int CorrectIndex, int XpReward, IReadOnlyList<string> ClientIds);
public sealed record SubmitQuizAnswerRequest(Guid SessionId, string ClientId, int SelectedIndex);
public sealed record QuizResult(Guid SessionId, bool Correct, int XpAwarded, string Message);
