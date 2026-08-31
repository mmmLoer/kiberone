using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Avalonia.Media.Imaging;
using System.Text.Json;

namespace Kiberone.Tutor.ViewModels;

public partial class MainViewModel(TypingLessonService lessons, ClassroomService classroom, FileSyncService fileSync, AssetDistributionService assets, ClientRegistry clients, ReliableCommandQueue commandQueue, QuizService quizzes, AuditService audit) : ViewModelBase
{
    public ObservableCollection<LessonCardViewModel> Lessons { get; } = [];
    public ObservableCollection<GroupCardViewModel> Groups { get; } = [];
    public ObservableCollection<StudentCardViewModel> Students { get; } = [];
    public ObservableCollection<StudentCardViewModel> FilteredStudents { get; } = [];
    public ObservableCollection<AchievementCardViewModel> Achievements { get; } = [];
    public ObservableCollection<StoreItemCardViewModel> StoreItems { get; } = [];
    public ObservableCollection<SyncApprovalCardViewModel> SyncApprovals { get; } = [];
    public ObservableCollection<SyncClientCardViewModel> SyncClients { get; } = [];
    public ObservableCollection<SyncedFileCardViewModel> SyncedFiles { get; } = [];
    public ObservableCollection<FileVersionCardViewModel> FileVersions { get; } = [];
    public ObservableCollection<ScreenPreviewCardViewModel> ScreenPreviews { get; } = [];
    public ObservableCollection<AuditEventCardViewModel> AuditEvents { get; } = [];
    public ObservableCollection<WinnerCardViewModel> Winners { get; } = [];
    public ObservableCollection<LessonStepEditorViewModel> Steps { get; } =
    [
        new() { Title = "Разминка", Text = "Начните печатать текст урока здесь." }
    ];
    public IReadOnlyList<string> LessonKinds { get; } = Enum.GetNames<LessonContentKind>();
    public IReadOnlyList<string> KeyboardLayouts { get; } = ["ru-RU", "en-US"];

    [ObservableProperty] private string lessonName = string.Empty;
    [ObservableProperty] private string description = string.Empty;
    [ObservableProperty] private string selectedLessonKind = nameof(LessonContentKind.Custom);
    [ObservableProperty] private string selectedKeyboardLayout = "ru-RU";
    [ObservableProperty] private int minimumCharacters = 50;
    [ObservableProperty] private int durationMinutes = 10;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = "Подключаем локальную базу и сервер…";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private int connectedClientCount;
    [ObservableProperty] private string connectedClientLabel = "Нет учеников";
    [ObservableProperty] private string groupName = string.Empty;
    [ObservableProperty] private string groupModule = string.Empty;
    [ObservableProperty] private string groupTopics = string.Empty;
    [ObservableProperty] private GroupCardViewModel? selectedGroup;
    [ObservableProperty] private string studentLastName = string.Empty;
    [ObservableProperty] private string studentFirstName = string.Empty;
    [ObservableProperty] private int studentAge = 10;
    [ObservableProperty] private string studentComment = string.Empty;
    [ObservableProperty] private string studentBirthdayText = string.Empty;
    [ObservableProperty] private int studentLevel = 1;
    [ObservableProperty] private int studentKiberons;
    [ObservableProperty] private bool isEditingStudent;
    [ObservableProperty] private bool showClassScreens;
    [ObservableProperty] private bool showTypingStatistics;
    [ObservableProperty] private StudentCardViewModel? selectedStudent;
    [ObservableProperty] private AchievementCardViewModel? selectedAchievement;
    [ObservableProperty] private StoreItemCardViewModel? selectedStoreItem;
    [ObservableProperty] private string achievementCode = string.Empty;
    [ObservableProperty] private string achievementName = string.Empty;
    [ObservableProperty] private int achievementXp = 25;
    [ObservableProperty] private int achievementKiberons = 5;
    [ObservableProperty] private string storeSku = string.Empty;
    [ObservableProperty] private string storeItemName = string.Empty;
    [ObservableProperty] private int storePrice = 10;
    [ObservableProperty] private int storeStock = 1;
    [ObservableProperty] private SyncApprovalCardViewModel? selectedSyncApproval;
    [ObservableProperty] private SyncClientCardViewModel? selectedSyncClient;
    [ObservableProperty] private SyncedFileCardViewModel? selectedSyncedFile;
    [ObservableProperty] private FileVersionCardViewModel? selectedFileVersion;
    [ObservableProperty] private string classroomMessage = "Внимание: следуйте инструкциям тьютора.";
    [ObservableProperty] private bool isScreensLocked;
    [ObservableProperty] private bool isFocusModeOn;
    [ObservableProperty] private bool isWatchdogOn;
    [ObservableProperty] private bool isVpnOn;
    [ObservableProperty] private string quizTitle = "Новая викторина";
    [ObservableProperty] private int quizTimePerQuestion = 30;
    [ObservableProperty] private int quizXpReward = 10;
    [ObservableProperty] private bool quizShuffleAnswers;
    [ObservableProperty] private bool quizShowFeedback = true;
    [ObservableProperty] private bool showQuizSettings;
    [ObservableProperty] private string quizStatus = "Соберите вопросы и запустите выбранный класс.";
    [ObservableProperty] private QuizQuestionEditorViewModel? selectedQuizQuestion;
    public ObservableCollection<QuizQuestionEditorViewModel> QuizQuestions { get; } = [];
    [ObservableProperty] private string auditSearch = string.Empty;
    [ObservableProperty] private string? selectedAuditCategory;
    [ObservableProperty] private string groupStatisticsText = "Выберите группу и загрузите статистику.";
    [ObservableProperty] private string studentStatisticsText = "Выберите ученика и загрузите статистику.";
    [ObservableProperty] private bool showStatsForStudent;
    [ObservableProperty] private LessonFilterOption? selectedStatsLesson;
    [ObservableProperty] private string statsReportTitle = "Статистика печати";
    [ObservableProperty] private string statsSummaryText = "Выберите область и нажмите «Показать».";
    public ObservableCollection<LessonFilterOption> StatsLessonFilters { get; } = [];
    public ObservableCollection<ChartBarViewModel> StatsCpmBars { get; } = [];
    public ObservableCollection<ChartBarViewModel> StatsAccuracyBars { get; } = [];
    [ObservableProperty] private string liveLessonName = "Практика класса";
    [ObservableProperty] private string liveLessonText = "for i in range(10): print(i)";
    [ObservableProperty] private string liveLessonState = "Урок не запущен";
    [ObservableProperty] private bool hasLessonResultsNotification;
    [ObservableProperty] private string lessonResultsSummary = string.Empty;
    [ObservableProperty] private int screenRefreshSeconds = 30;
    [ObservableProperty] private int syncIntervalSeconds = 15;
    [ObservableProperty] private bool autoApproveSafeFiles = true;
    [ObservableProperty] private bool enableStudentUpdates = true;
    [ObservableProperty] private bool isDarkTheme;
    [ObservableProperty] private string locationName = "KIBERone Classroom";
    [ObservableProperty] private string settingsStatus = "Настройки действуют только на этом Tutor.";
    [ObservableProperty] private string vpnConfigsFolder = string.Empty;
    [ObservableProperty] private string vpnDistributionStatus = "Укажите папку с .conf файлами — конфиги раздадутся ученикам автоматически.";
    [ObservableProperty] private int selectedSectionIndex;

    public Func<Task<string?>>? VpnConfigsFolderPicker { get; set; }
    public Func<Task<string?>>? QuizExportPathPicker { get; set; }
    public Func<Task<string?>>? QuizImportPathPicker { get; set; }
    public Func<Task<string?>>? QuizMediaPathPicker { get; set; }

    public bool IsSection0 => SelectedSectionIndex == 0;
    public bool IsSection1 => SelectedSectionIndex == 1;
    public bool IsSection2 => SelectedSectionIndex == 2;
    public bool IsSection3 => SelectedSectionIndex == 3;
    public bool IsSection4 => SelectedSectionIndex == 4;
    public bool IsSection5 => SelectedSectionIndex == 5;
    public bool IsSection6 => SelectedSectionIndex == 6;
    public bool IsSection7 => SelectedSectionIndex == 7;
    public bool IsSection8 => SelectedSectionIndex == 8;
    public bool IsSection9 => SelectedSectionIndex == 9;
    public bool IsSection10 => SelectedSectionIndex == 10;
    public bool IsSection11 => SelectedSectionIndex == 11;
    public bool IsSection12 => SelectedSectionIndex == 12;
    public bool IsSection13 => SelectedSectionIndex == 13;
    public bool IsSection14 => SelectedSectionIndex == 14;
    public bool IsSection15 => SelectedSectionIndex == 15;
    public bool IsSection16 => SelectedSectionIndex == 16;
    public bool IsSection17 => SelectedSectionIndex == 17;
    public bool IsSection18 => SelectedSectionIndex == 18;

