using Kiberone.Core;

namespace Kiberone.Tests;

public sealed class TypingMetricsTests
{
    [Theory]
    [InlineData(60, 60, 60)]
    [InlineData(120, 60, 120)]
    [InlineData(10, 0, 0)]
    public void Cpm_UsesActiveTimeOnly(int keys, double seconds, double expected) =>
        Assert.Equal(expected, TypingMetrics.Cpm(keys, seconds));

    [Theory]
    [InlineData(90, 10, 90)]
    [InlineData(0, 0, 100)]
    [InlineData(2, 1, 66.7)]
    public void Accuracy_IsStable(int correct, int wrong, double expected) =>
        Assert.Equal(expected, TypingMetrics.Accuracy(correct, wrong));

    [Fact]
    public void WinnerSelector_NeverAwardsOneStudentTwice()
    {
        var students = new[]
        {
            Metrics("Анна", 100, 0, 100),
            Metrics("Борис", 120, 5, 80),
            Metrics("Вера", 150, 10, 90)
        };

        var winners = WinnerSelector.Select(students, 50);

        Assert.Equal(3, winners.Count);
        Assert.Equal(3, winners.Select(x => x.StudentId).Distinct().Count());
    }

    [Fact]
    public void Validator_RejectsEmptyLesson()
    {
        var request = new CreateLessonRequest("", "", LessonContentKind.Custom, "", 0, 0, []);

        var errors = LessonValidator.Validate(request);

        Assert.Contains(errors, x => x.Contains("Название", StringComparison.Ordinal));
        Assert.Contains(errors, x => x.Contains("текст", StringComparison.OrdinalIgnoreCase));
    }

    private static ParticipantMetrics Metrics(string name, int correct, int wrong, double cpm) => new(
        Guid.NewGuid(), name, ParticipantStatus.Finished, 0, correct, wrong, cpm,
        TypingMetrics.Accuracy(correct, wrong), 60, 0, 100, new Dictionary<string, int>(), DateTimeOffset.UtcNow);
}
