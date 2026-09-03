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
    public ObservableCollection<ProgramModuleCardViewModel> SelectedGroupModules { get; } = [];
    public ObservableCollection<string> ClassLessonModules { get; } = [];
    public ObservableCollection<StarterAssetCardViewModel> StarterPackItems { get; } = [];
    public ObservableCollection<RolloutCardViewModel> RolloutItems { get; } = [];
    public ObservableCollection<StudentCardViewModel> Students { get; } = [];
    public ObservableCollection<StudentCardViewModel> FilteredStudents { get; } = [];
    public ObservableCollection<StudentCardViewModel> ClassRosterStudents { get; } = [];
    public ObservableCollection<RosterFilterOption> RosterGroupFilters { get; } = [];
    public ObservableCollection<ClassNoticeViewModel> ClassNotices { get; } = [];
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
    [ObservableProperty] private string currentModuleSchedule = "Выберите группу, чтобы увидеть модуль по дате";
    [ObservableProperty] private string groupsWorkspaceMode = "none";
    [ObservableProperty] private bool showCreateGroupForm;
    [ObservableProperty] private bool isGroupsChoiceOpen;
    [ObservableProperty] private GroupCardViewModel? selectedGroup;
    [ObservableProperty] private GroupCardViewModel? activeClassGroup;
    [ObservableProperty] private string? selectedClassLessonModule;
    [ObservableProperty] private RosterFilterOption? selectedRosterGroupFilter;
    [ObservableProperty] private string selectedRosterPresenceFilter = "Все";
    [ObservableProperty] private StudentCardViewModel? selectedClassStudent;
    [ObservableProperty] private bool isClassPanelOpen;
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
    [ObservableProperty] private string locationName = "";
    [ObservableProperty] private string hubUrl = ClassroomHubClient.DefaultBaseUrl;
    [ObservableProperty] private string hubStatus = "Сервер хранит учеников и группы локации.";
    [ObservableProperty] private string locationUploadPassword = "";
    [ObservableProperty] private bool needsLocationSetup;
    [ObservableProperty] private int setupStep;
    [ObservableProperty] private string? setupLocationName;
    [ObservableProperty] private string setupStudentSavesFolder = DefaultStudentSavesFolder();
    [ObservableProperty] private string settingsStatus = "Настройки действуют только на этом Tutor.";
    [ObservableProperty] private string vpnConfigsFolder = string.Empty;
    [ObservableProperty] private string studentSavesFolder = DefaultStudentSavesFolder();
    [ObservableProperty] private string vpnLocationName = VpnRegionCatalog.Primary.Name;
    [ObservableProperty] private string? setupVpnLocationName = VpnRegionCatalog.Primary.Name;
    [ObservableProperty] private string vpnDistributionStatus = "Выберите сервер VPN — конфиги можно скачать с хаба или указать папку.";
    [ObservableProperty] private StarterAssetCardViewModel? selectedStarterAsset;
    [ObservableProperty] private string wallpaperName = "Обои ещё не выбраны";
    [ObservableProperty] private string softwareStatus = "Соберите пакет: папки урока и установщики .exe / .msi.";
    [ObservableProperty] private string programStatus = "Программа подтянется при первом запуске или по кнопке ниже.";
    [ObservableProperty] private bool showOtherLocationStudents;
    [ObservableProperty] private string rolloutHeadline = "Статус раздачи появится после отправки пакета или обоев.";
    [ObservableProperty] private int selectedSectionIndex;

    public ClassroomLiveState LiveState { get; set; } = new();
    public Func<Task<string?>>? VpnConfigsFolderPicker { get; set; }
    public Func<Task<string?>>? StudentSavesFolderPicker { get; set; }
    public Func<Task<string?>>? QuizExportPathPicker { get; set; }
    public Func<Task<string?>>? QuizImportPathPicker { get; set; }
    public Func<Task<string?>>? QuizMediaPathPicker { get; set; }
    public Func<Task<IReadOnlyList<string>>>? StarterFilesPicker { get; set; }
    public Func<Task<string?>>? StarterFolderPicker { get; set; }
    public Func<Task<string?>>? WallpaperFilePicker { get; set; }

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

    public IReadOnlyList<string> RosterPresenceFilters { get; } = ["Все", "Онлайн", "Оффлайн"];
    public bool HasClassRosterStudents => ClassRosterStudents.Count > 0;
    public bool HasNoClassRosterStudents => !HasClassRosterStudents;
    public bool HasClassNotices => ClassNotices.Count > 0;
    public bool HasSelectedClassStudent => SelectedClassStudents.Count > 0;
    public string SelectedStudentsActionsTitle => SelectedClassStudents.Count <= 1 ? "Этому ученику" : $"Выбранным ({SelectedClassStudents.Count})";
    public string ClassPanelTitle => SelectedClassStudents.Count switch
    {
        0 => "Команды класса",
        1 => SelectedClassStudents[0].Name,
        _ => $"{SelectedClassStudents.Count} учеников"
    };
    public string ClassPanelSubtitle => SelectedClassStudents.Count switch
    {
        0 => "Выберите ученика. Несколько — Ctrl (на Mac ⌘) и клик.",
        1 => SelectedClassStudents[0].QuickActionsTitle,
        _ => "Команды уйдут всем выделенным."
    };
    public bool HasLessons => Lessons.Count > 0;
    public bool HasNoLessons => !HasLessons;
    public bool HasStudents => Students.Count > 0;
    public bool HasNoStudents => !HasStudents;
    public bool HasFilteredStudents => FilteredStudents.Count > 0;
    public bool HasNoFilteredStudents => !HasFilteredStudents;
    public bool HasGroups => Groups.Count > 0;
    public bool HasNoGroups => !HasGroups;
    public bool HasSelectedGroup => SelectedGroup is not null;
    public bool ShowGroupsWorkspace => IsGroupsChoiceOpen && SelectedGroup is not null;
    public bool IsGroupWorkspaceOpen => GroupsWorkspaceMode is "modules" or "students";
    public bool ShowGroupModulesWorkspace => GroupsWorkspaceMode == "modules";
    public bool ShowGroupStudentsWorkspace => GroupsWorkspaceMode == "students";
    public double GroupsWorkspaceWidth => IsGroupWorkspaceOpen ? 920 : 340;
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
    public string FocusModeLabel => IsFocusModeOn ? "Только нужные окна. Нажмите, чтобы выключить." : "Оставить только нужные окна.";
    public string WatchdogLabel => IsWatchdogOn ? "Приложение нельзя закрыть. Нажмите, чтобы выключить." : "Не давать закрыть приложение ученика.";
    public string VpnToggleLabel => IsVpnOn ? "Безопасная сеть включена. Нажмите, чтобы отключить." : "Включить безопасную сеть на всех ПК.";
    public string ThemeToggleLabel => IsDarkTheme ? "Светлая тема" : "Тёмная тема";
    public string StudentFormTitle => IsEditingStudent ? "Редактировать ученика" : "Новый ученик";
    public string StudentFormActionLabel => IsEditingStudent ? "Сохранить изменения" : "Добавить ученика";
    public bool HasAuditEvents => AuditEvents.Count > 0;
    public bool HasNoAuditEvents => !HasAuditEvents;
    public bool HasStarterPackItems => StarterPackItems.Count > 0;
    public bool HasNoStarterPackItems => !HasStarterPackItems;
    public bool HasRolloutItems => RolloutItems.Count > 0;
    public bool HasNoRolloutItems => !HasRolloutItems;
    public string ServerAddress => "http://0.0.0.0:8765";
    public string DiscoveryAddress => "UDP 8766 · локальная сеть";
    public string VersionLabel => $"Tutor {BuildInfo.Version}";
    public IReadOnlyList<string> AuditCategories { get; } = ["Все", "Синхронизация", "Магазин", "Печать", "Викторина", "Команды", "Ученики", "Система"];
    public IReadOnlyList<string> Locations { get; } = ProgramCatalog.LocationNames();
    public IReadOnlyList<string> VpnLocationNames { get; } = VpnRegionCatalog.Names();
    public bool IsSetupWelcome => NeedsLocationSetup && SetupStep == 0;
    public bool IsSetupLocation => NeedsLocationSetup && SetupStep == 1;
    public bool IsSetupDownload => NeedsLocationSetup && SetupStep == 2;

    public async Task InitializeAsync()
    {
        try
        {
            LoadSettings();
            loadingSettings = true;
            if (!NeedsLocationSetup
                && Locations.Count > 0
                && Locations.All(x => !string.Equals(x, LocationName, StringComparison.OrdinalIgnoreCase)))
                LocationName = Locations[0];
            loadingSettings = false;
            EnsureQuizSeed();
            fileSync.SetRosterRoot(StudentSavesFolder);
            if (!NeedsLocationSetup && !string.IsNullOrWhiteSpace(LocationName))
                await ApplyLocationProgramAsync();
            await RefreshCoreAsync();
            RefreshSoftwarePack();
            RefreshProgramStatus();
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
        var vpnCount = online.Count(client => client.Extra?.VpnConnected == true);
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
        RebuildClassRoster();
        RebuildClassNotices();
        RefreshRolloutStatus();
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
            var cached = VpnRegionCatalog.CacheFolder(VpnRegionCatalog.Resolve(VpnLocationName).Id);
            VpnDistributionStatus = string.IsNullOrWhiteSpace(VpnConfigsFolder)
                ? "Скачайте конфиги с сервера или укажите папку с .conf."
                : $"Папка не найдена: {VpnConfigsFolder}";
            if (Directory.Exists(cached) && Directory.GetFiles(cached, "*.conf").Length > 0)
                VpnDistributionStatus = $"Локальный кэш «{VpnLocationName}»: {Directory.GetFiles(cached, "*.conf").Length} конфигов. Нажмите «Скачать VPN», если нужно обновить.";
            return;
        }

        int configCount;
        try
        {
            configCount = Directory.GetFiles(VpnConfigsFolder, "*.conf", SearchOption.TopDirectoryOnly).Length;
            var assignments = VpnConfigDistributor.Assign(onlineClients, VpnConfigsFolder, FallbackVpnFolder());
            VpnDistributionStatus = $"{VpnLocationName}: {VpnConfigDistributor.DescribeAssignments(assignments, onlineClients.Count, configCount)}";
        }
        catch (Exception error)
        {
            VpnDistributionStatus = $"Не удалось прочитать папку VPN: {error.Message}";
        }
    }

    private string? FallbackVpnFolder()
    {
        var fallback = VpnRegionCatalog.CacheFolder(VpnRegionCatalog.Other(VpnLocationName).Id);
        return Directory.Exists(fallback) ? fallback : null;
    }

    private string EffectiveVpnFolder()
    {
        if (!string.IsNullOrWhiteSpace(VpnConfigsFolder) && Directory.Exists(VpnConfigsFolder))
            return VpnConfigsFolder;
        return VpnRegionCatalog.CacheFolder(VpnRegionCatalog.Resolve(VpnLocationName).Id);
    }

    partial void OnVpnLocationNameChanged(string value)
    {
        if (loadingSettings) return;
        var cached = VpnRegionCatalog.CacheFolder(VpnRegionCatalog.Resolve(value).Id);
        if (Directory.Exists(cached) && Directory.GetFiles(cached, "*.conf").Length > 0)
            VpnConfigsFolder = cached;
        RefreshVpnDistributionStatus(clients.GetAll().Where(client => client.IsOnline).ToList());
        if (!NeedsLocationSetup)
            SaveSettings();
    }

    partial void OnVpnConfigsFolderChanged(string value) => RefreshVpnDistributionStatus(clients.GetAll().Where(client => client.IsOnline).ToList());

    public Task RefreshScreensAsync()
    {
        try
        {
            var previous = ScreenPreviews.ToList();
            ScreenPreviews.Clear();
            foreach (var card in previous)
            {
                try { card.Dispose(); } catch { }
            }

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
        }
        catch (Exception error)
        {
            CrashLog.Write("RefreshScreens", error);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task RefreshScreenGridAsync() => RefreshScreensAsync();

    public string SectionTitle => SelectedSectionIndex switch
    {
        0 => "Класс сейчас", 1 => "Уроки печати", 2 => "Ученики и группы",
        3 => "Награды и магазин", 4 => "Сохранения", 5 => "Экраны",
        6 => "Пульт класса", 7 => "Урок печати", 8 => "Итоги урока",
        9 => "Викторины", 10 => "Статистика", 11 => "Настройки",
        12 => "Софт класса", 13 => "Восстановление файлов", 14 => "Цели",
        15 => "Настройки", 16 => "Достижения", 17 => "Журнал", _ => "Статистика"
    };
    public string SectionSubtitle => SelectedSectionIndex switch
    {
        0 => ConnectedClientLabel, 1 => "Текст урока и запуск для группы",
        2 => "Сначала группа, затем модули или ученики", 3 => "Достижения, кибероны и товары",
        4 => "Работы учеников и восстановление", 5 => "Экраны компьютеров класса",
        6 => "Блокировка, окна и сохранения", 7 => "Урок печати для класса",
        8 => "Итоги появятся в уведомлениях", 9 => "Вопросы и запуск для класса",
        10 => "Прогресс группы", 11 => "Локация, программа и этот компьютер",
        12 => "Пакет программ и обои на все компьютеры", 13 => "Вернуть файл к сохранённой копии",
        14 => "Накопления учеников", 15 => "Параметры этого класса",
        16 => "Награды за успехи", 17 => "Что происходило в классе", _ => "Прогресс учеников и групп"
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
    private async Task PickStudentSavesFolderAsync()
    {
        if (StudentSavesFolderPicker is null)
        {
            HasError = true;
            StatusMessage = "Выбор папки недоступен в этом окне.";
            return;
        }

        var selected = await StudentSavesFolderPicker();
        if (string.IsNullOrWhiteSpace(selected))
            return;

        ApplyStudentSavesFolder(selected);
        if (!NeedsLocationSetup)
            SaveSettings();
        HasError = false;
        StatusMessage = $"Сохранения учеников: {StudentSavesFolder}";
        HubStatus = StatusMessage;
    }

    private void ApplyStudentSavesFolder(string path)
    {
        StudentSavesFolder = path.Trim();
        SetupStudentSavesFolder = StudentSavesFolder;
        fileSync.SetRosterRoot(StudentSavesFolder);
    }

    private static string DefaultStudentSavesFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "groups");

    private void RefreshSoftwarePack()
    {
        var selected = SelectedStarterAsset?.Name;
        StarterPackItems.Clear();
        foreach (var item in assets.ListStarterPack())
            StarterPackItems.Add(new StarterAssetCardViewModel(item));
        SelectedStarterAsset = StarterPackItems.FirstOrDefault(x => x.Name == selected) ?? StarterPackItems.FirstOrDefault();
        WallpaperName = assets.GetWallpaper()?.Name is { Length: > 0 } name
            ? $"Выбраны обои: {name}"
            : "Обои ещё не выбраны";
        OnPropertyChanged(nameof(HasStarterPackItems));
        OnPropertyChanged(nameof(HasNoStarterPackItems));
    }

    private static string ProgramMarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "program-catalog.hash");

    private void RefreshProgramStatus()
    {
        var path = ProgramMarkerPath;
        if (!File.Exists(path))
        {
            ProgramStatus = "Каталог ещё не записан в базу — нажмите «Обновить программу».";
            return;
        }

        var lines = File.ReadAllLines(path);
        var count = lines.Length > 1 ? lines[1].Trim() : "?";
        ProgramStatus = $"Программа в базе: {count} групп. Повторно не импортируется, пока каталог в приложении не изменится.";
    }

    [RelayCommand]
    private async Task ImportProgramAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LocationName))
                throw new InvalidOperationException("Сначала выберите локацию.");
            await ApplyLocationProgramAsync();
            var groups = await classroom.ListGroupsAsync(LocationName);
            RefreshProgramStatus();
            await RefreshCoreAsync();
            ProgramStatus = $"Программа локации «{LocationName}»: {groups.Count} групп.";
            HasError = false;
            StatusMessage = ProgramStatus;
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
            ProgramStatus = error.Message;
        }
    }

    private ClassroomHubClient CreateHubClient() => new(string.IsNullOrWhiteSpace(HubUrl) ? ClassroomHubClient.DefaultBaseUrl : HubUrl);

    private async Task ApplyLocationProgramAsync()
    {
        if (string.IsNullOrWhiteSpace(LocationName)) return;
        await classroom.ImportShbProgramAsync(LocationName);
        await classroom.KeepOnlyLocationAsync(LocationName);
    }

    private void NotifySetupSteps()
    {
        OnPropertyChanged(nameof(IsSetupWelcome));
        OnPropertyChanged(nameof(IsSetupLocation));
        OnPropertyChanged(nameof(IsSetupDownload));
        ConfirmSetupLocationCommand.NotifyCanExecuteChanged();
    }

    partial void OnNeedsLocationSetupChanged(bool value)
    {
        if (value) SetupStep = 0;
        NotifySetupSteps();
    }

    partial void OnSetupStepChanged(int value) => NotifySetupSteps();

    partial void OnSetupLocationNameChanged(string? value) => ConfirmSetupLocationCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) => ConfirmSetupLocationCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void BeginLocationSetup()
    {
        if (string.IsNullOrWhiteSpace(SetupStudentSavesFolder))
            SetupStudentSavesFolder = DefaultStudentSavesFolder();
        if (string.IsNullOrWhiteSpace(SetupVpnLocationName))
            SetupVpnLocationName = VpnLocationName;
        SetupStep = 1;
    }

    private bool CanConfirmSetupLocation() =>
        !IsBusy && !string.IsNullOrWhiteSpace(SetupLocationName);

    [RelayCommand(CanExecute = nameof(CanConfirmSetupLocation))]
    private async Task ConfirmSetupLocationAsync()
    {
        loadingSettings = true;
        LocationName = SetupLocationName!.Trim();
        if (!string.IsNullOrWhiteSpace(SetupVpnLocationName))
            VpnLocationName = SetupVpnLocationName.Trim();
        if (!string.IsNullOrWhiteSpace(SetupStudentSavesFolder))
            ApplyStudentSavesFolder(SetupStudentSavesFolder);
        loadingSettings = false;
        SetupStep = 2;
        HubStatus = $"Загружаем группы и учеников локации «{LocationName}»…";
        await DownloadLocationRosterAsync();
        if (NeedsLocationSetup)
            HubStatus = string.IsNullOrWhiteSpace(HubStatus)
                ? "Не получилось скачать. Проверьте сеть и попробуйте ещё раз."
                : HubStatus;
    }

    [RelayCommand]
    private void BackToLocationSetup()
    {
        if (IsBusy) return;
        SetupStep = 1;
        HubStatus = "Выберите локацию — данные скачаются с сервера.";
    }

    [RelayCommand]
    private async Task DownloadLocationRosterAsync()
    {
        if (string.IsNullOrWhiteSpace(LocationName))
        {
            HubStatus = "Сначала выберите локацию.";
            return;
        }

        try
        {
            IsBusy = true;
            await ApplyLocationProgramAsync();
            var snapshot = await CreateHubClient().DownloadAsync(LocationName);
            if (snapshot is null)
                throw new InvalidOperationException("Сервер не вернул данные локации.");
            if (snapshot.Groups.Count == 0 && snapshot.Students.Count == 0)
            {
                HubStatus = $"Локация «{LocationName}»: группы из программы 2026–2027. На сервере учеников пока нет.";
            }
            else
            {
                await classroom.ReplaceLocationRosterAsync(snapshot);
                HubStatus = $"Скачано: {snapshot.Groups.Count} групп, {snapshot.Students.Count} учеников.";
            }
            await RefreshCoreAsync();
            NeedsLocationSetup = false;
            SetupStep = 0;
            SaveSettings();
            HasError = false;
            StatusMessage = HubStatus;
        }
        catch (Exception error)
        {
            HasError = true;
            HubStatus = $"Не удалось скачать: {error.Message}";
            StatusMessage = HubStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UploadLocationRosterAsync()
    {
        if (string.IsNullOrWhiteSpace(LocationName))
        {
            HubStatus = "Сначала выберите локацию.";
            return;
        }
        if (string.IsNullOrWhiteSpace(LocationUploadPassword))
        {
            HubStatus = "Чтобы отправить данные, введите пароль этой локации.";
            return;
        }

        try
        {
            IsBusy = true;
            var snapshot = await classroom.ExportLocationRosterAsync(LocationName);
            await CreateHubClient().UploadAsync(LocationName, LocationUploadPassword, snapshot);
            LocationUploadPassword = string.Empty;
            HubStatus = $"Отправлено на сервер: {snapshot.Groups.Count} групп, {snapshot.Students.Count} учеников.";
            HasError = false;
            StatusMessage = HubStatus;
        }
        catch (UnauthorizedAccessException)
        {
            HasError = true;
            HubStatus = "Неверный пароль локации. Данные на сервер не отправлены.";
            StatusMessage = HubStatus;
        }
        catch (Exception error)
        {
            HasError = true;
            HubStatus = $"Не удалось отправить: {error.Message}";
            StatusMessage = HubStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadVpnPeersAsync()
    {
        if (string.IsNullOrWhiteSpace(LocationName))
        {
            HubStatus = "Сначала выберите локацию класса.";
            return;
        }
        if (string.IsNullOrWhiteSpace(LocationUploadPassword))
        {
            HubStatus = "Чтобы скачать VPN-конфиги, введите пароль локации.";
            return;
        }

        try
        {
            IsBusy = true;
            var hub = CreateHubClient();
            var downloaded = 0;
            foreach (var region in VpnRegionCatalog.All)
            {
                var pack = await hub.DownloadVpnPeersAsync(region.Id, LocationName, LocationUploadPassword);
                var folder = VpnRegionCatalog.CacheFolder(region.Id);
                Directory.CreateDirectory(folder);
                foreach (var leftover in Directory.GetFiles(folder, "*.conf"))
                    File.Delete(leftover);
                foreach (var file in pack.Files)
                {
                    var name = Path.GetFileName(file.FileName);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    await File.WriteAllTextAsync(Path.Combine(folder, name), file.Content);
                    downloaded++;
                }
            }

            var selected = VpnRegionCatalog.CacheFolder(VpnRegionCatalog.Resolve(VpnLocationName).Id);
            if (Directory.Exists(selected))
                VpnConfigsFolder = selected;
            RefreshVpnDistributionStatus(clients.GetAll().Where(client => client.IsOnline).ToList());
            HubStatus = downloaded == 0
                ? "На сервере пока нет VPN-конфигов для этих серверов. Загрузите .conf на хаб."
                : $"Скачано VPN-конфигов: {downloaded}. Выбран «{VpnLocationName}».";
            HasError = false;
            StatusMessage = HubStatus;
            SaveSettings();
        }
        catch (UnauthorizedAccessException)
        {
            HasError = true;
            HubStatus = "Неверный пароль локации. VPN-конфиги не скачаны.";
            StatusMessage = HubStatus;
        }
        catch (Exception error)
        {
            HasError = true;
            HubStatus = $"Не удалось скачать VPN: {error.Message}";
            StatusMessage = HubStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadStudentUpdateFromHubAsync()
    {
        try
        {
            IsBusy = true;
            var hub = CreateHubClient();
            var manifest = await hub.GetStudentUpdateAsync();
            if (manifest is null)
            {
                HubStatus = "На сервере нет обновления Student.";
                StatusMessage = HubStatus;
                return;
            }

            var bytes = await hub.DownloadStudentUpdateFileAsync();
            var stored = assets.ImportStudentRelease(manifest, bytes);
            HubStatus = $"Обновление Student {stored.Version} сохранено для раздачи по классу.";
            HasError = false;
            StatusMessage = HubStatus;
        }
        catch (Exception error)
        {
            HasError = true;
            HubStatus = $"Не удалось скачать обновление Student: {error.Message}";
            StatusMessage = HubStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SkipLocationServer()
    {
        if (string.IsNullOrWhiteSpace(LocationName))
        {
            if (!string.IsNullOrWhiteSpace(SetupLocationName))
                LocationName = SetupLocationName.Trim();
            else if (Locations.Count > 0)
                LocationName = Locations[0];
        }
        NeedsLocationSetup = false;
        SetupStep = 0;
        HubStatus = "Работаем без сервера. Скачать состав локации можно в настройках.";
        SaveSettings();
    }

    private void TrackRollout(string title, ClassroomCommand? command)
    {
        lastRolloutCommandId = command?.Id;
        RolloutHeadline = command is null
            ? $"{title}: не отправлено. Подключите учеников и повторите."
            : $"{title}: ждём ответы компьютеров…";
        RefreshRolloutStatus();
    }

    private void RefreshRolloutStatus()
    {
        RolloutItems.Clear();
        if (lastRolloutCommandId is not Guid commandId)
        {
            OnPropertyChanged(nameof(HasRolloutItems));
            OnPropertyChanged(nameof(HasNoRolloutItems));
            return;
        }

        var snapshots = clients.GetAll().ToDictionary(x => x.ClientId, StringComparer.OrdinalIgnoreCase);
        foreach (var row in commandQueue.GetRollout(commandId))
        {
            snapshots.TryGetValue(row.ClientId, out var client);
            var name = string.IsNullOrWhiteSpace(client?.PcNumber) ? row.ClientId : client!.PcNumber;
            var status = row.State switch
            {
                "ok" => "готово",
                "error" => "ошибка",
                _ => client?.IsOnline == true ? "скачивает…" : "ждёт ПК"
            };
            RolloutItems.Add(new RolloutCardViewModel(name, status, row.Detail, row.State == "error", row.State == "ok"));
        }

        var ready = RolloutItems.Count(x => x.IsOk);
        var failed = RolloutItems.Count(x => x.IsError);
        if (RolloutItems.Count > 0)
            RolloutHeadline = $"Готово {ready} из {RolloutItems.Count}" + (failed > 0 ? $" · ошибок: {failed}" : "");
        OnPropertyChanged(nameof(HasRolloutItems));
        OnPropertyChanged(nameof(HasNoRolloutItems));
    }

    [RelayCommand]
    private async Task AddStarterFilesAsync()
    {
        if (StarterFilesPicker is null)
        {
            ShowSelectionError("Выбор файлов недоступен в этом окне.");
            return;
        }

        var files = await StarterFilesPicker();
        if (files.Count == 0) return;
        try
        {
            foreach (var file in files)
                assets.AddStarterFile(file);
            RefreshSoftwarePack();
            SoftwareStatus = files.Count == 1 ? "Файл добавлен в стартовый пакет." : $"В пакет добавлено файлов: {files.Count}.";
            HasError = false;
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
            SoftwareStatus = error.Message;
        }
    }

    [RelayCommand]
    private async Task AddStarterFolderAsync()
    {
        if (StarterFolderPicker is null)
        {
            ShowSelectionError("Выбор папки недоступен в этом окне.");
            return;
        }

        var folder = await StarterFolderPicker();
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            assets.AddStarterFolder(folder);
            RefreshSoftwarePack();
            SoftwareStatus = $"Папка «{Path.GetFileName(folder)}» добавлена в пакет.";
            HasError = false;
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
            SoftwareStatus = error.Message;
        }
    }

    [RelayCommand]
    private void RemoveStarterAsset()
    {
        if (SelectedStarterAsset is null)
        {
            ShowSelectionError("Выберите файл или папку в пакете.");
            return;
        }

        try
        {
            assets.RemoveStarterAsset(SelectedStarterAsset.Name);
            RefreshSoftwarePack();
            SoftwareStatus = "Элемент убран из пакета.";
            HasError = false;
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
            SoftwareStatus = error.Message;
        }
    }

    [RelayCommand]
    private void OpenStarterPackFolder()
    {
        try
        {
            Directory.CreateDirectory(assets.StarterPackFolder);
            OpenFolderInOs(assets.StarterPackFolder);
            SoftwareStatus = "Открыта папка стартового пакета на этом компьютере.";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    [RelayCommand]
    private async Task PickWallpaperAsync()
    {
        if (WallpaperFilePicker is null)
        {
            ShowSelectionError("Выбор картинки недоступен в этом окне.");
            return;
        }

        var file = await WallpaperFilePicker();
        if (string.IsNullOrWhiteSpace(file)) return;
        try
        {
            assets.SetWallpaper(file);
            RefreshSoftwarePack();
            SoftwareStatus = "Обои сохранены. Нажмите «Поставить обои на все ПК».";
            HasError = false;
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
            SoftwareStatus = error.Message;
        }
    }

    [RelayCommand]
    private void PushStarterPack()
    {
        if (StarterPackItems.Count == 0)
        {
            ShowSelectionError("Сначала добавьте в пакет файлы или папки.");
            return;
        }

        var command = SendClassCommand(ClassroomCommandKinds.InstallStarterPack, new { run_installers = true }, 7200);
        SoftwareStatus = "Пакет уходит на компьютеры учеников. Установщики .exe и .msi запустятся сами.";
        TrackRollout("Стартовый пакет", command);
    }

    [RelayCommand]
    private async Task PushWallpaperAsync()
    {
        if (WallpaperFilePicker is null)
        {
            ShowSelectionError("Выбор картинки недоступен в этом окне.");
            return;
        }

        var file = await WallpaperFilePicker();
        if (string.IsNullOrWhiteSpace(file))
            return;

        try
        {
            assets.SetWallpaper(file);
            RefreshSoftwarePack();
            var command = SendClassCommand(ClassroomCommandKinds.SetWallpaper, new { }, 1800);
            SoftwareStatus = "Обои выбраны и отправлены на компьютеры класса.";
            TrackRollout("Обои", command);
            HasError = false;
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
            SoftwareStatus = error.Message;
        }
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
        RebuildClassNotices();
    }

    [RelayCommand]
    private void SelectClassStudent(StudentCardViewModel? student) => SelectClassStudentCore(student, false);

    public void SelectClassStudentCore(StudentCardViewModel? student, bool toggle)
    {
        if (student is null)
        {
            IsClassPanelOpen = true;
            NotifyClassSelection();
            return;
        }

        if (!toggle)
        {
            foreach (var card in Students)
                card.IsClassSelected = card.Id == student.Id;
        }
        else
        {
            student.IsClassSelected = !student.IsClassSelected;
        }

        SelectedClassStudent = Students.FirstOrDefault(x => x.IsClassSelected && x.Id == student.Id)
            ?? Students.FirstOrDefault(x => x.IsClassSelected);
        IsClassPanelOpen = true;
        NotifyClassSelection();
        _ = LoadClassLessonModulesAsync();
    }

    private IReadOnlyList<StudentCardViewModel> SelectedClassStudents =>
        Students.Where(x => x.IsClassSelected).ToList();

    private void NotifyClassSelection()
    {
        OnPropertyChanged(nameof(HasSelectedClassStudent));
        OnPropertyChanged(nameof(ClassPanelTitle));
        OnPropertyChanged(nameof(ClassPanelSubtitle));
        OnPropertyChanged(nameof(SelectedStudentsActionsTitle));
    }

    [RelayCommand]
    private void CloseClassPanel()
    {
        IsClassPanelOpen = false;
    }

    [RelayCommand]
    private void DismissClassNotice(ClassNoticeViewModel? notice)
    {
        if (notice is null) return;
        if (notice.Kind == "error")
        {
            HasError = false;
        }
        else if (notice.Kind == "lesson")
        {
            HasLessonResultsNotification = false;
            LessonResultsSummary = string.Empty;
        }
        else if (notice.Kind == "sync")
        {
            dismissedSyncNotice = true;
        }

        RebuildClassNotices();
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
        var targets = SelectedClassStudents.Count > 0
            ? SelectedClassStudents
            : student is null ? [] : [student];
        if (targets.Count == 0)
        {
            ShowSelectionError("Выберите ученика.");
            return;
        }

        foreach (var target in targets)
        {
            OpenRosterFolder(fileSync.EnsureStudentModuleFolder(
                target.Group,
                target.LastName,
                target.FirstName,
                fileSync.GetLessonModule(target.Id)
                    ?? Groups.FirstOrDefault(x => x.Id == target.GroupId)?.Module
                    ?? target.LessonModule), target.Name);
        }
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
        var targets = SelectedClassStudents.Count > 0
            ? SelectedClassStudents
            : student is null ? [] : [student];
        if (targets.Count == 0)
        {
            ShowSelectionError("Выберите ученика.");
            return;
        }

        var sent = 0;
        string? lastError = null;
        foreach (var target in targets)
        {
            var clientId = ResolveStudentClientId(target);
            if (clientId is null)
            {
                lastError = $"ПК для {target.Name} не найден. Ученик должен быть онлайн хотя бы раз.";
                continue;
            }

            try
            {
                SendClientCommand(clientId, kind, payload);
                target.ApplyCommandedMode(kind);
                sent++;
            }
            catch (Exception error)
            {
                lastError = error.Message;
            }
        }

        if (sent == 0)
        {
            HasError = true;
            StatusMessage = lastError ?? "ПК выбранных учеников не найдены.";
            return;
        }

        HasError = false;
        StatusMessage = targets.Count == 1
            ? $"{targets[0].Name}: {DescribeCommand(kind)}"
            : $"{DescribeCommand(kind)} · {sent} из {targets.Count}";
        if (lastError is not null)
            StatusMessage += $" · {lastError}";
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
            ApplyCommandedModeToClient(pc.ClientId, kind);
            HasError = false;
            StatusMessage = $"{pc.Title}: {DescribeCommand(kind)}";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    private void ApplyCommandedModeToClient(string clientId, string kind)
    {
        var student = Students.FirstOrDefault(item => item.ClientId == clientId);
        student?.ApplyCommandedMode(kind);
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
        OpenRosterFolder(fileSync.GetClientFolderPath(clientId), label ?? clientId);
    }

    private void OpenRosterFolder(string path, string label)
    {
        try
        {
            OpenFolderInOs(path);
            HasError = false;
            StatusMessage = $"Открыта папка {label}.";
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
        }
    }

    private static void OpenFolderInOs(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "open",
                ArgumentList = { path },
                UseShellExecute = false
            });
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "xdg-open",
            ArgumentList = { path },
            UseShellExecute = false
        });
    }

    private bool loadingSettings;
    private bool dismissedSyncNotice;
    private Guid? pendingActiveClassGroupId;
    private bool applyingClassLessonModule;
    private Guid? lastRolloutCommandId;
    private const string DefaultLessonModuleLabel = "Как в группе";

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
            IsDarkTheme,
            ActiveClassGroup?.Id,
            ShowOtherLocationStudents,
            HubUrl.Trim(),
            !NeedsLocationSetup,
            StudentSavesFolder.Trim(),
            VpnRegionCatalog.Resolve(VpnLocationName).Id);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone", "Tutor");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        fileSync.SetRosterRoot(StudentSavesFolder);
        PublishPreferredGroup();
        SettingsStatus = $"Настройки сохранены · {DateTime.Now:t}";
    }

    private void LoadSettings()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone", "Tutor", "settings.json");
            if (!File.Exists(path))
            {
                NeedsLocationSetup = true;
                return;
            }
            var saved = JsonSerializer.Deserialize<TutorLocalSettings>(File.ReadAllText(path));
            if (saved is null)
            {
                NeedsLocationSetup = true;
                return;
            }
            loadingSettings = true;
            LocationName = saved.LocationName;
            ScreenRefreshSeconds = saved.ScreenRefreshSeconds;
            SyncIntervalSeconds = saved.SyncIntervalSeconds;
            AutoApproveSafeFiles = saved.AutoApproveSafeFiles;
            EnableStudentUpdates = saved.EnableStudentUpdates;
            VpnConfigsFolder = saved.VpnConfigsFolder ?? string.Empty;
            VpnLocationName = VpnRegionCatalog.Resolve(saved.VpnRegionId).Name;
            if (string.IsNullOrWhiteSpace(VpnConfigsFolder))
            {
                var cached = VpnRegionCatalog.CacheFolder(VpnRegionCatalog.Resolve(VpnLocationName).Id);
                if (Directory.Exists(cached) && Directory.GetFiles(cached, "*.conf").Length > 0)
                    VpnConfigsFolder = cached;
            }
            if (!string.IsNullOrWhiteSpace(saved.StudentSavesFolder))
                StudentSavesFolder = saved.StudentSavesFolder;
            fileSync.SetRosterRoot(StudentSavesFolder);
            IsDarkTheme = saved.PreferDarkTheme;
            pendingActiveClassGroupId = saved.ActiveClassGroupId;
            ShowOtherLocationStudents = saved.ShowOtherLocationStudents;
            if (!string.IsNullOrWhiteSpace(saved.HubUrl))
                HubUrl = saved.HubUrl;
            NeedsLocationSetup = !saved.LocationSetupCompleted;
            if (NeedsLocationSetup)
            {
                SetupStep = 0;
                SetupLocationName = string.IsNullOrWhiteSpace(saved.LocationName) ? null : saved.LocationName;
                SetupVpnLocationName = VpnLocationName;
                SetupStudentSavesFolder = StudentSavesFolder;
            }
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

    private ClassroomCommand? SendClassCommand(string kind, object payload, int? ttlSeconds = null)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            var command = commandQueue.Enqueue(new EnqueueCommandRequest(["__all__"], kind, document.RootElement, ttlSeconds));
            foreach (var student in Students)
                student.ApplyCommandedMode(kind);
            HasError = false;
            StatusMessage = DescribeCommand(kind);
            return command;
        }
        catch (Exception error)
        {
            HasError = true;
            StatusMessage = error.Message;
            return null;
        }
    }

    private static string DescribeCommand(string kind) => kind switch
    {
        ClassroomCommandKinds.LockScreen => "экран заблокирован",
        ClassroomCommandKinds.UnlockScreen => "экран разблокирован",
        ClassroomCommandKinds.WatchdogOn => "нельзя закрыть приложение",
        ClassroomCommandKinds.WatchdogOff => "приложение снова можно закрыть",
        ClassroomCommandKinds.FocusOn => "только нужные окна",
        ClassroomCommandKinds.FocusOff => "все окна снова доступны",
        ClassroomCommandKinds.SyncNow => "сохранения обновляются",
        ClassroomCommandKinds.Message => "сообщение отправлено",
        ClassroomCommandKinds.SetWorkspace => "модуль на этот урок обновлён",
        ClassroomCommandKinds.VpnConnect => "безопасная сеть включена",
        ClassroomCommandKinds.VpnDisconnect => "безопасная сеть выключена",
        ClassroomCommandKinds.InstallStarterPack => "стартовый пакет отправлен на компьютеры",
        ClassroomCommandKinds.SetWallpaper => "обои отправлены на компьютеры",
        _ => "готово"
    };

    private async Task LoadClassLessonModulesAsync()
    {
        applyingClassLessonModule = true;
        try
        {
            ClassLessonModules.Clear();
            ClassLessonModules.Add(DefaultLessonModuleLabel);
            var selected = SelectedClassStudents;
            if (selected.Count == 0)
            {
                SelectedClassLessonModule = DefaultLessonModuleLabel;
                return;
            }

            foreach (var groupId in selected.Select(x => x.GroupId).Distinct())
            {
                foreach (var module in await classroom.ListProgramModulesAsync(groupId))
                {
                    if (!string.IsNullOrWhiteSpace(module.Name) && ClassLessonModules.All(x => !string.Equals(x, module.Name, StringComparison.Ordinal)))
                        ClassLessonModules.Add(module.Name);
                }
            }

            var overrideModules = selected
                .Select(x => fileSync.GetLessonModule(x.Id))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            SelectedClassLessonModule = overrideModules.Count == 1 && ClassLessonModules.Any(x => string.Equals(x, overrideModules[0], StringComparison.OrdinalIgnoreCase))
                ? overrideModules[0]
                : DefaultLessonModuleLabel;
        }
        finally
        {
            applyingClassLessonModule = false;
        }
    }

    partial void OnSelectedClassLessonModuleChanged(string? value)
    {
        if (applyingClassLessonModule || SelectedClassStudents.Count == 0 || string.IsNullOrWhiteSpace(value))
            return;
        foreach (var student in SelectedClassStudents)
            ApplyClassLessonModule(student, value);
    }

    private void ApplyClassLessonModule(StudentCardViewModel student, string selected)
    {
        var groupModule = Groups.FirstOrDefault(x => x.Id == student.GroupId)?.Module ?? string.Empty;
        var isDefault = string.Equals(selected, DefaultLessonModuleLabel, StringComparison.Ordinal);
        if (!isDefault && ClassLessonModules.All(x => !string.Equals(x, selected, StringComparison.OrdinalIgnoreCase)))
            return;
        var module = isDefault ? groupModule : selected;
        fileSync.SetLessonModule(student.Id, isDefault ? null : selected);
        student.SetLessonModule(string.IsNullOrWhiteSpace(module) ? groupModule : module);
        fileSync.EnsureStudentModuleFolder(student.Group, student.LastName, student.FirstName, student.LessonModule);

        var clientId = ResolveStudentClientId(student);
        if (clientId is not null)
        {
            try
            {
                SendClientCommand(clientId, ClassroomCommandKinds.SetWorkspace, new
                {
                    module = student.LessonModule,
                    student_name = student.Name
                });
                SendClientCommand(clientId, ClassroomCommandKinds.SyncNow, new { });
            }
            catch (Exception error)
            {
                HasError = true;
                StatusMessage = error.Message;
                return;
            }
        }

        HasError = false;
        StatusMessage = isDefault
            ? $"{student.Name}: модуль как в группе «{student.Group}»."
            : $"{student.Name}: на этом уроке модуль «{student.LessonModule}».";
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
                StatusMessage = "Нет учеников в сети, чтобы включить безопасную связь.";
                return;
            }

            if (!string.IsNullOrWhiteSpace(EffectiveVpnFolder()) && Directory.Exists(EffectiveVpnFolder()))
            {
                var region = VpnRegionCatalog.Resolve(VpnLocationName);
                var fallbackRegion = VpnRegionCatalog.Other(VpnLocationName);
                var assignments = VpnConfigDistributor.Assign(online, EffectiveVpnFolder(), FallbackVpnFolder());
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
                    byte[]? fallback = null;
                    if (!string.IsNullOrWhiteSpace(assignment.FallbackConfigFilePath) && File.Exists(assignment.FallbackConfigFilePath))
                        fallback = await File.ReadAllBytesAsync(assignment.FallbackConfigFilePath);
                    SendClientCommand(
                        assignment.ClientId,
                        ClassroomCommandKinds.VpnInstallConfig,
                        new
                        {
                            config_base64 = Convert.ToBase64String(content),
                            fallback_config_base64 = fallback is null ? null : Convert.ToBase64String(fallback),
                            source_name = assignment.ConfigFileName,
                            auto_connect = true,
                            check_host = region.CheckHost,
                            vpn_region = region.Id,
                            fallback_vpn_region = fallback is null ? null : fallbackRegion.Id,
                            fallback_check_host = fallback is null ? null : fallbackRegion.CheckHost
                        });
                    ApplyCommandedModeToClient(assignment.ClientId, ClassroomCommandKinds.VpnInstallConfig);
                }

                var configCount = Directory.GetFiles(EffectiveVpnFolder(), "*.conf", SearchOption.TopDirectoryOnly).Length;
                VpnDistributionStatus = $"{region.Name}: {VpnConfigDistributor.DescribeAssignments(assignments, online.Count, configCount)}";
                HasError = false;
                StatusMessage = $"VPN: {VpnDistributionStatus}";
                return;
            }

            SendClassCommand(ClassroomCommandKinds.VpnConnect, new { check_host = VpnRegionCatalog.Resolve(VpnLocationName).CheckHost, vpn_region = VpnRegionCatalog.Resolve(VpnLocationName).Id });
            HasError = true;
            StatusMessage = "Нет VPN-конфигов. Скачайте их с сервера или укажите папку с .conf.";
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
        IsGroupsChoiceOpen = true;
        SelectedGroup = group;
        if (GroupsWorkspaceMode is not "modules" and not "students")
            GroupsWorkspaceMode = "none";
        StatusMessage = $"Группа «{group.Name}»: {FilteredStudents.Count} учеников. Выберите, что настроить.";
    }

    [RelayCommand]
    private void OpenGroupModulesWorkspace()
    {
        if (SelectedGroup is null) return;
        GroupsWorkspaceMode = "modules";
        StatusMessage = $"Модули группы «{SelectedGroup.Name}».";
    }

    [RelayCommand]
    private void OpenGroupStudentsWorkspace()
    {
        if (SelectedGroup is null) return;
        GroupsWorkspaceMode = "students";
        StatusMessage = $"Ученики группы «{SelectedGroup.Name}».";
    }

    [RelayCommand]
    private void CloseGroupWorkspace()
    {
        GroupsWorkspaceMode = "none";
        StatusMessage = SelectedGroup is null
            ? "Выберите группу."
            : $"Группа «{SelectedGroup.Name}». Настройте модули или учеников.";
    }

    [RelayCommand]
    private void CloseGroupChoice()
    {
        GroupsWorkspaceMode = "none";
        IsGroupsChoiceOpen = false;
        SelectedGroup = null;
        MarkSelectedGroup();
        RebuildFilteredStudents();
        CurrentModuleSchedule = "Выберите группу, чтобы увидеть модуль по дате";
        SelectedGroupModules.Clear();
        StatusMessage = "Выберите группу.";
    }

    [RelayCommand]
    private void ToggleCreateGroupForm() => ShowCreateGroupForm = !ShowCreateGroupForm;

    [RelayCommand]
    private void BeginEditStudent(StudentCardViewModel? student)
    {
        if (student is null) return;
        SelectedStudent = student;
        MarkSelectedStudent();
        IsEditingStudent = true;
        GroupsWorkspaceMode = "students";
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
            var group = await classroom.CreateGroupAsync(new GroupDraft(GroupName, GroupModule, GroupTopics, LocationName));
            fileSync.EnsureGroupFolder(group.Name);
            GroupName = GroupModule = GroupTopics = string.Empty;
            ShowCreateGroupForm = false;
            StatusMessage = $"Группа «{group.Name}» создана. Папка группы готова на сервере.";
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
                fileSync.EnsureStudentModuleFolder(SelectedGroup.Name, updated.LastName, updated.FirstName, SelectedGroup.Module);
                StatusMessage = $"Карточка {updated.DisplayName} обновлена.";
            }
            else
            {
                var student = await classroom.CreateStudentAsync(draft);
                fileSync.EnsureStudentModuleFolder(SelectedGroup.Name, student.LastName, student.FirstName, SelectedGroup.Module);
                StatusMessage = $"Ученик {student.DisplayName} добавлен. Папка создана.";
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
            StatusMessage = $"Принята версия ученика для {SelectedSyncApproval.ClientId}.";
        });
    }

    [RelayCommand]
    private async Task RejectSyncAsync()
    {
        if (SelectedSyncApproval is null) { ShowSelectionError("Выберите запрос синхронизации."); return; }
        await RunActionAsync(async () =>
        {
            await fileSync.DecideAsync(SelectedSyncApproval.Id, false);
            StatusMessage = $"Восстановлена версия тьютора для {SelectedSyncApproval.ClientId}.";
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
        var location = string.IsNullOrWhiteSpace(LocationName) ? null : LocationName.Trim();
        var storedGroups = string.IsNullOrWhiteSpace(location)
            ? []
            : await classroom.ListGroupsAsync(location);
        var selectedId = SelectedGroup?.Id;
        Groups.Clear();
        foreach (var group in storedGroups)
        {
            Groups.Add(new GroupCardViewModel(group));
            try
            {
                fileSync.EnsureGroupFolder(group.Name);
                foreach (var student in group.Students)
                    fileSync.EnsureStudentModuleFolder(group.Name, student.LastName, student.FirstName, group.Module);
            }
            catch
            {
            }
        }
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == selectedId) ?? Groups.FirstOrDefault();
        MarkSelectedGroup();
        await LoadGroupProgramAsync();
        RebuildRosterGroupFilters();
        ActiveClassGroup = Groups.FirstOrDefault(x => x.Id == (pendingActiveClassGroupId ?? ActiveClassGroup?.Id))
            ?? Groups.FirstOrDefault();
        pendingActiveClassGroupId = null;
        PublishPreferredGroup();
        var selectedStudentId = SelectedStudent?.Id;
        var storedStudents = string.IsNullOrWhiteSpace(location)
            ? []
            : (await classroom.ListStudentsAsync(location: location)).ToList();
        if (ShowOtherLocationStudents)
        {
            var connectedIds = clients.GetAll().Select(x => x.StudentId).OfType<Guid>().ToHashSet();
            var missing = connectedIds.Except(storedStudents.Select(x => x.Id)).ToList();
            if (missing.Count > 0)
            {
                var extras = await classroom.ListStudentsAsync();
                storedStudents.AddRange(extras.Where(x => missing.Contains(x.Id)));
            }
        }
        Students.Clear();
        foreach (var student in storedStudents)
        {
            var card = new StudentCardViewModel(student);
            var groupModule = Groups.FirstOrDefault(x => x.Id == student.GroupId)?.Module ?? string.Empty;
            card.SetLessonModule(fileSync.GetLessonModule(student.Id) ?? groupModule);
            Students.Add(card);
        }
        SelectedStudent = Students.FirstOrDefault(x => x.Id == selectedStudentId) ?? Students.FirstOrDefault();
        ApplyPresence(clients.GetAll());
        RebuildFilteredStudents();
        RebuildClassRoster();
        RebuildClassNotices();
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
        if (value is null)
            GroupsWorkspaceMode = "none";
        MarkSelectedGroup();
        RebuildFilteredStudents();
        NotifyGroupWorkspace();
        _ = LoadGroupProgramAsync();
    }

    partial void OnIsGroupsChoiceOpenChanged(bool value) => NotifyGroupWorkspace();
    partial void OnGroupsWorkspaceModeChanged(string value) => NotifyGroupWorkspace();

    private void NotifyGroupWorkspace()
    {
        OnPropertyChanged(nameof(HasSelectedGroup));
        OnPropertyChanged(nameof(ShowGroupsWorkspace));
        OnPropertyChanged(nameof(IsGroupWorkspaceOpen));
        OnPropertyChanged(nameof(ShowGroupModulesWorkspace));
        OnPropertyChanged(nameof(ShowGroupStudentsWorkspace));
        OnPropertyChanged(nameof(GroupsWorkspaceWidth));
    }

    private async Task LoadGroupProgramAsync()
    {
        SelectedGroupModules.Clear();
        if (SelectedGroup is null)
        {
            CurrentModuleSchedule = "Выберите группу, чтобы увидеть модуль по дате";
            return;
        }

        var current = await classroom.ApplyCurrentModuleAsync(SelectedGroup.Id);
        var modules = await classroom.ListProgramModulesAsync(SelectedGroup.Id);
        foreach (var module in modules)
            SelectedGroupModules.Add(new ProgramModuleCardViewModel(module, current is not null && current.Id == module.Id));
        if (current is null)
        {
            CurrentModuleSchedule = "В программе этой группы нет модулей с датами";
            return;
        }

        SelectedGroup.SetCurrentModule(current.Name);
        CurrentModuleSchedule = $"Сейчас: {current.Name} · {current.StartDate:dd.MM.yyyy}–{current.EndDate:dd.MM.yyyy}";
        fileSync.EnsureGroupFolder(SelectedGroup.Name);
    }

    partial void OnActiveClassGroupChanged(GroupCardViewModel? value)
    {
        PublishPreferredGroup();
        if (value is not null)
        {
            SelectedRosterGroupFilter = RosterGroupFilters.FirstOrDefault(x => x.GroupId == value.Id)
                ?? SelectedRosterGroupFilter;
        }

        if (!loadingSettings)
            SaveSettings();
    }

    partial void OnSelectedRosterGroupFilterChanged(RosterFilterOption? value) => RebuildClassRoster();
    partial void OnSelectedRosterPresenceFilterChanged(string value) => RebuildClassRoster();

    private void PublishPreferredGroup()
    {
        LiveState.PreferredGroupName = ActiveClassGroup?.Name;
        LiveState.LocationName = string.IsNullOrWhiteSpace(LocationName) ? null : LocationName.Trim();
        LiveState.ShowAllLocations = ShowOtherLocationStudents;
        LiveState.SyncSeconds = Math.Clamp(SyncIntervalSeconds, 5, 3600);
    }

    partial void OnLocationNameChanged(string value)
    {
        if (loadingSettings || NeedsLocationSetup) return;
        PublishPreferredGroup();
        _ = SwitchLocationAsync();
    }

    private async Task SwitchLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(LocationName) || IsBusy) return;
        try
        {
            IsBusy = true;
            HubStatus = $"Переключаем локацию «{LocationName}»…";
            await ApplyLocationProgramAsync();
            try
            {
                var snapshot = await CreateHubClient().DownloadAsync(LocationName);
                if (snapshot is not null && (snapshot.Groups.Count > 0 || snapshot.Students.Count > 0))
                    await classroom.ReplaceLocationRosterAsync(snapshot);
            }
            catch (Exception error)
            {
                HubStatus = $"Программа локации загружена. Сервер недоступен: {error.Message}";
            }
            await RefreshCoreAsync();
            SaveSettings();
            HubStatus = $"Локация «{LocationName}»: {Groups.Count} групп, {Students.Count} учеников.";
            StatusMessage = HubStatus;
            HasError = false;
        }
        catch (Exception error)
        {
            HasError = true;
            HubStatus = $"Не удалось сменить локацию: {error.Message}";
            StatusMessage = HubStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnShowOtherLocationStudentsChanged(bool value)
    {
        if (loadingSettings) return;
        PublishPreferredGroup();
        SaveSettings();
        _ = RefreshCoreAsync();
    }

    private void RebuildRosterGroupFilters()
    {
        var selectedId = SelectedRosterGroupFilter?.GroupId;
        RosterGroupFilters.Clear();
        RosterGroupFilters.Add(new RosterFilterOption(null, "Все группы"));
        foreach (var group in Groups)
            RosterGroupFilters.Add(new RosterFilterOption(group.Id, group.Name));
        SelectedRosterGroupFilter = RosterGroupFilters.FirstOrDefault(x => x.GroupId == selectedId)
            ?? RosterGroupFilters.FirstOrDefault();
    }

    private void RebuildClassRoster()
    {
        ClassRosterStudents.Clear();
        IEnumerable<StudentCardViewModel> source = Students;
        if (SelectedRosterGroupFilter?.GroupId is Guid groupId)
            source = source.Where(x => x.GroupId == groupId);
        source = SelectedRosterPresenceFilter switch
        {
            "Онлайн" => source.Where(x => x.IsOnline),
            "Оффлайн" => source.Where(x => x.IsOffline),
            _ => source
        };
        foreach (var student in source.OrderByDescending(x => x.IsOnline).ThenBy(x => x.Name))
            ClassRosterStudents.Add(student);
        var visible = ClassRosterStudents.Select(x => x.Id).ToHashSet();
        foreach (var student in Students)
        {
            if (!visible.Contains(student.Id))
                student.IsClassSelected = false;
        }
        SelectedClassStudent = Students.FirstOrDefault(x => x.IsClassSelected);
        NotifyClassSelection();
        OnPropertyChanged(nameof(HasClassRosterStudents));
        OnPropertyChanged(nameof(HasNoClassRosterStudents));
    }

    private void RebuildClassNotices()
    {
        ClassNotices.Clear();
        if (HasError && !string.IsNullOrWhiteSpace(StatusMessage))
            ClassNotices.Add(new ClassNoticeViewModel("error", StatusMessage));
        if (HasLessonResultsNotification && !string.IsNullOrWhiteSpace(LessonResultsSummary))
            ClassNotices.Add(new ClassNoticeViewModel("lesson", LessonResultsSummary));
        if (!dismissedSyncNotice && SyncApprovals.Count > 0)
            ClassNotices.Add(new ClassNoticeViewModel("sync", SyncApprovals.Count == 1
                ? "1 файл ждёт подтверждения тьютора"
                : $"{SyncApprovals.Count} файлов ждут подтверждения"));
        OnPropertyChanged(nameof(HasClassNotices));
    }

    private void NotifyCollectionStates()
    {
        OnPropertyChanged(nameof(HasLessons));
        OnPropertyChanged(nameof(HasNoLessons));
        OnPropertyChanged(nameof(HasStudents));
        OnPropertyChanged(nameof(HasNoStudents));
        OnPropertyChanged(nameof(HasFilteredStudents));
        OnPropertyChanged(nameof(HasNoFilteredStudents));
        OnPropertyChanged(nameof(HasClassRosterStudents));
        OnPropertyChanged(nameof(HasNoClassRosterStudents));
        OnPropertyChanged(nameof(HasClassNotices));
        OnPropertyChanged(nameof(HasSelectedClassStudent));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasNoGroups));
        NotifyGroupWorkspace();
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