    public bool HasLessons => Lessons.Count > 0;
    public bool HasNoLessons => !HasLessons;
    public bool HasStudents => Students.Count > 0;
    public bool HasNoStudents => !HasStudents;
    public bool HasFilteredStudents => FilteredStudents.Count > 0;
    public bool HasNoFilteredStudents => !HasFilteredStudents;
    public bool HasGroups => Groups.Count > 0;
    public bool HasNoGroups => !HasGroups;
    public bool HasScreenPreviews => ScreenPreviews.Count > 0;
    public bool HasNoScreenPreviews => !HasScreenPreviews;
    public bool ShowClassRoster => !ShowClassScreens;
    public bool ShowTypingEditor => !ShowTypingStatistics;
    public string ClassViewToggleLabel => ShowClassScreens ? "Показать список" : "Показать экраны";
    public string TypingViewToggleLabel => ShowTypingStatistics ? "К редактору уроков" : "К статистике";
    public bool IsClassRosterMode => !ShowClassScreens;
    public bool IsClassScreensMode => ShowClassScreens;
    public bool IsTypingEditorMode => !ShowTypingStatistics;
    public bool IsTypingStatsMode => ShowTypingStatistics;
    public bool ShowStatsForGroup => !ShowStatsForStudent;
    public bool HasStatsBars => StatsCpmBars.Count > 0;
    public bool HasNoStatsBars => !HasStatsBars;
    public bool ShowQuizQuestions => !ShowQuizSettings;
    public bool HasSelectedQuizQuestion => SelectedQuizQuestion is not null;
    public bool HasNoSelectedQuizQuestion => !HasSelectedQuizQuestion;
    public string ScreensLockLabel => IsScreensLocked ? "Экраны заблокированы. Нажмите, чтобы разблокировать." : "Заблокировать экраны учеников.";
    public string FocusModeLabel => IsFocusModeOn ? "Фокус включён. Нажмите, чтобы выключить." : "Ограничить отвлекающие окна.";
    public string WatchdogLabel => IsWatchdogOn ? "Watchdog следит за Student. Нажмите, чтобы выключить." : "Перезапуск Student, если его закрыли.";
    public string VpnToggleLabel => IsVpnOn ? "VPN включён на классе. Нажмите, чтобы отключить." : "Подключить WireGuard на всех ПК.";
    public string ThemeToggleLabel => IsDarkTheme ? "Светлая тема" : "Тёмная тема";
    public string StudentFormTitle => IsEditingStudent ? "Редактировать ученика" : "Новый ученик";
    public string StudentFormActionLabel => IsEditingStudent ? "Сохранить изменения" : "Добавить ученика";
    public bool HasAuditEvents => AuditEvents.Count > 0;
    public bool HasNoAuditEvents => !HasAuditEvents;
    public string ServerAddress => "http://0.0.0.0:8765";
    public string DiscoveryAddress => "UDP 8766 · локальная сеть";
    public string VersionLabel => $"Tutor {BuildInfo.Version}";
    public IReadOnlyList<string> AuditCategories { get; } = ["Все", "Синхронизация", "Магазин", "Печать", "Викторина", "Команды", "Ученики", "Система"];

    public async Task InitializeAsync()
    {
        try
        {
            LoadSettings();
            EnsureQuizSeed();
            await RefreshCoreAsync();
            await RefreshScreensAsync();
            StatusMessage = Lessons.Count == 0
                ? "Создайте первый урок — он сохранится в локальной базе."
                : $"Загружено уроков: {Lessons.Count}";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = $"Не удалось открыть базу: {error.Message}";
        }
    }

    public void RefreshClients()
    {
        var all = clients.GetAll();
        var online = all.Where(client => client.IsOnline).ToList();
        ConnectedClientCount = online.Count;
        var vpnCount = online.Count(client => client.Extra.VpnConnected);
        ConnectedClientLabel = ConnectedClientCount switch
        {
            0 => "Нет учеников",
            1 => vpnCount == 1 ? "1 ученик онлайн · VPN вкл" : "1 ученик онлайн",
            _ => vpnCount > 0
                ? $"{ConnectedClientCount} учеников онлайн · VPN: {vpnCount}"
                : $"{ConnectedClientCount} учеников онлайн"
        };
        ApplyPresence(all);
        RefreshVpnDistributionStatus(online);
        OnPropertyChanged(nameof(SectionSubtitle));
    }

    private void ApplyPresence(IReadOnlyList<ClassroomClientSnapshot> snapshots)
    {
        foreach (var student in Students)
        {
            var match = snapshots
                .Where(client => client.StudentId == student.Id)
                .OrderByDescending(client => client.IsOnline)
                .ThenByDescending(client => client.LastSeenAt)
                .FirstOrDefault();
            student.ApplyPresence(match);
        }
    }

    private void RefreshVpnDistributionStatus(IReadOnlyList<ClassroomClientSnapshot> onlineClients)
    {
        if (string.IsNullOrWhiteSpace(VpnConfigsFolder) || !Directory.Exists(VpnConfigsFolder))
        {
            VpnDistributionStatus = string.IsNullOrWhiteSpace(VpnConfigsFolder)
                ? "Укажите папку с .conf файлами — конфиги раздадутся ученикам автоматически."
                : $"Папка не найдена: {VpnConfigsFolder}";
            return;
        }

        var configCount = Directory.GetFiles(VpnConfigsFolder, "*.conf", SearchOption.TopDirectoryOnly).Length;
        var assignments = VpnConfigDistributor.Assign(onlineClients, VpnConfigsFolder);
        VpnDistributionStatus = VpnConfigDistributor.DescribeAssignments(assignments, onlineClients.Count, configCount);
    }

    partial void OnVpnConfigsFolderChanged(string value) => RefreshVpnDistributionStatus(clients.GetAll().Where(client => client.IsOnline).ToList());

