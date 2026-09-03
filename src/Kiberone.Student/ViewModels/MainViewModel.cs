using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Kiberone.Vpn;
using System.Collections.ObjectModel;

namespace Kiberone.Student.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly Stopwatch activeTime = new();
    private readonly Stopwatch pauseTime = new();
    private readonly Dictionary<string, int> problemCharacters = [];
    private readonly List<bool> typedResults = [];
    private bool lastAttemptWasWrong;

    [ObservableProperty] private string lessonName = "Разминка · Python";
    [ObservableProperty] private string targetText = "for i in range(10): print(i)";
    [ObservableProperty] private string typedText = string.Empty;
    [ObservableProperty] private int correctKeys;
    [ObservableProperty] private int wrongKeys;
    [ObservableProperty] private double cpm;
    [ObservableProperty] private double accuracy = 100;
    [ObservableProperty] private double progress;
    [ObservableProperty] private int goalCharacters = 120;
    [ObservableProperty] private string goalLabel = "Зачёт: 0 / 120 знаков";
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private bool isFinished;
    [ObservableProperty] private string statusMessage = "Печатайте — Backspace отключён, Escape ставит урок на паузу.";
    [ObservableProperty] private string currentCharacter = "f";
    [ObservableProperty] private bool isLessonStarted;
    [ObservableProperty] private string elapsedLabel = "00:00";
    [ObservableProperty] private string lastInputFeedback = "Нажмите ПРОБЕЛ, чтобы начать";
    [ObservableProperty] private string layoutWarning = string.Empty;
    [ObservableProperty] private bool hasLayoutWarning;
    [ObservableProperty] private int currentStreak;
    [ObservableProperty] private int bestStreak;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string connectionLabel = "Ищем тьютора…";
    [ObservableProperty] private string connectionActionMessage = "Поиск Tutor выполняется автоматически.";
    [ObservableProperty] private string syncLabel = "Ожидаем первую проверку…";
    [ObservableProperty] private string vpnLabel = "VPN: ожидает команду тьютора";
    [ObservableProperty] private string updateLabel = $"Версия {BuildInfo.Version}";
    [ObservableProperty] private bool hasUpdate;
    [ObservableProperty] private bool isScreenLocked;
    [ObservableProperty] private string lockMessage = "Занятие продолжается. Экран временно заблокирован тьютором.";
    [ObservableProperty] private string notificationText = string.Empty;
    [ObservableProperty] private bool isNotificationVisible;
    [ObservableProperty] private bool isQuizVisible;
    [ObservableProperty] private string quizQuestion = string.Empty;
    [ObservableProperty] private string? selectedQuizOption;
    [ObservableProperty] private string quizFeedback = "Выберите один вариант.";
    public ObservableCollection<string> QuizOptions { get; } = [];
    public ObservableCollection<TypingGlyphViewModel> TextGlyphs { get; } = [];
    public ObservableCollection<KeyboardRowViewModel> KeyboardRows { get; } = [];
    public ObservableCollection<StudentChoiceViewModel> Students { get; } = [];
    public ObservableCollection<string> LoginGroups { get; } = [];
    public ObservableCollection<StudentChoiceViewModel> LoginStudents { get; } = [];
    public ObservableCollection<TutorLessonCardViewModel> TutorLessons { get; } = [];
    [ObservableProperty] private string tutorLessonsStatus = "Ждём уроки от тьютора…";
    public bool HasTutorLessons => TutorLessons.Count > 0;
    public bool HasNoTutorLessons => !HasTutorLessons;
    [ObservableProperty] private string? selectedLoginGroup;
    [ObservableProperty] private StudentChoiceViewModel? selectedStudent;
    [ObservableProperty] private bool isLoginVisible = true;
    [ObservableProperty] private string loginMessage = "Выберите группу и своё имя.";
    [ObservableProperty] private string currentStudentName = "Ученик";
    [ObservableProperty] private string currentStudentGroup = "Группа не выбрана";
    [ObservableProperty] private int currentStudentLevel = 1;
    [ObservableProperty] private int currentStudentKiberons;
    [ObservableProperty] private int selectedSectionIndex;
    private Guid? quizSessionId;
    private bool applyingLoginGroups;
    private bool loginGroupChosenByUser;
    private string? lastPreferredGroup;
    public Action? UpdateRequested { get; set; }
    public Action? FocusEnabled { get; set; }
    public Action? FocusDisabled { get; set; }
    public Action? WatchdogEnabled { get; set; }
    public Action? WatchdogDisabled { get; set; }
    public Action<Guid, int>? QuizAnswerRequested { get; set; }
    public Action<Guid>? StudentSelected { get; set; }
    public Action<bool>? ScreenLockChanged { get; set; }

    partial void OnIsScreenLockedChanged(bool value) => ScreenLockChanged?.Invoke(value);

    public void HandleCharacter(char character)
    {
        if (IsPaused || IsFinished) return;
        if (!IsLessonStarted) StartLesson();
        if (TypedText.Length >= GoalCharacters || TypedText.Length >= TargetText.Length)
        {
            Finish();
            return;
        }

        var expected = TargetText[TypedText.Length];
        if (character == expected)
        {
            lastAttemptWasWrong = false;
            CorrectKeys++;
            CurrentStreak++;
            BestStreak = Math.Max(BestStreak, CurrentStreak);
            LastInputFeedback = $"Верно: {Printable(character)}";
            typedResults.Add(true);
            HasLayoutWarning = false;
            LayoutWarning = string.Empty;
            TypedText += character;
        }
        else
        {
            lastAttemptWasWrong = true;
            WrongKeys++;
            CurrentStreak = 0;
            LastInputFeedback = $"Ошибка: ожидалась {Printable(expected)}, нажата {Printable(character)}";
            var key = expected.ToString();
            problemCharacters[key] = problemCharacters.GetValueOrDefault(key) + 1;
            DetectLayoutMismatch(expected, character);
        }
        UpdateMetrics();
        if (TypedText.Length >= GoalCharacters || TypedText.Length >= TargetText.Length) Finish();
    }

    public void StartLesson()
    {
        if (IsLessonStarted || IsFinished) return;
        IsLessonStarted = true;
        activeTime.Restart();
        LastInputFeedback = "Урок начат — смотрите на подсвеченную клавишу";
        StatusMessage = "Урок идёт. Escape — пауза.";
        RebuildTypingPresentation();
    }

    public void RegisterBlockedBackspace()
    {
        if (IsPaused || IsFinished) return;
        WrongKeys++;
        problemCharacters["Backspace"] = problemCharacters.GetValueOrDefault("Backspace") + 1;
        StatusMessage = "Backspace запрещён: продолжайте с текущего места.";
        UpdateMetrics();
    }

    public void TogglePause()
    {
        if (IsFinished) return;
        IsPaused = !IsPaused;
        if (IsPaused)
        {
            activeTime.Stop();
            pauseTime.Start();
            StatusMessage = "Пауза. Нажмите Escape, чтобы продолжить.";
            SelectedSectionIndex = 4;
        }
        else
        {
            pauseTime.Stop();
            activeTime.Start();
            StatusMessage = "Урок продолжается.";
            SelectedSectionIndex = 3;
        }
        UpdateMetrics();
    }

    public void SetConnection(StudentConnectionState state)
    {
        IsConnected = state.IsConnected;
        ConnectionLabel = state.IsConnected
            ? "На связи с классом"
            : string.IsNullOrWhiteSpace(state.Message) ? "Ищем класс…" : state.Message;
        ConnectionActionMessage = state.IsConnected
            ? "Задания и сохранения приходят сами."
            : "Подождите: класс ещё не найден в этой сети.";
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(ConnectionForeground));
        OnPropertyChanged(nameof(ConnectionBackground));
    }

    public bool IsOffline => !IsConnected;
    public string ConnectionForeground => IsConnected ? "#056D69" : "#64727A";
    public string ConnectionBackground => IsConnected ? "#E0F5F2" : "#EDF1F3";
    public string Greeting => $"Привет, {CurrentStudentName}!";
    public string LevelLabel => $"Уровень {CurrentStudentLevel}";
    public string BalanceLabel => $"{CurrentStudentKiberons} ₭";
    public double LevelProgress => Math.Clamp((CurrentStudentLevel * 83) % 100, 8, 96);
    public string LevelProgressLabel => $"{(int)LevelProgress * 10} / 1000 XP до нового уровня";
    public double GoalProgress => Math.Clamp(CurrentStudentKiberons / 14.2, 0, 100);
    public string GoalRemainder => $"Осталось {Math.Max(0, 1420 - CurrentStudentKiberons)} ₭";
    public string LessonMeta => $"{(TargetText.Any(IsCyrillic) ? "Русская раскладка" : "Английская раскладка")} · {TargetText.Length} знаков";
    public bool IsHomeSection => SelectedSectionIndex == 0;
    public bool IsLessonsSection => SelectedSectionIndex is >= 1 and <= 5;
    public bool IsProfileSection => SelectedSectionIndex == 6;
    public bool IsConnectionSection => SelectedSectionIndex == 7;
    public string SectionTitle => SelectedSectionIndex switch
    {
        0 => "Главная", 1 => "Уроки печати", 2 => "Назначенный урок", 3 => "Урок печати",
        4 => "Пауза", 5 => "Итоги урока",         6 => "Уровень и награды", _ => "Связь"
    };
    public string SectionSubtitle => SelectedSectionIndex switch
    {
        0 => "Твой следующий шаг появится здесь", 1 => "Выбери доступный материал",
        2 => "Пробел запускает урок", 3 => "Одна строка · пробел — старт",
        4 => "Можно передохнуть", 5 => "Урок закончен",
        6 => "Уровень, кибероны и достижения", _ => "Связь с классом"
    };

    partial void OnSelectedSectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(SectionSubtitle));
        OnPropertyChanged(nameof(IsHomeSection));
        OnPropertyChanged(nameof(IsLessonsSection));
        OnPropertyChanged(nameof(IsProfileSection));
        OnPropertyChanged(nameof(IsConnectionSection));
    }

    partial void OnTargetTextChanged(string value) => OnPropertyChanged(nameof(LessonMeta));

    public void SetStudents(IReadOnlyList<StudentSummary> students, string? preferredGroup = null)
    {
        var selectedId = SelectedStudent?.Id;
        var previousGroup = SelectedLoginGroup;
        Students.Clear();
        foreach (var student in students) Students.Add(new StudentChoiceViewModel(student));

        applyingLoginGroups = true;
        try
        {
            LoginGroups.Clear();
            foreach (var group in Students.Select(x => x.Group).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().OrderBy(x => x))
                LoginGroups.Add(group);

            var tutorChangedGroup = !string.Equals(lastPreferredGroup, preferredGroup, StringComparison.Ordinal);
            lastPreferredGroup = preferredGroup;
            if (tutorChangedGroup)
                loginGroupChosenByUser = false;

            if (loginGroupChosenByUser && previousGroup is not null && LoginGroups.Contains(previousGroup))
                SelectedLoginGroup = previousGroup;
            else
                SelectedLoginGroup = LoginGroups.FirstOrDefault(x => !string.IsNullOrWhiteSpace(preferredGroup) && x == preferredGroup)
                    ?? LoginGroups.FirstOrDefault(x => previousGroup is not null && x == previousGroup)
                    ?? LoginGroups.FirstOrDefault(x => Students.Any(s => s.Id == selectedId && s.Group == x))
                    ?? LoginGroups.FirstOrDefault();
        }
        finally
        {
            applyingLoginGroups = false;
        }

        RebuildLoginStudents(selectedId);
        LoginMessage = Students.Count == 0
            ? "Тьютор ещё не добавил учеников."
            : LoginGroups.Count == 0
                ? "Группы пока не назначены."
                : "Группа уже выбрана для урока. Можно сменить, если вы на отработке.";
    }

    public void ApplyPreferredGroup(string? preferredGroup)
    {
        if (string.IsNullOrWhiteSpace(preferredGroup) || !LoginGroups.Contains(preferredGroup))
            return;
        if (loginGroupChosenByUser && string.Equals(lastPreferredGroup, preferredGroup, StringComparison.Ordinal))
            return;
        lastPreferredGroup = preferredGroup;
        if (loginGroupChosenByUser) return;
        applyingLoginGroups = true;
        try { SelectedLoginGroup = preferredGroup; }
        finally { applyingLoginGroups = false; }
        RebuildLoginStudents(SelectedStudent?.Id);
    }

    partial void OnSelectedLoginGroupChanged(string? value)
    {
        if (!applyingLoginGroups)
            loginGroupChosenByUser = true;
        RebuildLoginStudents(SelectedStudent?.Id);
    }

    private void RebuildLoginStudents(Guid? preferredId)
    {
        LoginStudents.Clear();
        foreach (var student in Students.Where(x => SelectedLoginGroup is null || x.Group == SelectedLoginGroup))
            LoginStudents.Add(student);
        SelectedStudent = LoginStudents.FirstOrDefault(x => x.Id == preferredId) ?? LoginStudents.FirstOrDefault();
    }

    public void SetSyncState(StudentSyncState state) =>
        SyncLabel = state.PendingChanges > 0 ? $"{state.Status}: {state.PendingChanges}" : state.Status;

    public void SetVpnState(VpnStatus? status, string? detail = null)
    {
        if (status?.Connected == true)
        {
            VpnLabel = "Сеть класса: подключена";
            return;
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            VpnLabel = detail.Contains("VPN-служба не установлена", StringComparison.Ordinal)
                ? "Нужна разовая установка сети класса (с компьютера администратора)"
                : "Сеть класса пока не подключена";
            return;
        }

        VpnLabel = status?.ConfigExists == true ? "Сеть класса ещё не включена" : "Ждём команду тьютора";
    }

    public void SetUpdate(StudentUpdateInfo update)
    {
        HasUpdate = true;
        UpdateLabel = "Доступно обновление программы";
    }

    public void SetUpdateState(string state) => UpdateLabel = state;

    [RelayCommand]
    private void InstallUpdate()
    {
        if (!HasUpdate) return;
        HasUpdate = false;
        UpdateLabel = "Скачиваем и проверяем обновление…";
        UpdateRequested?.Invoke();
    }

    [RelayCommand]
    private void SubmitQuiz()
    {
        if (quizSessionId is null || SelectedQuizOption is null) { QuizFeedback = "Сначала выберите ответ."; return; }
        var index = QuizOptions.IndexOf(SelectedQuizOption);
        if (index < 0) return;
        QuizAnswerRequested?.Invoke(quizSessionId.Value, index);
        QuizFeedback = "Ответ отправляется тьютору…";
        IsQuizVisible = false;
    }

    [RelayCommand]
    private void ConfirmStudent()
    {
        if (SelectedLoginGroup is null) { LoginMessage = "Сначала выберите группу."; return; }
        if (SelectedStudent is null) { LoginMessage = "Затем выберите своё имя в группе."; return; }
        StudentSelected?.Invoke(SelectedStudent.Id);
        IsLoginVisible = false;
        CurrentStudentName = SelectedStudent.Name;
        CurrentStudentGroup = SelectedStudent.Group;
        CurrentStudentLevel = SelectedStudent.Level;
        CurrentStudentKiberons = SelectedStudent.Kiberons;
        OnPropertyChanged(nameof(LevelProgress));
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(LevelLabel));
        OnPropertyChanged(nameof(BalanceLabel));
        OnPropertyChanged(nameof(LevelProgressLabel));
        OnPropertyChanged(nameof(GoalProgress));
        OnPropertyChanged(nameof(GoalRemainder));
        LessonName = $"Добро пожаловать, {SelectedStudent.Name}";
    }

    [RelayCommand]
    private void DismissNotification() => IsNotificationVisible = false;

    [RelayCommand]
    private void Navigate(string? sectionIndex)
    {
        if (int.TryParse(sectionIndex, out var parsed)) SelectedSectionIndex = Math.Clamp(parsed, 0, 7);
    }

    public void SetTutorLessons(IReadOnlyList<TypingLessonOffer> lessons)
    {
        TutorLessons.Clear();
        foreach (var lesson in lessons.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            TutorLessons.Add(new TutorLessonCardViewModel(lesson));
        TutorLessonsStatus = TutorLessons.Count == 0
            ? (IsConnected ? "У тьютора пока нет уроков печати." : "Подключитесь к классу — уроки подтянутся сами.")
            : $"Доступно уроков от тьютора: {TutorLessons.Count}";
        OnPropertyChanged(nameof(HasTutorLessons));
        OnPropertyChanged(nameof(HasNoTutorLessons));
    }

    partial void OnIsConnectedChanged(bool value)
    {
        if (!value && TutorLessons.Count == 0)
            TutorLessonsStatus = "Подключитесь к классу — уроки подтянутся сами.";
    }

    [RelayCommand]
    private void SelectTutorLesson(TutorLessonCardViewModel? lesson)
    {
        if (lesson is null) return;
        LessonName = lesson.Name;
        ResetLesson(lesson.Text, lesson.MinimumCharacters);
        OpenTypingTrainer();
    }

    [RelayCommand]
    private void SelectPracticeLesson(string? lessonKey)
    {
        // Kept for older UI bindings; prefer Tutor catalog.
        if (TutorLessons.Count > 0)
        {
            SelectTutorLesson(TutorLessons[0]);
            return;
        }

        var lesson = lessonKey switch
        {
            "python" => ("Python · циклы", TypingLessonCatalog.Defaults[1].Text, TypingLessonCatalog.Defaults[1].MinimumCharacters),
            _ => ("Разминка", TypingLessonCatalog.Defaults[0].Text, 80)
        };
        LessonName = lesson.Item1;
        ResetLesson(lesson.Item2, lesson.Item3);
        OpenTypingTrainer();
    }

    [RelayCommand]
    private void ResumeLesson()
    {
        if (IsPaused)
        {
            TogglePause();
            return;
        }

        SelectedSectionIndex = 3;
    }

    [RelayCommand]
    private void PauseLesson()
    {
        if (IsLessonStarted && !IsPaused && !IsFinished) TogglePause();
    }

    [RelayCommand]
    private void StartAssignedLesson() => OpenTypingTrainer();

    private void OpenTypingTrainer()
    {
        SelectedSectionIndex = 3;
        IsLessonStarted = false;
        activeTime.Reset();
        StatusMessage = GoalCharacters < TargetText.Length
            ? $"Нажмите пробел, чтобы начать. Для зачёта достаточно {GoalCharacters} знаков."
            : "Нажмите пробел, чтобы начать урок.";
        LastInputFeedback = "Нажмите ПРОБЕЛ, чтобы начать";
        RebuildTypingPresentation();
    }

    [RelayCommand]
    private void FinishAttempt()
    {
        Finish();
        SelectedSectionIndex = 5;
    }

    [RelayCommand]
    private void RetryConnection()
    {
        ConnectionActionMessage = "Ищем Tutor в локальной сети… Обычно это занимает несколько секунд.";
        StatusMessage = "Повторно ищем Tutor в локальной сети…";
    }

    public CommandExecutionResult ApplyCommand(ClassroomCommand command)
    {
        switch (command.Kind)
        {
            case ClassroomCommandKinds.Message:
                NotificationText = command.Payload.TryGetProperty("text", out var message)
                    ? message.GetString() ?? "Новое сообщение"
                    : "Тьютор отправил сообщение без текста.";
                StatusMessage = $"Сообщение тьютора: {NotificationText}";
                IsNotificationVisible = true;
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.TypingStart:
                var text = command.Payload.TryGetProperty("text", out var textProperty) ? textProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) return new CommandExecutionResult(false, "В команде нет текста урока.");
                LessonName = command.Payload.TryGetProperty("lesson_name", out var nameProperty)
                    ? nameProperty.GetString() ?? "Назначенный урок"
                    : "Назначенный урок";
                int? minimum = null;
                if (command.Payload.TryGetProperty("minimum_characters", out var minimumProperty)
                    && minimumProperty.ValueKind == System.Text.Json.JsonValueKind.Number)
                    minimum = minimumProperty.GetInt32();
                ResetLesson(text, minimum);
                OpenTypingTrainer();
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.TypingFinish:
                Finish();
                SelectedSectionIndex = 5;
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.Configure:
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.LockScreen:
                IsScreenLocked = true;
                LockMessage = command.Payload.TryGetProperty("message", out var lockText) ? lockText.GetString() ?? LockMessage : LockMessage;
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.UnlockScreen:
                IsScreenLocked = false;
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.FocusOn:
                FocusEnabled?.Invoke();
                StatusMessage = "Тьютор оставил только нужные окна.";
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.FocusOff:
                FocusDisabled?.Invoke();
                StatusMessage = "Снова можно открывать все окна.";
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.WatchdogOn:
                WatchdogEnabled?.Invoke();
                StatusMessage = "Приложение теперь нельзя закрыть.";
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.WatchdogOff:
                WatchdogDisabled?.Invoke();
                StatusMessage = "Приложение снова можно закрыть.";
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.SetWorkspace:
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.InstallStarterPack:
            case ClassroomCommandKinds.SetWallpaper:
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.Notification:
                NotificationText = command.Payload.TryGetProperty("title", out var title) ? title.GetString() ?? "Получена награда" : "Получена награда";
                IsNotificationVisible = true;
                return CommandExecutionResult.Success;
            case ClassroomCommandKinds.QuizStart:
                if (!command.Payload.TryGetProperty("session_id", out var sessionProperty) || !sessionProperty.TryGetGuid(out var sessionId))
                    return new CommandExecutionResult(false, "В викторине отсутствует session_id.");
                if (!command.Payload.TryGetProperty("question", out var questionProperty) || !command.Payload.TryGetProperty("options", out var optionsProperty))
                    return new CommandExecutionResult(false, "Викторина заполнена не полностью.");
                QuizOptions.Clear();
                foreach (var option in optionsProperty.EnumerateArray())
                    if (option.GetString() is { } optionText) QuizOptions.Add(optionText);
                if (QuizOptions.Count < 2) return new CommandExecutionResult(false, "Недостаточно вариантов ответа.");
                quizSessionId = sessionId;
                QuizQuestion = questionProperty.GetString() ?? "Вопрос";
                SelectedQuizOption = null;
                QuizFeedback = "Выберите один вариант.";
                IsQuizVisible = true;
                return CommandExecutionResult.Success;
            default:
                return new CommandExecutionResult(false, $"Команда {command.Kind} пока не поддерживается этим экраном.");
        }
    }

    private void ResetLesson(string text, int? minimumCharacters = null)
    {
        TargetText = text;
        GoalCharacters = TypingLessonCatalog.SuggestGoalCharacters(text, minimumCharacters);
        GoalLabel = $"Зачёт: 0 / {GoalCharacters} знаков · в тексте {text.Length}";
        TypedText = string.Empty;
        CorrectKeys = 0;
        WrongKeys = 0;
        Cpm = 0;
        Accuracy = 100;
        Progress = 0;
        IsPaused = false;
        IsFinished = false;
        problemCharacters.Clear();
        typedResults.Clear();
        lastAttemptWasWrong = false;
        pauseTime.Reset();
        activeTime.Reset();
        IsLessonStarted = false;
        CurrentStreak = 0;
        BestStreak = 0;
        ElapsedLabel = "00:00";
        LastInputFeedback = "Нажмите ПРОБЕЛ, чтобы начать";
        LayoutWarning = string.Empty;
        HasLayoutWarning = false;
        CurrentCharacter = text.Length > 0 ? text[0].ToString() : "✓";
        StatusMessage = GoalCharacters < text.Length
            ? $"Для зачёта наберите {GoalCharacters} знаков. Весь текст печатать не обязательно."
            : "Урок назначен. Нажмите пробел, чтобы начать.";
        RebuildTypingPresentation();
    }

    private void Finish()
    {
        activeTime.Stop();
        pauseTime.Stop();
        IsFinished = true;
        StatusMessage = GoalCharacters < TargetText.Length
            ? $"Зачёт получен ({TypedText.Length} из {GoalCharacters} знаков). Можно остановиться или потренироваться дальше в следующий раз."
            : "Этап завершён. Результат готов к отправке тьютору.";
        UpdateMetrics();
        SelectedSectionIndex = 5;
    }

    private void UpdateMetrics()
    {
        Cpm = TypingMetrics.Cpm(CorrectKeys, activeTime.Elapsed.TotalSeconds);
        Accuracy = TypingMetrics.Accuracy(CorrectKeys, WrongKeys);
        Progress = TypingMetrics.Progress(TypedText.Length, GoalCharacters);
        GoalLabel = $"Зачёт: {Math.Min(TypedText.Length, GoalCharacters)} / {GoalCharacters} знаков · в тексте {TargetText.Length}";
        CurrentCharacter = TypedText.Length < TargetText.Length ? TargetText[TypedText.Length].ToString() : "✓";
        ElapsedLabel = $"{(int)activeTime.Elapsed.TotalMinutes:00}:{activeTime.Elapsed.Seconds:00}";
        RebuildTypingPresentation();
    }

    private void RebuildTypingPresentation()
    {
        TextGlyphs.Clear();
        // One active line only, with a short sliding window of characters.
        const int maxVisibleChars = 42;
        var (lineStart, lineEnd) = GetCurrentLineRange(TargetText, TypedText.Length);
        var caretInLine = Math.Clamp(TypedText.Length - lineStart, 0, Math.Max(0, lineEnd - lineStart));
        var lineLength = lineEnd - lineStart;
        var visibleStartInLine = 0;
        var visibleEndInLine = lineLength;
        if (lineLength > maxVisibleChars)
        {
            visibleStartInLine = Math.Max(0, caretInLine - maxVisibleChars / 3);
            visibleEndInLine = Math.Min(lineLength, visibleStartInLine + maxVisibleChars);
            visibleStartInLine = Math.Max(0, visibleEndInLine - maxVisibleChars);
        }

        var start = lineStart + visibleStartInLine;
        var end = lineStart + visibleEndInLine;
        for (var index = start; index < end; index++)
        {
            var state = index < typedResults.Count
                ? TypingGlyphState.Correct
                : index == TypedText.Length
                    ? lastAttemptWasWrong ? TypingGlyphState.Wrong : TypingGlyphState.Current
                    : TypingGlyphState.Pending;
            TextGlyphs.Add(new TypingGlyphViewModel(TargetText[index], state));
        }

        KeyboardRows.Clear();
        var current = TypedText.Length < TargetText.Length ? char.ToLowerInvariant(TargetText[TypedText.Length]) : '\0';
        var russianLayout = current is >= 'а' and <= 'я' or 'ё' || TargetText.Any(character => character is >= 'а' and <= 'я' or 'ё');
        var rows = russianLayout
            ? new[]
            {
                new[] { "ё", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "Backspace" },
                new[] { "Tab", "й", "ц", "у", "к", "е", "н", "г", "ш", "щ", "з", "х", "ъ", "\\" },
                new[] { "Caps", "ф", "ы", "в", "а", "п", "р", "о", "л", "д", "ж", "э", "Enter" },
                new[] { "Shift", "я", "ч", "с", "м", "и", "т", "ь", "б", "ю", ".", "Shift" },
                new[] { "Ctrl", "Alt", "ПРОБЕЛ", "Alt", "Ctrl" }
            }
            : new[]
            {
                new[] { "`", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "Backspace" },
                new[] { "Tab", "q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "[", "]", "\\" },
                new[] { "Caps", "a", "s", "d", "f", "g", "h", "j", "k", "l", ";", "'", "Enter" },
                new[] { "Shift", "z", "x", "c", "v", "b", "n", "m", ",", ".", "/", "Shift" },
                new[] { "Ctrl", "Alt", "SPACE", "Alt", "Ctrl" }
            };
        foreach (var row in rows)
        {
            var keys = row.Select(key => new KeyboardKeyViewModel(key, IsExpectedKey(key))).ToList();
            KeyboardRows.Add(new KeyboardRowViewModel(keys));
        }
    }

    private static (int Start, int End) GetCurrentLineRange(string text, int caret)
    {
        if (text.Length == 0)
            return (0, 0);

        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1);
        start = start < 0 ? 0 : start + 1;
        var end = text.IndexOf('\n', caret);
        if (end < 0)
            end = text.Length;
        return (start, end);
    }

    private bool IsExpectedKey(string key)
    {
        if (TypedText.Length >= TargetText.Length || TypedText.Length >= GoalCharacters) return false;
        var expected = char.ToLowerInvariant(TargetText[TypedText.Length]);
        if (char.IsWhiteSpace(expected)) return key is "ПРОБЕЛ" or "SPACE";
        return key.Length == 1 && char.ToLowerInvariant(key[0]) == expected;
    }

    private void DetectLayoutMismatch(char expected, char actual)
    {
        var ruToEn = new Dictionary<char, char>
        {
            ['й']='q',['ц']='w',['у']='e',['к']='r',['е']='t',['н']='y',['г']='u',['ш']='i',['щ']='o',['з']='p',
            ['ф']='a',['ы']='s',['в']='d',['а']='f',['п']='g',['р']='h',['о']='j',['л']='k',['д']='l',
            ['я']='z',['ч']='x',['с']='c',['м']='v',['и']='b',['т']='n',['ь']='m'
        };
        var normalizedExpected = char.ToLowerInvariant(expected);
        var normalizedActual = char.ToLowerInvariant(actual);
        var enToRu = ruToEn.ToDictionary(pair => pair.Value, pair => pair.Key);
        var expectsRussianButTypedEnglish = ruToEn.TryGetValue(normalizedExpected, out var latin) && latin == normalizedActual;
        var expectsEnglishButTypedRussian = enToRu.TryGetValue(normalizedExpected, out var russian) && russian == normalizedActual;
        HasLayoutWarning = expectsRussianButTypedEnglish || expectsEnglishButTypedRussian;
        LayoutWarning = expectsRussianButTypedEnglish
            ? "Проверьте раскладку: включён английский язык"
            : expectsEnglishButTypedRussian
                ? "Проверьте раскладку: включён русский язык"
                : string.Empty;
    }

    private static string Printable(char character) => char.IsWhiteSpace(character) ? "ПРОБЕЛ" : $"«{character}»";
    private static bool IsCyrillic(char character) => character is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё';
}