public sealed class RosterFilterOption(Guid? groupId, string name)
{
    public Guid? GroupId { get; } = groupId;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

public sealed class ClassNoticeViewModel(string kind, string text)
{
    public string Kind { get; } = kind;
    public string Text { get; } = text;
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
        Module = group.Module;
        Details = $"{group.Students.Count} учеников · {(string.IsNullOrWhiteSpace(group.Module) ? "модуль не задан" : group.Module)}";
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Module { get; private set; }
    public string Details { get; private set; }
    [ObservableProperty] private bool isSelected;

    public void SetCurrentModule(string module)
    {
        Module = module;
        var split = Details.LastIndexOf(" · ", StringComparison.Ordinal);
        Details = split >= 0 ? $"{Details[..split]} · {module}" : module;
        OnPropertyChanged(nameof(Module));
        OnPropertyChanged(nameof(Details));
    }
    public override string ToString() => Name;
}

public sealed class ProgramModuleCardViewModel(GroupProgramModule module, bool isCurrent)
{
    public Guid Id { get; } = module.Id;
    public string Name { get; } = module.Name;
    public bool IsCurrent { get; } = isCurrent;
    public string Dates { get; } = $"{module.StartDate:dd.MM.yyyy} – {module.EndDate:dd.MM.yyyy} · {module.LessonCount} ур.";
    public override string ToString() => Name;
}

public sealed class LessonFilterOption(Guid? id, string name)
{
    public Guid? Id { get; } = id;
    public string Name { get; } = name;
    public override string ToString() => Name;
}

public sealed class StarterAssetCardViewModel(DistributedAsset asset)
{
    public string Name { get; } = asset.Name;
    public string KindLabel { get; } = asset.Kind == "folder" ? "Папка" : "Файл";
    public string Details { get; } = BuildDetails(asset);
    public override string ToString() => $"{Name} · {BuildDetails(asset)}";

