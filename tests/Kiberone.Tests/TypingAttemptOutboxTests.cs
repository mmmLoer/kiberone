using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class TypingAttemptOutboxTests
{
    [Fact]
    public void PendingAttempts_SurviveRestart_AndOnlyAcknowledgedAttemptIsRemoved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"typing-attempts-{Guid.NewGuid():N}.json");
        try
        {
            var firstStudent = Guid.NewGuid();
            var secondStudent = Guid.NewGuid();
            var first = NewAttempt(firstStudent);
            var second = NewAttempt(secondStudent);
            var outbox = new TypingAttemptOutbox(path);
            outbox.Enqueue(first);
            outbox.Enqueue(second);

            var restarted = new TypingAttemptOutbox(path);
            Assert.True(restarted.TryGetNextForStudent(secondStudent, out var forSecond));
            Assert.Equal(second.Attempt.AttemptId, forSecond?.Attempt.AttemptId);
            restarted.Acknowledge(second.Attempt.AttemptId);

            var afterAcknowledgement = new TypingAttemptOutbox(path);
            Assert.False(afterAcknowledgement.TryGetNextForStudent(secondStudent, out _));
            Assert.True(afterAcknowledgement.TryGetNextForStudent(firstStudent, out var forFirst));
            Assert.Equal(first.Attempt.AttemptId, forFirst?.Attempt.AttemptId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static SubmitTypingAttemptRequest NewAttempt(Guid studentId) => new(studentId,
        new TypingAttemptDraft(Guid.NewGuid(), null, "Тест", "текст", 5, 3, 1, 4, 0,
            new Dictionary<string, int>()));
}
