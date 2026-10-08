using System.Security.Cryptography;
using System.Text;
namespace Kiberone.Core;
public sealed record SharedQuiz(Guid Id, string Location, int Revision, DateTimeOffset UpdatedAt, QuizDocument Document);
public sealed record PublishQuizRequest(string Location, string Password, QuizDocument Document, int ExpectedRevision = 0);
public sealed record LearningAuth(string Location, string Password);
public sealed record TypingPersonalBest(string LessonKey, double Speed, double Accuracy, int CorrectKeys);
public sealed record TypingRecordsRequest(string Location, string Password, Guid StudentId, IReadOnlyList<TypingPersonalBest>? Records = null);
public sealed record SaveTypingRecordsRequest(IReadOnlyList<TypingPersonalBest> Records);
public static class TypingRecordKey
{
    public static string For(string name, string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim() + "\n" + text)));
}