    private static string BuildDetails(DistributedAsset asset)
    {
        var size = asset.Size switch
        {
            < 1024 => $"{asset.Size} Б",
            < 1024 * 1024 => $"{asset.Size / 1024.0:0.#} КБ",
            < 1024L * 1024 * 1024 => $"{asset.Size / (1024.0 * 1024):0.#} МБ",
            _ => $"{asset.Size / (1024.0 * 1024 * 1024):0.#} ГБ"
        };
        var kind = asset.Kind == "folder" ? "Папка" : "Файл";
        return asset.RunsInstaller ? $"{kind} · {size} · установщик" : $"{kind} · {size}";
    }
}

public sealed class RolloutCardViewModel(string pc, string status, string? detail, bool isError, bool isOk)
{
    public string Pc { get; } = pc;
    public string Status { get; } = status;
    public string Detail { get; } = string.IsNullOrWhiteSpace(detail) ? status : $"{status} · {detail}";
    public bool IsError { get; } = isError;
    public bool IsOk { get; } = isOk;
    public override string ToString() => $"{Pc} · {Detail}";
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
        LessonModule = string.Empty;
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
    public string LessonModule { get; private set; }

    [ObservableProperty] private bool isOnline;
    [ObservableProperty] private bool isOffline = true;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isClassSelected;
    [ObservableProperty] private string presenceLabel = "оффлайн";
    [ObservableProperty] private string presenceColor = "#9AA7AE";
    [ObservableProperty] private string batteryLabel = "—";
    [ObservableProperty] private string detailsLine = "оффлайн · батарея —";
    [ObservableProperty] private string lastUpdateLabel = "нет связи";
    [ObservableProperty] private string? clientId;
    [ObservableProperty] private string pcLabel = "ПК не привязан";
    [ObservableProperty] private string watchFolder = string.Empty;
    [ObservableProperty] private bool isScreenLocked;
    [ObservableProperty] private bool isWatchdogOn;
    [ObservableProperty] private bool isFocusOn;
    [ObservableProperty] private bool isVpnOn;
    public bool HasLinkedPc => !string.IsNullOrWhiteSpace(ClientId);
    public string QuickActionsTitle => HasLinkedPc ? Name : $"{Name} · нет компьютера";
    public bool HasActiveModes => IsScreenLocked || IsWatchdogOn || IsFocusOn || IsVpnOn;