    public Task RefreshScreensAsync()
    {
        foreach (var card in ScreenPreviews) card.Dispose();
        ScreenPreviews.Clear();
        foreach (var client in clients.GetAll().OrderBy(x => x.PcNumber))
        {
            using var stream = assets.OpenScreen(client.ClientId);
            Bitmap? preview = null;
            if (stream is not null)
            {
                try { preview = new Bitmap(stream); } catch { preview = null; }
            }
            ScreenPreviews.Add(new ScreenPreviewCardViewModel(client, preview));
        }
        NotifyCollectionStates();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshScreenGridAsync() => RefreshScreensAsync();

    public string SectionTitle => SelectedSectionIndex switch
    {
        0 => "Класс сейчас", 1 => "Настройка урока печати", 2 => "Ученики и группы",
        3 => "Награды и магазин", 4 => "Сохранения и версии", 5 => "Сетка экранов",
        6 => "Пульт класса", 7 => "Урок печати в эфире", 8 => "Итоги урока",
        9 => "Викторины", 10 => "Статистика группы", 11 => "Сервер и локация",
        12 => "Модули и версии", 13 => "Сравнение и восстановление", 14 => "Цели и каталог",
        15 => "Настройки", 16 => "Достижения и награды", 17 => "Журнал аудита", _ => "Статистика"
    };
    public string SectionSubtitle => SelectedSectionIndex switch
    {
        0 => $"Текущий класс · {ConnectedClientLabel}", 1 => "Создание текста и запуск для группы",
        2 => "Группы, ученики и персональные карточки", 3 => "Достижения, кибероны и товары",
        4 => "Версии проектов и восстановление", 5 => "Наблюдение за классом · LAN",
        6 => "Карточки команд: фокус, VPN, Watchdog", 7 => "Python · группа 01 · 10:00",
        8 => "Итоги теперь в уведомлениях класса", 9 => "Редактор вопросов · экспорт JSON",
        10 => "Сводная прогрессия · Python", 11 => "Резервная копия · локальная база главная",
        12 => "Архивы разделены по учебным модулям", 13 => "Текстовый diff · snapshot перед восстановлением",
        14 => "Трекер накопления · баланс не списывается", 15 => "Общие параметры локации и класса",
        16 => "Правила XP и киберонов", 17 => "Действия тьюторов, компьютеров и локации", _ => "Прогресс учеников и групп"
    };

    partial void OnSelectedSectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(SectionSubtitle));
        for (var index = 0; index <= 18; index++)
            OnPropertyChanged($"IsSection{index}");
    }

    [RelayCommand]
    private void Navigate(string? sectionIndex)
    {
        if (!int.TryParse(sectionIndex, out var parsed))
        {
            HasError = true;
            StatusMessage = "Не удалось открыть раздел: неверный индекс навигации.";
            return;
        }

        SelectedSectionIndex = Math.Clamp(parsed, 0, 18);
        HasError = false;
    }

    [RelayCommand] private void LockAll() => SendClassCommand(ClassroomCommandKinds.LockScreen, new { message = ClassroomMessage });
    [RelayCommand] private void UnlockAll() => SendClassCommand(ClassroomCommandKinds.UnlockScreen, new { });
    [RelayCommand] private void FocusAllOn() => SendClassCommand(ClassroomCommandKinds.FocusOn, new { });
    [RelayCommand] private void FocusAllOff() => SendClassCommand(ClassroomCommandKinds.FocusOff, new { });
    [RelayCommand] private void WatchdogAllOn() => SendClassCommand(ClassroomCommandKinds.WatchdogOn, new { });
    [RelayCommand] private void WatchdogAllOff() => SendClassCommand(ClassroomCommandKinds.WatchdogOff, new { });
    [RelayCommand] private void SyncAllNow() => SendClassCommand(ClassroomCommandKinds.SyncNow, new { });
    [RelayCommand] private Task VpnAllOnAsync() => EnableVpnForClassAsync();
    [RelayCommand] private void VpnAllOff() => SendClassCommand(ClassroomCommandKinds.VpnDisconnect, new { });
    [RelayCommand] private void SendMessageAll() => SendClassCommand(ClassroomCommandKinds.Message, new { text = ClassroomMessage });

    [RelayCommand]
    private void ToggleScreensLock()
    {
        if (IsScreensLocked) UnlockAll();
        else LockAll();
        IsScreensLocked = !IsScreensLocked;
        NotifyConsoleToggles();
    }

    [RelayCommand]
    private void ToggleFocusMode()
    {
        if (IsFocusModeOn) FocusAllOff();
        else FocusAllOn();
        IsFocusModeOn = !IsFocusModeOn;
        NotifyConsoleToggles();
    }

    [RelayCommand]
    private void ToggleWatchdog()
    {
        if (IsWatchdogOn) WatchdogAllOff();
        else WatchdogAllOn();
        IsWatchdogOn = !IsWatchdogOn;
        NotifyConsoleToggles();
    }

    [RelayCommand]
    private async Task ToggleVpnAsync()
    {
        if (IsVpnOn)
        {
            VpnAllOff();
            IsVpnOn = false;
        }
        else
        {
            await VpnAllOnAsync();
            IsVpnOn = true;
        }

        NotifyConsoleToggles();
    }

    private void NotifyConsoleToggles()
    {
        OnPropertyChanged(nameof(ScreensLockLabel));
        OnPropertyChanged(nameof(FocusModeLabel));
        OnPropertyChanged(nameof(WatchdogLabel));
        OnPropertyChanged(nameof(VpnToggleLabel));
    }

    [RelayCommand]
    private async Task PickVpnConfigsFolderAsync()
    {
        if (VpnConfigsFolderPicker is null)
        {
            HasError = true;
            StatusMessage = "Выбор папки недоступен в этом окне.";
            return;
        }

        var selected = await VpnConfigsFolderPicker();
        if (string.IsNullOrWhiteSpace(selected))
            return;

        VpnConfigsFolder = selected;
        SaveSettings();
        HasError = false;
        StatusMessage = $"Папка VPN-конфигов: {selected}";
    }

    [RelayCommand]
    private void StartLiveLesson()
    {
        if (string.IsNullOrWhiteSpace(LiveLessonText)) { ShowSelectionError("Введите текст живого урока."); return; }
        SendClassCommand(ClassroomCommandKinds.TypingStart, new { lesson_name = LiveLessonName, text = LiveLessonText });
        LiveLessonState = $"Идёт урок «{LiveLessonName}» · {ConnectedClientCount} подключено";
    }

    [RelayCommand]
    private void FinishLiveLesson()
    {
        SendClassCommand(ClassroomCommandKinds.TypingFinish, new { });
        LiveLessonState = "Урок завершён · результаты зафиксированы";
        RefreshWinners();
        HasLessonResultsNotification = Winners.Count > 0;
        LessonResultsSummary = HasLessonResultsNotification
            ? $"Три награды после «{LiveLessonName}» · показаны в классе"
            : "Пока нет учеников для наград — добавьте состав класса.";
        SelectedSectionIndex = 0;
        StatusMessage = HasLessonResultsNotification
            ? "Итоги урока добавлены в уведомления на «Класс сейчас»."
            : LiveLessonState;
    }

    [RelayCommand]
    private void RefreshWinners()
    {
        Winners.Clear();
        var place = 1;
        foreach (var student in Students.OrderByDescending(x => x.Xp).ThenBy(x => x.Name).Take(3))
            Winners.Add(new WinnerCardViewModel(place++, student.Name, student.Group, student.Xp));
        if (Winners.Count == 0) SettingsStatus = "Победители появятся после добавления учеников.";
    }

    [RelayCommand]
    private void DismissLessonResults()
    {
        HasLessonResultsNotification = false;
        LessonResultsSummary = string.Empty;
    }

    [RelayCommand] private void StudentLock(StudentCardViewModel? student) => SendStudentCommand(student, ClassroomCommandKinds.LockScreen, new { message = ClassroomMessage });
    [RelayCommand] private void StudentUnlock(StudentCardViewModel? student) => SendStudentCommand(student, ClassroomCommandKinds.UnlockScreen, new { });
    [RelayCommand] private void StudentWatchdogOn(StudentCardViewModel? student) => SendStudentCommand(student, ClassroomCommandKinds.WatchdogOn, new { });
    [RelayCommand] private void StudentWatchdogOff(StudentCardViewModel? student) => SendStudentCommand(student, ClassroomCommandKinds.WatchdogOff, new { });
    [RelayCommand] private void StudentFocusOn(StudentCardViewModel? student) => SendStudentCommand(student, ClassroomCommandKinds.FocusOn, new { });
    [RelayCommand] private void StudentFocusOff(StudentCardViewModel? student) => SendStudentCommand(student, ClassroomCommandKinds.FocusOff, new { });
    [RelayCommand] private void StudentSync(StudentCardViewModel? student) => SendStudentCommand(student, ClassroomCommandKinds.SyncNow, new { });
    [RelayCommand] private void StudentMessage(StudentCardViewModel? student) => SendStudentCommand(student, ClassroomCommandKinds.Message, new { text = ClassroomMessage });

    [RelayCommand]
    private void OpenStudentFolder(StudentCardViewModel? student)
    {
        var clientId = ResolveStudentClientId(student);
        if (clientId is null) { ShowSelectionError("ПК ученика ещё не подключался — папки на сервере нет."); return; }
        OpenSyncFolder(clientId, student?.Name);
    }

    [RelayCommand]
    private void OpenStudentFromClass(StudentCardViewModel? student)
    {
        if (student is null) return;
        SelectedSectionIndex = 2;
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == student.GroupId) ?? SelectedGroup;
        RebuildFilteredStudents();
        BeginEditStudent(student);
    }

    [RelayCommand] private void PcLock(ScreenPreviewCardViewModel? pc) => SendPcCommand(pc, ClassroomCommandKinds.LockScreen, new { message = ClassroomMessage });
    [RelayCommand] private void PcUnlock(ScreenPreviewCardViewModel? pc) => SendPcCommand(pc, ClassroomCommandKinds.UnlockScreen, new { });
    [RelayCommand] private void PcWatchdogOn(ScreenPreviewCardViewModel? pc) => SendPcCommand(pc, ClassroomCommandKinds.WatchdogOn, new { });
    [RelayCommand] private void PcWatchdogOff(ScreenPreviewCardViewModel? pc) => SendPcCommand(pc, ClassroomCommandKinds.WatchdogOff, new { });
    [RelayCommand] private void PcFocusOn(ScreenPreviewCardViewModel? pc) => SendPcCommand(pc, ClassroomCommandKinds.FocusOn, new { });
    [RelayCommand] private void PcFocusOff(ScreenPreviewCardViewModel? pc) => SendPcCommand(pc, ClassroomCommandKinds.FocusOff, new { });
    [RelayCommand] private void PcSync(ScreenPreviewCardViewModel? pc) => SendPcCommand(pc, ClassroomCommandKinds.SyncNow, new { });
    [RelayCommand] private void PcMessage(ScreenPreviewCardViewModel? pc) => SendPcCommand(pc, ClassroomCommandKinds.Message, new { text = ClassroomMessage });

    [RelayCommand]
    private void OpenPcFolder(ScreenPreviewCardViewModel? pc)
    {
        if (pc is null || string.IsNullOrWhiteSpace(pc.ClientId))
        {
            ShowSelectionError("Неизвестный ПК.");
            return;
        }

        OpenSyncFolder(pc.ClientId, pc.Title);
    }

    private void SendStudentCommand(StudentCardViewModel? student, string kind, object payload)
    {
        var clientId = ResolveStudentClientId(student);
        if (clientId is null)
        {
            ShowSelectionError(student is null
                ? "Выберите ученика."
                : $"ПК для {student.Name} не найден. Ученик должен быть онлайн хотя бы раз.");
            return;
        }

        try
        {
            SendClientCommand(clientId, kind, payload);
            HasError = false;
            StatusMessage = $"{student!.Name}: команда {kind} · {clientId}";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    private void SendPcCommand(ScreenPreviewCardViewModel? pc, string kind, object payload)
    {
        if (pc is null || string.IsNullOrWhiteSpace(pc.ClientId))
        {
            ShowSelectionError("Выберите ПК.");
            return;
        }

        try
        {
            SendClientCommand(pc.ClientId, kind, payload);
            HasError = false;
            StatusMessage = $"{pc.Title}: команда {kind}";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    private string? ResolveStudentClientId(StudentCardViewModel? student)
    {
        if (student is null) return null;
        if (!string.IsNullOrWhiteSpace(student.ClientId)) return student.ClientId;
        return clients.GetAll()
            .Where(client => client.StudentId == student.Id)
            .OrderByDescending(client => client.IsOnline)
            .ThenByDescending(client => client.LastSeenAt)
            .Select(client => client.ClientId)
            .FirstOrDefault();
    }

    private void OpenSyncFolder(string clientId, string? label)
    {
        try
        {
            var path = fileSync.GetClientFolderPath(clientId);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            HasError = false;
            StatusMessage = $"Открыта папка {(string.IsNullOrWhiteSpace(label) ? clientId : label)}.";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    private bool loadingSettings;

    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    partial void OnIsDarkThemeChanged(bool value)
    {
        ApplyThemeVariant(value);
        OnPropertyChanged(nameof(ThemeToggleLabel));
        if (!loadingSettings)
            SaveSettings();
    }

    private static void ApplyThemeVariant(bool dark)
    {
        if (Avalonia.Application.Current is null) return;
        Avalonia.Application.Current.RequestedThemeVariant = dark
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        ScreenRefreshSeconds = Math.Clamp(ScreenRefreshSeconds, 5, 300);
        SyncIntervalSeconds = Math.Clamp(SyncIntervalSeconds, 5, 600);
        var settings = new TutorLocalSettings(
            LocationName.Trim(),
            ScreenRefreshSeconds,
            SyncIntervalSeconds,
            AutoApproveSafeFiles,
            EnableStudentUpdates,
            VpnConfigsFolder.Trim(),
            IsDarkTheme);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone", "Tutor");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        SettingsStatus = $"Настройки сохранены · {DateTime.Now:t}";
    }

    private void LoadSettings()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone", "Tutor", "settings.json");
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<TutorLocalSettings>(File.ReadAllText(path));
            if (saved is null) return;
            loadingSettings = true;
            LocationName = saved.LocationName;
            ScreenRefreshSeconds = saved.ScreenRefreshSeconds;
            SyncIntervalSeconds = saved.SyncIntervalSeconds;
            AutoApproveSafeFiles = saved.AutoApproveSafeFiles;
            EnableStudentUpdates = saved.EnableStudentUpdates;
            VpnConfigsFolder = saved.VpnConfigsFolder ?? string.Empty;
            IsDarkTheme = saved.PreferDarkTheme;
            SettingsStatus = "Локальные настройки загружены.";
        }
        catch (Exception error)
        {
            SettingsStatus = $"Настройки не загружены: {error.Message}";
        }
        finally
        {
            loadingSettings = false;
        }
    }

    [RelayCommand]
    private async Task StartQuizAsync()
    {
        try
        {
            if (SelectedQuizQuestion is null)
            {
                ShowSelectionError("Выберите вопрос для запуска.");
                return;
            }

            var rawOptions = SelectedQuizQuestion.Options.ToList();
            var trimmedCorrect = -1;
            var trimmed = new List<string>();
            for (var i = 0; i < rawOptions.Count; i++)
            {
                var text = rawOptions[i].Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (rawOptions[i].IsCorrect) trimmedCorrect = trimmed.Count;
                trimmed.Add(text);
            }

            if (trimmedCorrect < 0 && trimmed.Count > 0) trimmedCorrect = 0;

            var session = await quizzes.StartAsync(new StartQuizRequest(
                SelectedQuizQuestion.Text,
                trimmed,
                trimmedCorrect,
                QuizXpReward,
                ["__all__"],
                QuizTimePerQuestion,
                QuizShuffleAnswers,
                QuizShowFeedback));
            QuizStatus = $"Вопрос запущен · {session.Id.ToString("N")[..8]} · вариантов: {trimmed.Count} · {QuizTimePerQuestion} с.";
            StatusMessage = QuizStatus;
            HasError = false;
        }
        catch (LessonValidationException validation) { HasError = true; QuizStatus = string.Join(" ", validation.Errors); }
        catch (Exception error) { HasError = true; QuizStatus = error.Message; }
    }

    [RelayCommand]
    private void ShowQuizQuestionsPane()
    {
        ShowQuizSettings = false;
        OnPropertyChanged(nameof(ShowQuizQuestions));
    }

    [RelayCommand]
    private void ShowQuizSettingsPane()
    {
        ShowQuizSettings = true;
        OnPropertyChanged(nameof(ShowQuizQuestions));
    }

    [RelayCommand]
    private void NewQuiz()
    {
        QuizTitle = "Новая викторина";
        QuizTimePerQuestion = 30;
        QuizXpReward = 10;
        QuizShuffleAnswers = false;
        QuizShowFeedback = true;
        QuizQuestions.Clear();
        SelectedQuizQuestion = null;
        AddQuizQuestion();
        QuizStatus = "Создана новая викторина.";
        NotifyQuizSelection();
    }

    [RelayCommand]
    private void AddQuizQuestion()
    {
        var question = QuizQuestionEditorViewModel.CreateBlank(QuizQuestions.Count + 1);
        QuizQuestions.Add(question);
        SelectQuizQuestion(question);
        RenumberQuizQuestions();
    }

    [RelayCommand]
    private void RemoveQuizQuestion()
    {
        if (SelectedQuizQuestion is null) return;
        var index = QuizQuestions.IndexOf(SelectedQuizQuestion);
        QuizQuestions.Remove(SelectedQuizQuestion);
        SelectedQuizQuestion = QuizQuestions.ElementAtOrDefault(Math.Max(0, index - 1)) ?? QuizQuestions.FirstOrDefault();
        foreach (var item in QuizQuestions) item.IsSelected = item == SelectedQuizQuestion;
        RenumberQuizQuestions();
        NotifyQuizSelection();
        if (QuizQuestions.Count == 0) AddQuizQuestion();
    }

    [RelayCommand]
    private void SelectQuizQuestion(QuizQuestionEditorViewModel? question)
    {
        if (question is null) return;
        foreach (var item in QuizQuestions) item.IsSelected = item == question;
        SelectedQuizQuestion = question;
        NotifyQuizSelection();
    }

    [RelayCommand]
    private void AddQuizOption()
    {
        SelectedQuizQuestion?.AddOption();
    }

    [RelayCommand]
    private async Task PickQuizMediaAsync()
    {
        if (SelectedQuizQuestion is null || QuizMediaPathPicker is null) return;
        var path = await QuizMediaPathPicker();
        if (string.IsNullOrWhiteSpace(path)) return;
        SelectedQuizQuestion.MediaPath = path;
    }

    [RelayCommand]
    private void ClearQuizMedia()
    {
        if (SelectedQuizQuestion is null) return;
        SelectedQuizQuestion.MediaPath = null;
    }

    [RelayCommand]
    private void SaveQuizDraft()
    {
        try
        {
            var document = BuildQuizDocument();
            var directory = GetQuizLibraryDirectory();
            Directory.CreateDirectory(directory);
            var fileName = SanitizeFileName(document.Title) + ".json";
            var path = Path.Combine(directory, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(document, QuizJsonOptions));
            File.WriteAllText(Path.Combine(directory, "draft.json"), JsonSerializer.Serialize(document, QuizJsonOptions));
            QuizStatus = $"Сохранено локально: {path}";
            HasError = false;
        }
        catch (Exception error)
        {
            HasError = true;
            QuizStatus = error.Message;
        }
    }

    [RelayCommand]
    private async Task ExportQuizAsync()
    {
        try
        {
            if (QuizExportPathPicker is null)
            {
                HasError = true;
                QuizStatus = "Экспорт недоступен в этом окне.";
                return;
            }

            var path = await QuizExportPathPicker();
            if (string.IsNullOrWhiteSpace(path)) return;
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                path += ".json";
            File.WriteAllText(path, JsonSerializer.Serialize(BuildQuizDocument(), QuizJsonOptions));
            QuizStatus = $"Экспортировано: {path}";
            HasError = false;
        }
        catch (Exception error)
        {
            HasError = true;
            QuizStatus = error.Message;
        }
    }

    [RelayCommand]
    private async Task ImportQuizAsync()
    {
        try
        {
            if (QuizImportPathPicker is null)
            {
                HasError = true;
                QuizStatus = "Импорт недоступен в этом окне.";
                return;
            }

            var path = await QuizImportPathPicker();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var document = JsonSerializer.Deserialize<QuizDocument>(File.ReadAllText(path), QuizJsonOptions)
                ?? throw new InvalidOperationException("Файл викторины пуст или повреждён.");
            ApplyQuizDocument(document);
            QuizStatus = $"Импортировано: {document.Title} · вопросов: {QuizQuestions.Count}";
            HasError = false;
            ShowQuizQuestionsPane();
        }
        catch (Exception error)
        {
            HasError = true;
            QuizStatus = error.Message;
        }
    }

    private void EnsureQuizSeed()
    {
        if (QuizQuestions.Count > 0) return;
        var draftPath = Path.Combine(GetQuizLibraryDirectory(), "draft.json");
        if (File.Exists(draftPath))
        {
            try
            {
                var document = JsonSerializer.Deserialize<QuizDocument>(File.ReadAllText(draftPath), QuizJsonOptions);
                if (document is not null)
                {
                    ApplyQuizDocument(document);
                    return;
                }
            }
            catch { /* fall through to blank quiz */ }
        }

        NewQuiz();
    }

    private QuizDocument BuildQuizDocument() => new()
    {
        Title = string.IsNullOrWhiteSpace(QuizTitle) ? "Новая викторина" : QuizTitle.Trim(),
        TimePerQuestionSeconds = Math.Clamp(QuizTimePerQuestion, 5, 300),
        XpReward = Math.Clamp(QuizXpReward, 0, 1000),
        ShuffleAnswers = QuizShuffleAnswers,
        ShowFeedback = QuizShowFeedback,
        Questions = QuizQuestions.Select(q => q.ToDocumentQuestion()).ToList()
    };

    private void ApplyQuizDocument(QuizDocument document)
    {
        QuizTitle = document.Title;
        QuizTimePerQuestion = Math.Clamp(document.TimePerQuestionSeconds, 5, 300);
        QuizXpReward = Math.Clamp(document.XpReward, 0, 1000);
        QuizShuffleAnswers = document.ShuffleAnswers;
        QuizShowFeedback = document.ShowFeedback;
        QuizQuestions.Clear();
        var index = 1;
        foreach (var question in document.Questions)
            QuizQuestions.Add(QuizQuestionEditorViewModel.FromDocument(question, index++));
        SelectedQuizQuestion = QuizQuestions.FirstOrDefault();
        foreach (var item in QuizQuestions) item.IsSelected = item == SelectedQuizQuestion;
        if (QuizQuestions.Count == 0) AddQuizQuestion();
        NotifyQuizSelection();
    }

    private void RenumberQuizQuestions()
    {
        for (var i = 0; i < QuizQuestions.Count; i++)
            QuizQuestions[i].SetNumber(i + 1);
    }

    private void NotifyQuizSelection()
    {
        OnPropertyChanged(nameof(HasSelectedQuizQuestion));
        OnPropertyChanged(nameof(HasNoSelectedQuizQuestion));
        OnPropertyChanged(nameof(ShowQuizQuestions));
    }

    private static string GetQuizLibraryDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone", "Tutor", "quizzes");

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "quiz" : cleaned;
    }

    private static readonly JsonSerializerOptions QuizJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    partial void OnSelectedQuizQuestionChanged(QuizQuestionEditorViewModel? value) => NotifyQuizSelection();
    partial void OnShowQuizSettingsChanged(bool value) => OnPropertyChanged(nameof(ShowQuizQuestions));

    [RelayCommand]
    private async Task RefreshAuditAsync()
    {
        var category = SelectedAuditCategory is null or "Все" ? null : SelectedAuditCategory;
        AuditEvents.Clear();
        foreach (var entry in await audit.ListAsync(new AuditQuery(category, AuditSearch, 500))) AuditEvents.Add(new AuditEventCardViewModel(entry));
        NotifyCollectionStates();
    }

    [RelayCommand]
    private void ShowStatsGroupScope()
    {
        ShowStatsForStudent = false;
        OnPropertyChanged(nameof(ShowStatsForGroup));
    }

    [RelayCommand]
    private void ShowStatsStudentScope()
    {
        ShowStatsForStudent = true;
        OnPropertyChanged(nameof(ShowStatsForGroup));
    }

    [RelayCommand]
    private async Task LoadGroupStatisticsAsync()
    {
        if (SelectedGroup is null) { ShowSelectionError("Выберите группу."); return; }
        var stats = await classroom.GetGroupStatisticsAsync(SelectedGroup.Id);
        GroupStatisticsText = stats is null ? "Группа не найдена." :
            $"{stats.GroupName}\nУченики: {stats.StudentCount}\nСредняя оценка: {stats.AverageGrade:0.##}\nВсего XP: {stats.TotalXp:N0}\nКибероны: {stats.TotalKiberons:N0}\nПосещения: {stats.SessionCount}\nДостижения: {stats.AchievementCount}";
        await RefreshTypingChartsAsync();
    }

    [RelayCommand]
    private async Task LoadStudentStatisticsAsync()
    {
        if (SelectedStudent is null) { ShowSelectionError("Выберите ученика."); return; }
        var stats = await classroom.GetStudentStatisticsAsync(SelectedStudent.Id);
        StudentStatisticsText = stats is null ? "Ученик не найден." :
            $"{stats.DisplayName}\nГруппа: {stats.GroupName}\nУровень: {stats.Level} · {stats.Xp} XP\nБаланс: {stats.Kiberons} K\nСредняя оценка: {stats.AverageGrade:0.##} ({stats.GradeCount})\nПосещения: {stats.SessionCount}\nДостижения: {stats.AchievementCount}\nПокупки: {stats.PurchaseCount}";
        await RefreshTypingChartsAsync();
    }

    [RelayCommand]
    private async Task RefreshTypingChartsAsync()
    {
        try
        {
            Guid? groupId = ShowStatsForStudent ? null : SelectedGroup?.Id;
            Guid? studentId = ShowStatsForStudent ? SelectedStudent?.Id : null;
            if (ShowStatsForStudent && studentId is null) { ShowSelectionError("Выберите ученика."); return; }
            if (!ShowStatsForStudent && groupId is null) { ShowSelectionError("Выберите группу."); return; }

            var lessonId = SelectedStatsLesson?.Id;
            var report = await lessons.GetTypingStatsAsync(groupId, studentId, lessonId);
            StatsReportTitle = report.Title;
            RebuildStatsBars(report.Points);

            if (ShowStatsForStudent)
            {
                var card = await classroom.GetStudentStatisticsAsync(studentId!.Value);
                StatsSummaryText = card is null
                    ? "Нет данных по ученику."
                    : $"{card.DisplayName} · ур. {card.Level} · {card.Xp} XP · {card.Kiberons} K\n" +
                      $"Уроков в графике: {report.Points.Count} · попыток: {report.Points.Sum(x => x.Attempts)}";
                StudentStatisticsText = StatsSummaryText;
            }
            else
            {
                var card = await classroom.GetGroupStatisticsAsync(groupId!.Value);
                StatsSummaryText = card is null
                    ? "Нет данных по группе."
                    : $"{card.GroupName} · {card.StudentCount} уч. · XP {card.TotalXp:N0} · {card.TotalKiberons:N0} K\n" +
                      $"Уроков в графике: {report.Points.Count} · попыток: {report.Points.Sum(x => x.Attempts)}";
                GroupStatisticsText = StatsSummaryText;
            }

            if (report.Points.Count == 0)
                StatsSummaryText += "\nПока нет завершённых уроков печати для выбранного фильтра.";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    private void RebuildStatsBars(IReadOnlyList<TypingLessonStatPoint> points)
    {
        StatsCpmBars.Clear();
        StatsAccuracyBars.Clear();
        var maxCpm = points.Count == 0 ? 1 : Math.Max(1, points.Max(x => x.AverageCpm));
        foreach (var point in points)
        {
            var cpmHeight = Math.Clamp(point.AverageCpm / maxCpm * 140, 8, 140);
            var accHeight = Math.Clamp(point.AverageAccuracy / 100.0 * 140, 8, 140);
            StatsCpmBars.Add(new ChartBarViewModel(point.LessonName, $"{point.AverageCpm:0.#}", cpmHeight, "#068F8A"));
            StatsAccuracyBars.Add(new ChartBarViewModel(point.LessonName, $"{point.AverageAccuracy:0.#}%", accHeight, "#1F9E61"));
        }
        OnPropertyChanged(nameof(HasStatsBars));
        OnPropertyChanged(nameof(HasNoStatsBars));
    }

    private void SendClassCommand(string kind, object payload)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var command = commandQueue.Enqueue(new EnqueueCommandRequest(["__all__"], kind, document.RootElement));
            HasError = false;
            StatusMessage = $"Команда {kind} поставлена в очередь · {command.Id.ToString("N")[..8]}.";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    private void SendClientCommand(string clientId, string kind, object payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        commandQueue.Enqueue(new EnqueueCommandRequest([clientId], kind, document.RootElement));
    }

    private async Task EnableVpnForClassAsync()
    {
        try
        {
            var online = clients.GetAll().Where(client => client.IsOnline).ToList();
            if (online.Count == 0)
            {
                HasError = true;
                StatusMessage = "Нет онлайн-учеников для включения VPN.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(VpnConfigsFolder) && Directory.Exists(VpnConfigsFolder))
            {
                var assignments = VpnConfigDistributor.Assign(online, VpnConfigsFolder);
                if (assignments.Count == 0)
                {
                    HasError = true;
                    StatusMessage = "В папке нет .conf файлов или не удалось сопоставить конфиги с учениками.";
                    RefreshVpnDistributionStatus(online);
                    return;
                }

                foreach (var assignment in assignments)
                {
                    var content = await File.ReadAllBytesAsync(assignment.ConfigFilePath);
                    SendClientCommand(
                        assignment.ClientId,
                        ClassroomCommandKinds.VpnInstallConfig,
                        new
                        {
                            config_base64 = Convert.ToBase64String(content),
                            source_name = assignment.ConfigFileName,
                            auto_connect = true
                        });
                }

                var configCount = Directory.GetFiles(VpnConfigsFolder, "*.conf", SearchOption.TopDirectoryOnly).Length;
                VpnDistributionStatus = VpnConfigDistributor.DescribeAssignments(assignments, online.Count, configCount);
                HasError = false;
                StatusMessage = $"VPN: {VpnDistributionStatus}";
                return;
            }

            SendClassCommand(ClassroomCommandKinds.VpnConnect, new { });
            HasError = true;
            StatusMessage = "Папка с VPN-конфигами не указана. Укажите её в поле выше или установите peer.conf вручную на каждом ПК.";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    [RelayCommand]
    private void AddStep() => Steps.Add(new LessonStepEditorViewModel { Title = $"Этап {Steps.Count + 1}" });

    [RelayCommand]
    private void RemoveLastStep()
    {
        if (Steps.Count > 1) Steps.RemoveAt(Steps.Count - 1);
    }

    [RelayCommand]
    private async Task CreateLessonAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        HasError = false;
        try
        {
            var kind = Enum.TryParse<LessonContentKind>(SelectedLessonKind, out var parsed) ? parsed : LessonContentKind.Custom;
            var request = new CreateLessonRequest(
                LessonName, Description, kind, SelectedKeyboardLayout, MinimumCharacters, DurationMinutes,
                Steps.Select(x => new LessonStepDraft(x.Title, x.Text, x.TargetCpm > 0 ? x.TargetCpm : null,
                    x.TargetAccuracy > 0 ? x.TargetAccuracy : null)).ToList());
            var lesson = await lessons.CreateLessonAsync(request);
            StatusMessage = $"Урок «{lesson.Name}» сохранён. Версия {lesson.Version}.";
            LessonName = string.Empty;
            Description = string.Empty;
            Steps.Clear();
            Steps.Add(new LessonStepEditorViewModel { Title = "Разминка" });
            await RefreshCoreAsync();
        }
        catch (LessonValidationException validation)
        {
            HasError = true;
            StatusMessage = string.Join(" ", validation.Errors);
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = $"Не удалось сохранить урок: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync();

    [RelayCommand]
    private void ToggleClassScreens()
    {
        ShowClassScreens = !ShowClassScreens;
        NotifyViewModes();
        if (ShowClassScreens) _ = RefreshScreensAsync();
    }

    [RelayCommand]
    private void ShowClassRosterView()
    {
        ShowClassScreens = false;
        NotifyViewModes();
    }

    [RelayCommand]
    private void ShowClassScreensView()
    {
        ShowClassScreens = true;
        NotifyViewModes();
        _ = RefreshScreensAsync();
    }

    [RelayCommand]
    private void ToggleTypingStatistics()
    {
        ShowTypingStatistics = !ShowTypingStatistics;
        NotifyViewModes();
    }

    [RelayCommand]
    private void ShowTypingEditorView()
    {
        ShowTypingStatistics = false;
        NotifyViewModes();
    }

    [RelayCommand]
    private void ShowTypingStatsView()
    {
        ShowTypingStatistics = true;
        NotifyViewModes();
    }

    private void NotifyViewModes()
    {
        OnPropertyChanged(nameof(ShowClassRoster));
        OnPropertyChanged(nameof(ShowTypingEditor));
        OnPropertyChanged(nameof(ClassViewToggleLabel));
        OnPropertyChanged(nameof(TypingViewToggleLabel));
        OnPropertyChanged(nameof(IsClassRosterMode));
        OnPropertyChanged(nameof(IsClassScreensMode));
        OnPropertyChanged(nameof(IsTypingEditorMode));
        OnPropertyChanged(nameof(IsTypingStatsMode));
    }

    [RelayCommand]
    private void SelectGroup(GroupCardViewModel? group)
    {
        if (group is null) return;
        SelectedGroup = group;
        MarkSelectedGroup();
        RebuildFilteredStudents();
        StatusMessage = $"Группа «{group.Name}»: {FilteredStudents.Count} учеников.";
    }

    [RelayCommand]
    private void BeginEditStudent(StudentCardViewModel? student)
    {
        if (student is null) return;
        SelectedStudent = student;
        MarkSelectedStudent();
        IsEditingStudent = true;
        StudentLastName = student.LastName;
        StudentFirstName = student.FirstName;
        StudentAge = student.Age ?? 10;
        StudentComment = string.Empty;
        StudentBirthdayText = student.Birthday?.ToString("dd.MM.yyyy") ?? string.Empty;
        StudentLevel = Math.Max(1, student.Level);
        StudentKiberons = student.Kiberons;
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == student.GroupId) ?? SelectedGroup;
        MarkSelectedGroup();
        OnPropertyChanged(nameof(StudentFormTitle));
        OnPropertyChanged(nameof(StudentFormActionLabel));
    }

    [RelayCommand]
    private void BeginCreateStudent()
    {
        IsEditingStudent = false;
        StudentLastName = StudentFirstName = StudentComment = StudentBirthdayText = string.Empty;
        StudentAge = 10;
        StudentLevel = 1;
        StudentKiberons = 0;
        MarkSelectedStudent();
        OnPropertyChanged(nameof(StudentFormTitle));
        OnPropertyChanged(nameof(StudentFormActionLabel));
    }

    private void MarkSelectedGroup()
    {
        foreach (var group in Groups)
            group.IsSelected = SelectedGroup is not null && group.Id == SelectedGroup.Id;
    }

    private void MarkSelectedStudent()
    {
        foreach (var student in Students)
            student.IsSelected = IsEditingStudent && SelectedStudent is not null && student.Id == SelectedStudent.Id;
    }

    [RelayCommand]
    private async Task CreateGroupAsync()
    {
        await RunActionAsync(async () =>
        {
            var group = await classroom.CreateGroupAsync(new GroupDraft(GroupName, GroupModule, GroupTopics));
            GroupName = GroupModule = GroupTopics = string.Empty;
            StatusMessage = $"Группа «{group.Name}» создана.";
        });
    }

    [RelayCommand]
    private async Task DeleteGroupAsync()
    {
        if (SelectedGroup is null)
        {
            ShowSelectionError("Выберите группу для удаления.");
            return;
        }

        var name = SelectedGroup.Name;
        var id = SelectedGroup.Id;
        await RunActionAsync(async () =>
        {
            if (!await classroom.DeleteGroupAsync(id))
                throw new KeyNotFoundException("Группа не найдена.");
            StatusMessage = $"Группа «{name}» удалена.";
        });
    }

    [RelayCommand]
    private async Task SaveStudentAsync()
    {
        if (SelectedGroup is null)
        {
            HasError = true;
            StatusMessage = "Сначала выберите группу ученика.";
            return;
        }

        DateOnly? birthday = null;
        if (!string.IsNullOrWhiteSpace(StudentBirthdayText))
        {
            if (!DateOnly.TryParse(StudentBirthdayText.Trim(), out var parsed) &&
                !DateOnly.TryParseExact(StudentBirthdayText.Trim(), ["dd.MM.yyyy", "yyyy-MM-dd"], null, System.Globalization.DateTimeStyles.None, out parsed))
            {
                HasError = true;
                StatusMessage = "Дата рождения: используйте ДД.ММ.ГГГГ.";
                return;
            }
            birthday = parsed;
        }

        var level = Math.Clamp(StudentLevel, 1, 100);
        var xp = (level - 1) * 100;
        var draft = new StudentDraft(
            StudentLastName,
            StudentFirstName,
            StudentAge,
            SelectedGroup.Id,
            StudentComment,
            string.Empty,
            string.Empty,
            birthday,
            Math.Max(0, StudentKiberons),
            xp);

        await RunActionAsync(async () =>
        {
            if (IsEditingStudent && SelectedStudent is not null)
            {
                var updated = await classroom.UpdateStudentAsync(SelectedStudent.Id, draft)
                    ?? throw new KeyNotFoundException("Ученик не найден.");
                StatusMessage = $"Карточка {updated.DisplayName} обновлена.";
            }
            else
            {
                var student = await classroom.CreateStudentAsync(draft);
                StatusMessage = $"Ученик {student.DisplayName} добавлен.";
            }
            BeginCreateStudent();
        });
    }

    [RelayCommand]
    private async Task DeleteStudentAsync()
    {
        if (SelectedStudent is null)
        {
            ShowSelectionError("Выберите ученика для удаления.");
            return;
        }
        var name = SelectedStudent.Name;
        var id = SelectedStudent.Id;
        await RunActionAsync(async () =>
        {
            if (!await classroom.DeleteStudentAsync(id))
                throw new KeyNotFoundException("Ученик не найден.");
            StatusMessage = $"Ученик {name} удалён.";
            BeginCreateStudent();
        });
    }

    [RelayCommand]
    private Task CreateAchievementAsync() => RunActionAsync(async () =>
    {
        var created = await classroom.CreateAchievementAsync(new AchievementDraft(AchievementCode, AchievementName, string.Empty, "star", AchievementXp, AchievementKiberons));
        AchievementCode = AchievementName = string.Empty;
        StatusMessage = $"Достижение «{created.Name}» создано.";
    });

    [RelayCommand]
    private async Task AwardAchievementAsync()
    {
        if (SelectedStudent is null || SelectedAchievement is null) { ShowSelectionError("Выберите ученика и достижение."); return; }
        await RunActionAsync(async () =>
        {
            await classroom.AwardAchievementAsync(new AwardAchievementRequest(SelectedStudent.Id, SelectedAchievement.Id, "Выдано тьютором"));
            StatusMessage = $"Награда выдана ученику {SelectedStudent.Name}.";
        });
    }

    [RelayCommand]
    private async Task AddKiberonsAsync()
    {
        if (SelectedStudent is null) { ShowSelectionError("Выберите ученика."); return; }
        await RunActionAsync(async () =>
        {
            await classroom.AdjustKiberonsAsync(new AdjustKiberonsRequest(SelectedStudent.Id, 10, "Награда тьютора"));
            StatusMessage = $"Ученику {SelectedStudent.Name} начислено 10 киберонов.";
        });
    }

    [RelayCommand]
    private Task CreateStoreItemAsync() => RunActionAsync(async () =>
    {
        var created = await classroom.CreateStoreItemAsync(new StoreItemDraft(StoreSku, StoreItemName, string.Empty, StorePrice, StoreStock, false));
        StoreSku = StoreItemName = string.Empty;
        StatusMessage = $"Товар «{created.Name}» добавлен.";
    });

    [RelayCommand]
    private async Task PurchaseAsync()
    {
        if (SelectedStudent is null || SelectedStoreItem is null) { ShowSelectionError("Выберите ученика и товар."); return; }
        await RunActionAsync(async () =>
        {
            await classroom.PurchaseAsync(new PurchaseRequest(SelectedStudent.Id, SelectedStoreItem.Id));
            StatusMessage = $"Покупка для {SelectedStudent.Name} оформлена.";
        });
    }

    [RelayCommand]
    private async Task ApproveSyncAsync()
    {
        if (SelectedSyncApproval is null) { ShowSelectionError("Выберите запрос синхронизации."); return; }
        await RunActionAsync(async () =>
        {
            await fileSync.DecideAsync(SelectedSyncApproval.Id, true);
            StatusMessage = $"Синхронизация {SelectedSyncApproval.ClientId} разрешена.";
        });
    }

    [RelayCommand]
    private async Task RejectSyncAsync()
    {
        if (SelectedSyncApproval is null) { ShowSelectionError("Выберите запрос синхронизации."); return; }
        await RunActionAsync(async () =>
        {
            await fileSync.DecideAsync(SelectedSyncApproval.Id, false);
            StatusMessage = $"Синхронизация {SelectedSyncApproval.ClientId} отклонена.";
        });
    }

    [RelayCommand]
    private async Task LoadSyncedFilesAsync()
    {
        if (SelectedSyncClient is null) { ShowSelectionError("Выберите компьютер ученика."); return; }
        SyncedFiles.Clear();
        foreach (var file in await fileSync.ListFilesAsync(SelectedSyncClient.ClientId)) SyncedFiles.Add(new SyncedFileCardViewModel(file));
        SelectedSyncedFile = SyncedFiles.FirstOrDefault();
        FileVersions.Clear();
        StatusMessage = $"Файлов на сервере: {SyncedFiles.Count}.";
    }

    [RelayCommand]
    private async Task LoadVersionsAsync()
    {
        if (SelectedSyncClient is null || SelectedSyncedFile is null) { ShowSelectionError("Выберите компьютер и файл."); return; }
        FileVersions.Clear();
        foreach (var version in await fileSync.ListVersionsAsync(SelectedSyncClient.ClientId, SelectedSyncedFile.Path)) FileVersions.Add(new FileVersionCardViewModel(version));
        SelectedFileVersion = FileVersions.FirstOrDefault();
        StatusMessage = $"Версий файла: {FileVersions.Count}.";
    }

    [RelayCommand]
    private async Task RestoreVersionAsync()
    {
        if (SelectedSyncClient is null || SelectedSyncedFile is null || SelectedFileVersion is null) { ShowSelectionError("Выберите версию для восстановления."); return; }
        await RunActionAsync(async () =>
        {
            await fileSync.RestoreVersionAsync(new RestoreVersionRequest(SelectedSyncClient.ClientId, SelectedSyncedFile.Path, SelectedFileVersion.Id));
            StatusMessage = $"Версия {SelectedFileVersion.Label} восстановлена на сервере.";
        });
    }

    private void ShowSelectionError(string message)
    {
        HasError = true;
        StatusMessage = message;
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        HasError = false;
        try
        {
            await action();
            await RefreshCoreAsync();
        }
        catch (LessonValidationException validation)
        {
            HasError = true;
            StatusMessage = string.Join(" ", validation.Errors);
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshCoreAsync()
    {
        var stored = await lessons.ListLessonsAsync();
        Lessons.Clear();
        foreach (var lesson in stored) Lessons.Add(new LessonCardViewModel(lesson));
        var selectedLessonFilter = SelectedStatsLesson?.Id;
        StatsLessonFilters.Clear();
        StatsLessonFilters.Add(new LessonFilterOption(null, "Все уроки"));
        foreach (var lesson in stored) StatsLessonFilters.Add(new LessonFilterOption(lesson.Id, lesson.Name));
        SelectedStatsLesson = StatsLessonFilters.FirstOrDefault(x => x.Id == selectedLessonFilter) ?? StatsLessonFilters.FirstOrDefault();
        var storedGroups = await classroom.ListGroupsAsync();
        var selectedId = SelectedGroup?.Id;
        Groups.Clear();
        foreach (var group in storedGroups) Groups.Add(new GroupCardViewModel(group));
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == selectedId) ?? Groups.FirstOrDefault();
        MarkSelectedGroup();
        var selectedStudentId = SelectedStudent?.Id;
        var storedStudents = await classroom.ListStudentsAsync();
        Students.Clear();
        foreach (var student in storedStudents) Students.Add(new StudentCardViewModel(student));
        SelectedStudent = Students.FirstOrDefault(x => x.Id == selectedStudentId) ?? Students.FirstOrDefault();
        ApplyPresence(clients.GetAll());
        RebuildFilteredStudents();
        MarkSelectedStudent();
        RefreshWinners();
        var selectedAchievementId = SelectedAchievement?.Id;
        Achievements.Clear();
        foreach (var achievement in await classroom.ListAchievementsAsync()) Achievements.Add(new AchievementCardViewModel(achievement));
        SelectedAchievement = Achievements.FirstOrDefault(x => x.Id == selectedAchievementId) ?? Achievements.FirstOrDefault();
        var selectedItemId = SelectedStoreItem?.Id;
        StoreItems.Clear();
        foreach (var item in await classroom.ListStoreItemsAsync(true)) StoreItems.Add(new StoreItemCardViewModel(item));
        SelectedStoreItem = StoreItems.FirstOrDefault(x => x.Id == selectedItemId) ?? StoreItems.FirstOrDefault();
        var selectedApprovalId = SelectedSyncApproval?.Id;
        SyncApprovals.Clear();
        foreach (var approval in await fileSync.ListPendingApprovalsAsync()) SyncApprovals.Add(new SyncApprovalCardViewModel(approval));
        SelectedSyncApproval = SyncApprovals.FirstOrDefault(x => x.Id == selectedApprovalId) ?? SyncApprovals.FirstOrDefault();
        var selectedClientId = SelectedSyncClient?.ClientId;
        SyncClients.Clear();
        foreach (var client in clients.GetAll()) SyncClients.Add(new SyncClientCardViewModel(client));
        SelectedSyncClient = SyncClients.FirstOrDefault(x => x.ClientId == selectedClientId) ?? SyncClients.FirstOrDefault();
        if (SelectedAuditCategory is null) SelectedAuditCategory = "Все";
        await RefreshAuditAsync();
        NotifyCollectionStates();
    }

    private void RebuildFilteredStudents()
    {
        FilteredStudents.Clear();
        IEnumerable<StudentCardViewModel> source = Students;
        if (SelectedGroup is not null)
            source = Students.Where(x => x.GroupId == SelectedGroup.Id);
        foreach (var student in source) FilteredStudents.Add(student);
        if (SelectedStudent is not null && FilteredStudents.All(x => x.Id != SelectedStudent.Id))
            SelectedStudent = FilteredStudents.FirstOrDefault();
        OnPropertyChanged(nameof(HasFilteredStudents));
        OnPropertyChanged(nameof(HasNoFilteredStudents));
    }

    partial void OnSelectedGroupChanged(GroupCardViewModel? value)
    {
        MarkSelectedGroup();
        RebuildFilteredStudents();
    }

    private void NotifyCollectionStates()
    {
        OnPropertyChanged(nameof(HasLessons));
        OnPropertyChanged(nameof(HasNoLessons));
        OnPropertyChanged(nameof(HasStudents));
        OnPropertyChanged(nameof(HasNoStudents));
        OnPropertyChanged(nameof(HasFilteredStudents));
        OnPropertyChanged(nameof(HasNoFilteredStudents));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasNoGroups));
        OnPropertyChanged(nameof(HasScreenPreviews));
        OnPropertyChanged(nameof(HasNoScreenPreviews));
        OnPropertyChanged(nameof(HasAuditEvents));
        OnPropertyChanged(nameof(HasNoAuditEvents));
        OnPropertyChanged(nameof(HasStatsBars));
        OnPropertyChanged(nameof(HasNoStatsBars));
        OnPropertyChanged(nameof(ShowStatsForGroup));
        NotifyViewModes();
        OnPropertyChanged(nameof(StudentFormTitle));
        OnPropertyChanged(nameof(StudentFormActionLabel));
    }
}

public partial class LessonStepEditorViewModel : ObservableObject
{
    [ObservableProperty] private string title = string.Empty;
    [ObservableProperty] private string text = string.Empty;
    [ObservableProperty] private int targetCpm;
    [ObservableProperty] private decimal targetAccuracy;
}

public sealed class LessonCardViewModel(TypingLessonTemplate lesson)
{
    public Guid Id { get; } = lesson.Id;
    public string Name { get; } = lesson.Name;
    public string Details { get; } = $"{lesson.Steps.Count} этапов · {lesson.DurationMinutes} мин · {lesson.MinimumCharacters} знаков";
    public string Kind { get; } = lesson.ContentKind.ToString();
    public string Version { get; } = $"v{lesson.Version}";
}

public partial class GroupCardViewModel : ObservableObject
{
    public GroupCardViewModel(ClassroomGroup group)
    {
        Id = group.Id;
        Name = group.Name;
        Details = $"{group.Students.Count} учеников · {(string.IsNullOrWhiteSpace(group.Module) ? "модуль не задан" : group.Module)}";
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Details { get; }
    [ObservableProperty] private bool isSelected;
    public override string ToString() => Name;
}

public sealed class LessonFilterOption(Guid? id, string name)
{
    public Guid? Id { get; } = id;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

public sealed class ChartBarViewModel(string label, string valueLabel, double height, string color)
{
    public string Label { get; } = label;
    public string ValueLabel { get; } = valueLabel;
    public double Height { get; } = height;
    public string Color { get; } = color;
}

public partial class StudentCardViewModel : ObservableObject
{
    public StudentCardViewModel(StudentSummary student)
    {
        Id = student.Id;
        GroupId = student.GroupId;
        LastName = string.IsNullOrWhiteSpace(student.LastName) ? SplitName(student.DisplayName).Last : student.LastName;
        FirstName = string.IsNullOrWhiteSpace(student.FirstName) ? SplitName(student.DisplayName).First : student.FirstName;
        Name = student.DisplayName;
        Group = student.GroupName;
        Age = student.Age;
        Birthday = student.Birthday;
        Kiberons = student.Kiberons;
        Xp = student.Xp;
        Level = student.Level;
        Progress = $"Уровень {student.Level} · {student.Xp} XP";
        Balance = $"{student.Kiberons} K";
        ApplyPresence(null);
    }

    public Guid Id { get; }
    public Guid GroupId { get; }
    public string LastName { get; }
    public string FirstName { get; }
    public string Name { get; }
    public string Group { get; }
    public int? Age { get; }
    public DateOnly? Birthday { get; }
    public int Kiberons { get; }
    public int Xp { get; }
    public int Level { get; }
    public string Progress { get; }
    public string Balance { get; }

    [ObservableProperty] private bool isOnline;
    [ObservableProperty] private bool isOffline = true;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string presenceLabel = "оффлайн";
    [ObservableProperty] private string presenceColor = "#9AA7AE";
    [ObservableProperty] private string batteryLabel = "—";
    [ObservableProperty] private string detailsLine = "оффлайн · батарея —";
    [ObservableProperty] private string? clientId;
    [ObservableProperty] private string pcLabel = "ПК не привязан";
    [ObservableProperty] private string watchFolder = string.Empty;
    public bool HasLinkedPc => !string.IsNullOrWhiteSpace(ClientId);
    public string QuickActionsTitle => HasLinkedPc ? $"{Name} · {PcLabel}" : $"{Name} · ПК не найден";

    public void ApplyPresence(ClassroomClientSnapshot? client)
    {
        ClientId = client?.ClientId;
        PcLabel = client is null
            ? "ПК не привязан"
            : $"ПК {client.PcNumber} · {client.Hostname}";
        WatchFolder = client?.WatchFolder ?? string.Empty;
        IsOnline = client?.IsOnline == true;
        IsOffline = !IsOnline;
        PresenceLabel = IsOnline ? "онлайн" : "оффлайн";
        PresenceColor = IsOnline ? "#068F8A" : "#9AA7AE";
        BatteryLabel = client?.Extra.BatteryPercent is int pct ? $"{pct}%" : "—";
        DetailsLine = $"{PresenceLabel} · батарея {BatteryLabel}";
        OnPropertyChanged(nameof(HasLinkedPc));
        OnPropertyChanged(nameof(QuickActionsTitle));
    }

    public override string ToString() => Name;

    private static (string Last, string First) SplitName(string displayName)
    {
        var parts = displayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => ("", ""),
            1 => (parts[0], ""),
            _ => (parts[0], parts[1])
        };
    }
}

public sealed record TutorLocalSettings(
    string LocationName,
    int ScreenRefreshSeconds,
    int SyncIntervalSeconds,
    bool AutoApproveSafeFiles,
    bool EnableStudentUpdates,
    string? VpnConfigsFolder = "",
    bool PreferDarkTheme = false);

public partial class QuizQuestionEditorViewModel : ObservableObject
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    [ObservableProperty] private string text = string.Empty;
    [ObservableProperty] private string? mediaPath;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string numberLabel = "1";
    [ObservableProperty] private string preview = "Новый вопрос";
    public ObservableCollection<QuizOptionEditorViewModel> Options { get; } = [];

    public string MediaLabel => string.IsNullOrWhiteSpace(MediaPath) ? "Изображение или видео не выбрано" : MediaPath;

    public static QuizQuestionEditorViewModel CreateBlank(int number)
    {
        var question = new QuizQuestionEditorViewModel();
        question.SetNumber(number);
        question.Text = $"Вопрос {number}";
        foreach (var letter in new[] { "A", "B", "C", "D" })
            question.Options.Add(new QuizOptionEditorViewModel(question, letter, string.Empty, letter == "A"));
        question.RefreshPreview();
        return question;
    }

    public static QuizQuestionEditorViewModel FromDocument(QuizDocumentQuestion source, int number)
    {
        var question = new QuizQuestionEditorViewModel
        {
            Id = source.Id == Guid.Empty ? Guid.NewGuid() : source.Id,
            Text = source.Text,
            MediaPath = source.MediaPath
        };
        question.SetNumber(number);
        var options = source.Options.Count == 0 ? new List<string> { "", "" } : source.Options;
        for (var i = 0; i < options.Count; i++)
        {
            var letter = ((char)('A' + i)).ToString();
            question.Options.Add(new QuizOptionEditorViewModel(question, letter, options[i], i == source.CorrectIndex));
        }

        question.RefreshPreview();
        return question;
    }

    public void SetNumber(int number) => NumberLabel = number.ToString();

    public void AddOption()
    {
        if (Options.Count >= 6) return;
        var letter = ((char)('A' + Options.Count)).ToString();
        Options.Add(new QuizOptionEditorViewModel(this, letter, string.Empty, false));
    }

    public void MarkCorrect(QuizOptionEditorViewModel option)
    {
        foreach (var item in Options)
            item.IsCorrect = item == option;
    }

    public QuizDocumentQuestion ToDocumentQuestion()
    {
        var filled = Options.Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
        var correct = filled.FindIndex(x => x.IsCorrect);
        return new QuizDocumentQuestion
        {
            Id = Id,
            Text = Text.Trim(),
            MediaPath = string.IsNullOrWhiteSpace(MediaPath) ? null : MediaPath,
            Options = filled.Select(x => x.Text.Trim()).ToList(),
            CorrectIndex = correct < 0 ? 0 : correct
        };
    }

    partial void OnTextChanged(string value) => RefreshPreview();
    partial void OnMediaPathChanged(string? value)
    {
        OnPropertyChanged(nameof(MediaLabel));
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var trimmed = Text.Trim();
        Preview = string.IsNullOrWhiteSpace(trimmed) ? "Пустой вопрос" : trimmed;
    }
}

public partial class QuizOptionEditorViewModel : ObservableObject
{
    private readonly QuizQuestionEditorViewModel owner;

    public QuizOptionEditorViewModel(QuizQuestionEditorViewModel owner, string letter, string text, bool isCorrect)
    {
        this.owner = owner;
        Letter = letter;
        Text = text;
        IsCorrect = isCorrect;
    }

    public string Letter { get; }
    [ObservableProperty] private string text;
    [ObservableProperty] private bool isCorrect;
    public bool IsIncorrect => !IsCorrect;

    [RelayCommand]
    private void MarkCorrect() => owner.MarkCorrect(this);

    partial void OnIsCorrectChanged(bool value) => OnPropertyChanged(nameof(IsIncorrect));
}

public sealed class WinnerCardViewModel(int place, string name, string group, int xp)
{
    public string Place { get; } = place switch { 1 => "1 место", 2 => "2 место", _ => "3 место" };
    public string Name { get; } = name;
    public string Details { get; } = $"{group} · {xp:N0} XP";
}

public sealed class AchievementCardViewModel(Achievement achievement)
{
    public Guid Id { get; } = achievement.Id;
    public string Name { get; } = achievement.Name;
    public string Reward { get; } = $"+{achievement.XpReward} XP · +{achievement.KiberonReward} K";
    public override string ToString() => Name;
}

public sealed class StoreItemCardViewModel(StoreItem item)
{
    public Guid Id { get; } = item.Id;
    public string Name { get; } = item.Name;
    public string Details { get; } = $"{item.Price} K · остаток {item.Stock}";
    public override string ToString() => Name;
}

public sealed class SyncApprovalCardViewModel(SyncApproval approval)
{
    public Guid Id { get; } = approval.Id;
    public string ClientId { get; } = approval.ClientId;
    public string Reason { get; } = approval.Reason;
    public string Created { get; } = approval.CreatedAt.ToLocalTime().ToString("g");
    public override string ToString() => $"{ClientId}: {Reason}";
}

public sealed class SyncClientCardViewModel(ClassroomClientSnapshot client)
{
    public string ClientId { get; } = client.ClientId;
    public string Name { get; } = $"ПК {client.PcNumber} · {client.Hostname}";
    public override string ToString() => Name;
}

public sealed class SyncedFileCardViewModel(SyncedFileInfo file)
{
    public string Path { get; } = file.Path;
    public string Details { get; } = $"{file.Size:N0} байт · {file.ModifiedAt.ToLocalTime():g}";
    public override string ToString() => Path;
}

public sealed class FileVersionCardViewModel(FileVersionInfo version)
{
    public string Id { get; } = version.Id;
    public string Label { get; } = version.Label;
    public string Details { get; } = $"{version.CreatedAt.ToLocalTime():g} · {version.Size:N0} байт";
    public override string ToString() => $"{Label} · {Details}";
}

public sealed class ScreenPreviewCardViewModel : IDisposable
{
    public ScreenPreviewCardViewModel(ClassroomClientSnapshot client, Bitmap? preview)
    {
        ClientId = client.ClientId;
        Title = $"ПК {client.PcNumber} · {client.Hostname}";
        Status = client.IsOnline ? "● онлайн" : $"не в сети · {client.LastSeenAt.ToLocalTime():t}";
        WatchFolder = client.WatchFolder;
        StudentId = client.StudentId;
        Preview = preview;
        HasPreview = preview is not null;
    }
    public string ClientId { get; }
    public string Title { get; }
    public string Status { get; }
    public string WatchFolder { get; }
    public Guid? StudentId { get; }
    public Bitmap? Preview { get; }
    public bool HasPreview { get; }
    public void Dispose() => Preview?.Dispose();
}

public sealed class AuditEventCardViewModel(AuditEvent entry)
{
    public string Time { get; } = entry.CreatedAt.ToLocalTime().ToString("g");
    public string Category { get; } = entry.Category;
    public string Action { get; } = entry.Action;
    public string Actor { get; } = string.IsNullOrWhiteSpace(entry.Actor) ? "система" : entry.Actor;
    public string Target { get; } = entry.Target;
    public string Result { get; } = $"HTTP {entry.StatusCode} · {entry.DurationMs} мс";
    public string Details { get; } = entry.Details;
}
