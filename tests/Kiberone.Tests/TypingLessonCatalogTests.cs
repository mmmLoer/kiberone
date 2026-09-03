using Kiberone.Core;

namespace Kiberone.Tests;

public sealed class TypingLessonCatalogTests
{
    [Fact]
    public void DefaultPassages_HaveAtLeastTwoHundredWords()
    {
        foreach (var seed in TypingLessonCatalog.Defaults)
        {
            var words = TypingLessonCatalog.CountWords(seed.Text);
            Assert.True(words >= 200, $"{seed.Name} has only {words} words");
            Assert.InRange(seed.MinimumCharacters, 80, words * 8);
            Assert.True(seed.MinimumCharacters < seed.Text.Length);
        }
    }

    [Fact]
    public void SuggestGoal_DoesNotRequireFullLongText()
    {
        var text = TypingLessonCatalog.DefaultLiveLessonText;
        var goal = TypingLessonCatalog.SuggestGoalCharacters(text, 150);
        Assert.Equal(150, goal);
        Assert.True(goal < text.Length);
    }
}