    private bool? commandedScreenLocked;

    public void ApplyPresence(ClassroomClientSnapshot? client)
    {
        ClientId = client?.ClientId;
        PcLabel = client is null
            ? "нет компьютера"
            : $"компьютер {client.PcNumber}";
        WatchFolder = client?.WatchFolder ?? string.Empty;
        IsOnline = client?.IsOnline == true;
        IsOffline = !IsOnline;
        PresenceLabel = IsOnline ? "онлайн" : "оффлайн";
        PresenceColor = IsOnline ? "#068F8A" : "#9AA7AE";
        BatteryLabel = client?.Extra.BatteryPercent is int pct ? $"{pct}%" : "—";
        DetailsLine = $"{PresenceLabel} · батарея {BatteryLabel}";
        LastUpdateLabel = FormatLastUpdate(client?.LastSeenAt);
        PresenceLabel = string.IsNullOrWhiteSpace(LessonModule)
            ? PresenceLabel
            : $"{PresenceLabel} · {LessonModule}";
        IsVpnOn = client?.Extra.VpnConnected == true;
        IsWatchdogOn = client?.Extra.WatchdogActive == true;
        IsFocusOn = client?.Extra.FocusModeActive == true;
        var reportedLock = client?.Extra.ScreenLocked == true;
        if (reportedLock)
        {
            commandedScreenLocked = null;
            IsScreenLocked = true;
        }
        else if (commandedScreenLocked is bool commanded)
            IsScreenLocked = commanded;
        else
            IsScreenLocked = false;
        OnPropertyChanged(nameof(HasLinkedPc));
        OnPropertyChanged(nameof(QuickActionsTitle));
        OnPropertyChanged(nameof(HasActiveModes));
    }

