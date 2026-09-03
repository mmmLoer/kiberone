namespace Kiberone.Core;

/// <summary>
/// Built-in typing passages. Texts are long for reading/practice context;
/// <see cref="TypingLessonSeed.MinimumCharacters"/> is what a student must type for credit.
/// Lessons are flat: one text, no stages.
/// </summary>
public static class TypingLessonCatalog
{
    public static IReadOnlyList<TypingLessonSeed> Defaults { get; } =
    [
        new(
            "Разминка: домашний ряд",
            "Базовая постановка пальцев и спокойный набор на домашнем ряду",
            LessonContentKind.Letters,
            "ru-RU",
            MinimumCharacters: 120,
            DurationMinutes: 10,
            HomeRowWarmup + " " + HomeRowPassage),
        new(
            "Python: цикл for",
            "Печать кода и короткого объяснения про циклы",
            LessonContentKind.Code,
            "en-US",
            MinimumCharacters: 140,
            DurationMinutes: 12,
            PythonForLoopPassage),
        new(
            "Предложения: школа и код",
            "Связный текст про учёбу в KIBERone — можно набрать только часть",
            LessonContentKind.Sentences,
            "ru-RU",
            MinimumCharacters: 150,
            DurationMinutes: 12,
            SchoolAndCodePassage)
    ];

    public static string DefaultLiveLessonText => SchoolAndCodePassage;

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

    private const string HomeRowWarmup =
        "фыва олдж фыва олдж фыва олдж фыва олдж ждло авыф ждло авыф " +
        "фывапро олджэ ёфыва пролдж эёфы вапрол джэёф ывапро лджэё";

    private const string HomeRowPassage =
        "Сегодня мы спокойно ставим пальцы на домашний ряд. Левая рука лежит на буквах ф ы в а, " +
        "правая — на о л д ж. Большие пальцы отдыхают у пробела, спина прямая, взгляд почти не " +
        "убегает на клавиатуру. Сначала печатаем короткие связки: фыва олдж, потом меняем порядок " +
        "и скорость. Не гонитесь за рекордом в первую минуту — важнее ровный ритм и мало ошибок. " +
        "Если палец промахнулся, не стирайте слово целиком: запомните место ошибки и продолжайте. " +
        "Так мозг быстрее запоминает нужные движения. В классе KIBERone мы учимся печатать так же, " +
        "как пишем код: внимательно, шаг за шагом, без паники. Когда домашний ряд станет привычным, " +
        "добавятся верхний и нижний ряды, цифры и знаки. А пока повторяйте спокойные фразы про школу, " +
        "друзей и проекты. Мама папа школа класс урок код игра мечта команда. Мы собираем идеи в " +
        "тетрадь, открываем редактор и пробуем маленькие программы. Печать помогает быстрее записывать " +
        "мысли и меньше отвлекаться на поиск букв. Дышите ровно, держите запястья свободно и слушайте " +
        "звук клавиш — он должен быть коротким и уверенным. Если устали, сделайте короткую паузу и " +
        "вернитесь к тексту. Урок длинный специально: можно набрать только нужную часть для зачёта " +
        "и остановиться, а остальное оставить для тренировки глаз и пальцев. Повторите ещё раз фыва " +
        "олдж и переходите к следующему абзацу без спешки. Хорошая техника сегодня экономит время " +
        "на каждом следующем занятии. Печатайте уверенно и берегите внимание.";

    private const string PythonForLoopPassage =
        "In Python a for-loop repeats actions for every item in a sequence. " +
        "We often write: for i in range(10): print(i) " +
        "This prints numbers from zero to nine. Indentation matters: the body of the loop must be " +
        "shifted to the right. You can also loop over words: for name in students: print(name) " +
        "Loops help games, quizzes and classroom tools count scores without copying the same line " +
        "again and again. Try typing slowly and keep symbols exact: colon, parentheses and spaces. " +
        "Пример на русском: цикл for нужен, когда надо повторить действие много раз. Например, " +
        "показать список учеников, проверить ответы викторины или нарисовать несколько спрайтов. " +
        "Сначала мы пишем заголовок цикла, затем тело с отступом. Если забыть двоеточие, Python " +
        "покажет ошибку. Если забыть отступ, код тоже не запустится. Поэтому сегодня тренируем " +
        "точность: каждая скобка и каждый пробел важны. Можно представить, что клавиатура — это " +
        "пульт робота: одна неверная команда меняет результат. Печатайте фрагменты кода и короткие " +
        "пояснения подряд, не глядя на руки. Когда пальцы запомнят частые сочетания вроде range, " +
        "print и for, писать программы станет быстрее и спокойнее. Ниже ещё один пример: " +
        "total = 0\nfor value in scores:\n    total = total + value\nprint(total)\n" +
        "Так мы складываем очки класса. Повторите набор несколько раз, но помните: для зачёта " +
        "достаточно набрать минимальное число знаков, весь длинный текст печатать не обязательно. " +
        "Остальное — запас для тех, кто хочет потренироваться дольше. Keep a steady rhythm, watch " +
        "the highlighted key, and finish when your progress reaches one hundred percent for the goal.";

    private const string SchoolAndCodePassage =
        "В школе будущего мы не только слушаем учителя, но и сами собираем маленькие проекты. " +
        "Утром открываем ноутбук, заходим в класс KIBERone и выбираем своё имя в списке группы. " +
        "На экране появляются задания, сохранения и иногда викторина. Печать помогает быстрее " +
        "отвечать и не терять идею, пока она свежая. Представьте: вы придумали игру про робота, " +
        "который ищет монетки. Чтобы робот двигался, нужен код, а чтобы код появился быстро, " +
        "нужны пальцы, которые знают дорогу к каждой букве. Поэтому мы тренируемся на связных " +
        "предложениях, а не только на отдельных словах. Сегодняшний текст длинный специально. " +
        "Его можно читать глазами как рассказ, а руками набрать лишь часть — ту, что нужна для " +
        "зачёта. Если останется время и сила, продолжайте дальше и улучшайте скорость. В классе " +
        "важно помогать соседу: подсказать, где лежит нужный знак, но не печатать за него. Ошибки " +
        "— это нормально. Каждая ошибка показывает, какую связку стоит повторить завтра. Держите " +
        "спину ровно, ноги на полу, монитор чуть ниже глаз. Когда устаёте, поморгайте и снова " +
        "смотрите на экран, а не на клавиши. Так формируется слепой метод. Мы пишем о школе, коде, " +
        "дружбе и целях: накопить кибероны, открыть новый модуль, показать родителям свой проект. " +
        "Пусть буквы складываются в предложения так же уверенно, как блоки складываются в программу. " +
        "Ещё один круг: школа класс урок проект команда идея прототип тест успех. Повторите спокойно, " +
        "без гонки. В конце урока сравните свой темп с началом — даже маленький рост уже победа. " +
        "Печатайте внимательно, дышите ровно и помните, что длинный текст — это полигон, а не " +
        "обязанность пройти его до последней точки сегодня.";
}

public sealed record TypingLessonSeed(
    string Name,
    string Description,
    LessonContentKind ContentKind,
    string KeyboardLayout,
    int MinimumCharacters,
    int DurationMinutes,
    string Text);