public enum TypingGlyphState { Pending, Current, Correct, Wrong }

public sealed class TypingGlyphViewModel(char character, TypingGlyphState state)
{
    public string Character { get; } = character.ToString();
    public bool IsWhitespace { get; } = char.IsWhiteSpace(character);
    public double GlyphMinWidth => IsWhitespace ? 16 : 0;
    public string Foreground => state switch { TypingGlyphState.Correct => "#087F5B", TypingGlyphState.Wrong => "#C9362B", TypingGlyphState.Current => "#13181D", _ => "#6B7880" };
    public string Background => state switch { TypingGlyphState.Correct => "#DDF7E9", TypingGlyphState.Wrong => "#FFE2DE", TypingGlyphState.Current => "#FFD52E", _ => "Transparent" };
    public string Decoration => state == TypingGlyphState.Wrong ? "Underline" : "None";
}

public sealed class KeyboardRowViewModel(IReadOnlyList<KeyboardKeyViewModel> keys)
{
    public IReadOnlyList<KeyboardKeyViewModel> Keys { get; } = keys;
}

public sealed class KeyboardKeyViewModel
{
    public KeyboardKeyViewModel(string label, bool isExpected)
    {
        Label = label;
        IsExpected = isExpected;
        Width = label switch { "ПРОБЕЛ" => 240, "Backspace" or "Caps" or "Enter" => 78, "Shift" => 96, "Tab" => 68, _ => 42 };
        Background = isExpected ? "#FFD52E" : FingerColor(label);
        BorderBrush = isExpected ? "#13181D" : "#CAD3D8";
    }