    public void SetLessonModule(string module)
    {
        LessonModule = module ?? string.Empty;
        OnPropertyChanged(nameof(LessonModule));
        PresenceLabel = IsOnline ? "онлайн" : "оффлайн";
        if (!string.IsNullOrWhiteSpace(LessonModule))
            PresenceLabel = $"{PresenceLabel} · {LessonModule}";
        OnPropertyChanged(nameof(PresenceLabel));
    }

    public void ApplyCommandedMode(string kind)
    {
        switch (kind)
        {
            case ClassroomCommandKinds.LockScreen:
                commandedScreenLocked = true;
                IsScreenLocked = true;
                break;
            case ClassroomCommandKinds.UnlockScreen:
                commandedScreenLocked = false;
                IsScreenLocked = false;
                break;
            case ClassroomCommandKinds.WatchdogOn:
                IsWatchdogOn = true;
                break;
            case ClassroomCommandKinds.WatchdogOff:
                IsWatchdogOn = false;
                break;
            case ClassroomCommandKinds.FocusOn:
                IsFocusOn = true;
                break;
            case ClassroomCommandKinds.FocusOff:
                IsFocusOn = false;
                break;
            case ClassroomCommandKinds.VpnConnect:
            case ClassroomCommandKinds.VpnInstallConfig:
                IsVpnOn = true;
                break;
            case ClassroomCommandKinds.VpnDisconnect:
                IsVpnOn = false;
                break;
            default:
                return;
        }
        OnPropertyChanged(nameof(HasActiveModes));
    }

    public override string ToString() => Name;

    private static string FormatLastUpdate(DateTimeOffset? lastSeenAt)
    {
        if (lastSeenAt is null) return "нет связи";
        var local = lastSeenAt.Value.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm")
            : local.ToString("dd.MM HH:mm");
    }

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
    bool PreferDarkTheme = false,
    Guid? ActiveClassGroupId = null,
    bool ShowOtherLocationStudents = false,
    string HubUrl = "",
    bool LocationSetupCompleted = false,
    string StudentSavesFolder = "",
    string VpnRegionId = "");

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
    public void Dispose()
    {
        try { Preview?.Dispose(); } catch { }
    }
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
