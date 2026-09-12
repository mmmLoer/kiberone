using Kiberone.Core;

namespace Kiberone.Tests;

public sealed class TypingLessonCatalogTests
{
    [Fact]
    public void Catalog_HasThreeEditableSongSlots()
    {
        Assert.Equal(3, TypingLessonCatalog.Defaults.Count);
        Assert.Equal("Дурак и молния — без знаков", TypingLessonCatalog.Defaults[0].Name);
        Assert.Equal("Дурак и молния — как в тексте", TypingLessonCatalog.Defaults[1].Name);
        Assert.Equal("Fool and Lightning", TypingLessonCatalog.Defaults[2].Name);
        Assert.All(TypingLessonCatalog.Defaults, seed =>
        {
            Assert.False(string.IsNullOrWhiteSpace(seed.Text));
            Assert.False(TypingLessonCatalog.IsDefaultName(seed.Name));
        });
    }

    [Fact]
    public void SuggestGoal_FitsShortPlaceholder()
    {
        var text = TypingLessonCatalog.DefaultLiveLessonText;
        var goal = TypingLessonCatalog.SuggestGoalCharacters(text, 150);
        Assert.InRange(goal, 1, text.Length);
    }
}