    public string Label { get; }
    public bool IsExpected { get; }
    public double Width { get; }
    public string Background { get; }
    public string BorderBrush { get; }
    private static string FingerColor(string key) => key.Length != 1 ? "#EDF1F3" : key[0] switch
    {
        'ё' or '`' or '1' or 'й' or 'q' or 'ф' or 'a' or 'я' or 'z' => "#FFDAD5",
        '2' or 'ц' or 'w' or 'ы' or 's' or 'ч' or 'x' => "#FFE9B8",
        '3' or 'у' or 'e' or 'в' or 'd' or 'с' or 'c' => "#DDF7E9",
        '4' or '5' or 'к' or 'r' or 'е' or 't' or 'а' or 'f' or 'п' or 'g' or 'м' or 'v' or 'и' or 'b' => "#D9F2F4",
        '6' or '7' or 'н' or 'y' or 'г' or 'u' or 'р' or 'h' or 'о' or 'j' or 'т' or 'n' or 'ь' or 'm' => "#E8DDF8",
        '8' or 'ш' or 'i' or 'л' or 'k' or 'б' or ',' => "#F7DDF0",
        '9' or 'щ' or 'o' or 'д' or 'l' or 'ю' or '.' => "#FFE5D5",
        _ => "#E2F3D8"
    };
}

public sealed class TutorLessonCardViewModel(TypingLessonOffer lesson)
{
    public Guid Id { get; } = lesson.Id;
    public string Name { get; } = lesson.Name;
    public string Description { get; } = string.IsNullOrWhiteSpace(lesson.Description)
        ? $"{lesson.DurationMinutes} мин · зачёт с {lesson.MinimumCharacters} знаков"
        : lesson.Description;
    public string Details { get; } = $"{lesson.KeyboardLayout} · {lesson.ContentKind} · зачёт {lesson.MinimumCharacters}";
    public string Text { get; } = lesson.Text;
    public int MinimumCharacters { get; } = lesson.MinimumCharacters;
}

public sealed class StudentChoiceViewModel(StudentSummary student)
{
    public Guid Id { get; } = student.Id;
    public string Name { get; } = student.DisplayName;
    public string Group { get; } = student.GroupName;
    public int Level { get; } = student.Level;
    public int Kiberons { get; } = student.Kiberons;
    public string Details { get; } = $"{student.GroupName} · уровень {student.Level} · {student.Kiberons} K";
    public override string ToString() => $"{Name} · {student.GroupName}";
}
