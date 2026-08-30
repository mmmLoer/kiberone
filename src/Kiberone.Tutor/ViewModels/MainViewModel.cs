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
    [ObservableProperty] private string quizQuestion = string.Empty;
    [ObservableProperty] private string quizOptionsText = "Вариант 1\nВариант 2";
    [ObservableProperty] private int quizCorrectAnswer = 1;
    [ObservableProperty] private int quizXpReward = 10;
    [ObservableProperty] private string quizStatus = "Викторина ещё не запускалась.";
    [ObservableProperty] private string auditSearch = string.Empty;
    [ObservableProperty] private string? selectedAuditCategory;
    [ObservableProperty] private string groupStatisticsText = "Выберите группу и загрузите статистику.";
    [ObservableProperty] private string studentStatisticsText = "Выберите ученика и загрузите статистику.";
    [ObservableProperty] private string liveLessonName = "Практика класса";
    [ObservableProperty] private string liveLessonText = "for i in range(10): print(i)";
    [ObservableProperty] private string liveLessonState = "Урок не запущен";
    [ObservableProperty] private int screenRefreshSeconds = 30;
    [ObservableProperty] private int syncIntervalSeconds = 15;
    [ObservableProperty] private bool autoApproveSafeFiles = true;
    [ObservableProperty] private bool enableStudentUpdates = true;
    [ObservableProperty] private string locationName = "KIBERone Classroom";
    [ObservableProperty] private string settingsStatus = "Настройки действуют только на этом Tutor.";
    [ObservableProperty] private int selectedSectionIndex;

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
    public bool HasGroups => Groups.Count > 0;
    public bool HasNoGroups => !HasGroups;
    public bool HasScreenPreviews => ScreenPreviews.Count > 0;
    public bool HasNoScreenPreviews => !HasScreenPreviews;
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
        ConnectedClientCount = clients.GetAll().Count(client => client.IsOnline);
        ConnectedClientLabel = ConnectedClientCount switch
        {
            0 => "Нет учеников",
            1 => "1 ученик онлайн",
            _ => $"{ConnectedClientCount} учеников онлайн"
        };
    }

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
        9 => "Статистика ученика", 10 => "Статистика группы", 11 => "Сервер и локация",
        12 => "Модули и версии", 13 => "Сравнение и восстановление", 14 => "Цели и каталог",
        15 => "Настройки", 16 => "Достижения и награды", 17 => "Журнал аудита", _ => "Статистика"
    };
    public string SectionSubtitle => SelectedSectionIndex switch
    {
        0 => $"Текущий класс · {ConnectedClientLabel}", 1 => "Создание текста и запуск для группы",
        2 => "Группы, ученики и персональные карточки", 3 => "Достижения, кибероны и товары",
        4 => "Версии проектов и восстановление", 5 => "Наблюдение за классом · LAN",
        6 => "Фокус, сообщения и управление компьютерами", 7 => "Python · группа 01 · 10:00",
        8 => "Сессия завершена тьютором · рейтинг открыт", 9 => "Персональная история печати",
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
    [RelayCommand] private void SendMessageAll() => SendClassCommand(ClassroomCommandKinds.Message, new { text = ClassroomMessage });

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
    private void SaveSettings()
    {
        ScreenRefreshSeconds = Math.Clamp(ScreenRefreshSeconds, 5, 300);
        SyncIntervalSeconds = Math.Clamp(SyncIntervalSeconds, 5, 600);
        var settings = new TutorLocalSettings(LocationName.Trim(), ScreenRefreshSeconds, SyncIntervalSeconds, AutoApproveSafeFiles, EnableStudentUpdates);
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
            LocationName = saved.LocationName;
            ScreenRefreshSeconds = saved.ScreenRefreshSeconds;
            SyncIntervalSeconds = saved.SyncIntervalSeconds;
            AutoApproveSafeFiles = saved.AutoApproveSafeFiles;
            EnableStudentUpdates = saved.EnableStudentUpdates;
            SettingsStatus = "Локальные настройки загружены.";
        }
        catch (Exception error)
        {
            SettingsStatus = $"Настройки не загружены: {error.Message}";
        }
    }

    [RelayCommand]
    private async Task StartQuizAsync()
    {
        try
        {
            var options = QuizOptionsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var session = await quizzes.StartAsync(new StartQuizRequest(QuizQuestion, options, QuizCorrectAnswer - 1, QuizXpReward, ["__all__"]));
            QuizStatus = $"Викторина запущена · {session.Id.ToString("N")[..8]} · вариантов: {options.Length}.";
            StatusMessage = QuizStatus;
            HasError = false;
        }
        catch (LessonValidationException validation) { HasError = true; QuizStatus = string.Join(" ", validation.Errors); }
        catch (Exception error) { HasError = true; QuizStatus = error.Message; }
    }

    [RelayCommand]
    private async Task RefreshAuditAsync()
    {
        var category = SelectedAuditCategory is null or "Все" ? null : SelectedAuditCategory;
        AuditEvents.Clear();
        foreach (var entry in await audit.ListAsync(new AuditQuery(category, AuditSearch, 500))) AuditEvents.Add(new AuditEventCardViewModel(entry));
        NotifyCollectionStates();
    }

    [RelayCommand]
    private async Task LoadGroupStatisticsAsync()
    {
        if (SelectedGroup is null) { ShowSelectionError("Выберите группу."); return; }
        var stats = await classroom.GetGroupStatisticsAsync(SelectedGroup.Id);
        GroupStatisticsText = stats is null ? "Группа не найдена." :
            $"{stats.GroupName}\nУченики: {stats.StudentCount}\nСредняя оценка: {stats.AverageGrade:0.##}\nВсего XP: {stats.TotalXp:N0}\nКибероны: {stats.TotalKiberons:N0}\nПосещения: {stats.SessionCount}\nДостижения: {stats.AchievementCount}";
    }

    [RelayCommand]
    private async Task LoadStudentStatisticsAsync()
    {
        if (SelectedStudent is null) { ShowSelectionError("Выберите ученика."); return; }
        var stats = await classroom.GetStudentStatisticsAsync(SelectedStudent.Id);
        StudentStatisticsText = stats is null ? "Ученик не найден." :
            $"{stats.DisplayName}\nГруппа: {stats.GroupName}\nУровень: {stats.Level} · {stats.Xp} XP\nБаланс: {stats.Kiberons} K\nСредняя оценка: {stats.AverageGrade:0.##} ({stats.GradeCount})\nПосещения: {stats.SessionCount}\nДостижения: {stats.AchievementCount}\nПокупки: {stats.PurchaseCount}";
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
    private async Task CreateStudentAsync()
    {
        if (SelectedGroup is null)
        {
            HasError = true;
            StatusMessage = "Сначала выберите группу ученика.";
            return;
        }
        await RunActionAsync(async () =>
        {
            var student = await classroom.CreateStudentAsync(new StudentDraft(StudentLastName, StudentFirstName, StudentAge,
                SelectedGroup.Id, StudentComment, string.Empty, string.Empty));
            StudentLastName = StudentFirstName = StudentComment = string.Empty;
            StatusMessage = $"Ученик {student.DisplayName} добавлен.";
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
        var storedGroups = await classroom.ListGroupsAsync();
        var selectedId = SelectedGroup?.Id;
        Groups.Clear();
        foreach (var group in storedGroups) Groups.Add(new GroupCardViewModel(group));
        SelectedGroup = Groups.FirstOrDefault(x => x.Id == selectedId) ?? Groups.FirstOrDefault();
        var selectedStudentId = SelectedStudent?.Id;
        var storedStudents = await classroom.ListStudentsAsync();
        Students.Clear();
        foreach (var student in storedStudents) Students.Add(new StudentCardViewModel(student));
        SelectedStudent = Students.FirstOrDefault(x => x.Id == selectedStudentId) ?? Students.FirstOrDefault();
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

    private void NotifyCollectionStates()
    {
        OnPropertyChanged(nameof(HasLessons));
        OnPropertyChanged(nameof(HasNoLessons));
        OnPropertyChanged(nameof(HasStudents));
        OnPropertyChanged(nameof(HasNoStudents));
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(HasNoGroups));
        OnPropertyChanged(nameof(HasScreenPreviews));
        OnPropertyChanged(nameof(HasNoScreenPreviews));
        OnPropertyChanged(nameof(HasAuditEvents));
        OnPropertyChanged(nameof(HasNoAuditEvents));
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

public sealed class GroupCardViewModel(ClassroomGroup group)
{
    public Guid Id { get; } = group.Id;
    public string Name { get; } = group.Name;
    public string Details { get; } = $"{group.Students.Count} учеников · {(string.IsNullOrWhiteSpace(group.Module) ? "модуль не задан" : group.Module)}";
    public override string ToString() => Name;
}

public sealed class StudentCardViewModel(StudentSummary student)
{
    public Guid Id { get; } = student.Id;
    public string Name { get; } = student.DisplayName;
    public string Group { get; } = student.GroupName;
    public string Progress { get; } = $"Уровень {student.Level} · {student.Xp} XP";
    public int Xp { get; } = student.Xp;
    public string Balance { get; } = $"{student.Kiberons} K";
    public override string ToString() => Name;
}

public sealed record TutorLocalSettings(string LocationName, int ScreenRefreshSeconds, int SyncIntervalSeconds, bool AutoApproveSafeFiles, bool EnableStudentUpdates);

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
        Preview = preview;
        HasPreview = preview is not null;
    }
    public string ClientId { get; }
    public string Title { get; }
    public string Status { get; }
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
