namespace Kiberone.Core;

/// <summary>
/// Built-in typing lessons. Song lyrics are not shipped here — the tutor pastes them into these slots.
/// </summary>
public static class TypingLessonCatalog
{
    public static IReadOnlyList<TypingLessonSeed> Defaults { get; } =
    [
        new(
            "Дурак и молния — без знаков",
            "Текст песни строчными буквами, без знаков препинания, тире и кавычек",
            LessonContentKind.Letters,
            "ru-RU",
            MinimumCharacters: 80,
            DurationMinutes: 12,
            PlainPlaceholder),
        new(
            "Дурак и молния — как в тексте",
            "Текст песни со всеми знаками препинания, тире и кавычками",
            LessonContentKind.Sentences,
            "ru-RU",
            MinimumCharacters: 80,
            DurationMinutes: 12,
            PunctuationPlaceholder),
        new(
            "Fool and Lightning",
            "The song lyrics in English",
            LessonContentKind.Sentences,
            "en-US",
            MinimumCharacters: 80,
            DurationMinutes: 12,
            EnglishPlaceholder)
    ];

    public static string DefaultLiveLessonText => PunctuationPlaceholder;

    public static bool IsDefaultName(string? name) => false;

    public static string GetLessonText(TypingLessonTemplate lesson) =>
        string.Join("\n\n", lesson.Steps.OrderBy(step => step.Order).Select(step => step.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    public static int SuggestGoalCharacters(string text, int? minimumCharacters = null)
    {
        var length = text?.Length ?? 0;
        if (length <= 0) return 1;
        if (minimumCharacters is int goal && goal > 0)
            return Math.Clamp(goal, 1, length);
        if (length <= 100) return length;
        return Math.Clamp(length / 4, 80, 200);
    }

    public static int CountWords(string text) =>
        text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    public static readonly string[] ObsoleteDefaultNames =
    [
        "Разминка: домашний ряд",
        "Python: цикл for",
        "Предложения: школа и код",
        "Стамина: домашний ряд",
        "Стамина: слова и предложения",
        "Стамина: точность и знаки"
    ];

    private const string PlainPlaceholder =
        "вставьте сюда текст песни строчными буквами без точек запятых тире и кавычек";

    private const string PunctuationPlaceholder =
        "Вставьте сюда текст песни со всеми знаками препинания, тире и кавычками.";

    private const string EnglishPlaceholder =
        "Paste the English lyrics here for this typing lesson.";
}

public sealed record TypingLessonSeed(
    string Name,
    string Description,
    LessonContentKind ContentKind,
    string KeyboardLayout,
    int MinimumCharacters,
    int DurationMinutes,
    string Text);
