using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tutor.ViewModels;
public partial class MainViewModel
{
    [ObservableProperty] private bool showQuizLibrary = true;
    [ObservableProperty] private string sharedQuizStatus = "Локальные викторины доступны без интернета.";
    [ObservableProperty] private bool sharedQuizBusy;
    private Guid quizDocumentId = Guid.NewGuid();
    private int quizSharedRevision;
    private string? editingQuizPath;
    [RelayCommand] private async Task DownloadSharedQuizzesAsync()
    {
        if (SharedQuizBusy) return;
        if (!await RequestLocationAuthorizationAsync("Загрузить библиотеку викторин", requireConfirmation: false)) return;
        SharedQuizBusy = true;
        try
        {
            using var client = new LearningClient(MailServerUrl);
            var documents = await client.PostAsync<List<SharedQuiz>>("api/learning/quizzes/list", new LearningAuth(LocationName, LocationUploadPassword));
            var cache = Path.Combine(GetQuizLibraryDirectory(), "shared");
            Directory.CreateDirectory(cache);
            foreach (var item in documents)
            {
                var path = Path.Combine(cache, item.Id.ToString("N") + ".json");
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(item, QuizJsonOptions));
                File.Move(path + ".tmp", path, true);
            }
            RefreshQuizLibrary(); SharedQuizStatus = $"Загружено викторин: {documents.Count}.";
        }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"Shared library unavailable: {error.GetType().Name}"); SharedQuizStatus = "Не удалось загрузить библиотеку. Проверьте подключение и данные локации."; }
        finally { SharedQuizBusy = false; }
    }
    [RelayCommand] private async Task PublishSharedQuizAsync()
    {
        if (SharedQuizBusy) return;
        if (!await RequestLocationAuthorizationAsync("Опубликовать викторину")) return;
        SharedQuizBusy = true;
        try
        {
            var document = BuildQuizDocument(); LearningStore.Validate(document);
            using var client = new LearningClient(MailServerUrl);
            var saved = await client.PostAsync<SharedQuiz>("api/learning/quizzes/publish", new PublishQuizRequest(LocationName, LocationUploadPassword, document, quizSharedRevision));
            quizSharedRevision = saved.Revision; SaveQuizDraft(); SharedQuizStatus = "Викторина опубликована для всех локаций.";
            RememberLocationPassword(CredentialServer, LocationName, LocationUploadPassword);
        }
        catch (LearningConflictException) { SharedQuizStatus = "На сервере есть новая версия. Обновите библиотеку; локальная версия сохранена."; SaveQuizDraft(); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"Shared publish failed: {error.GetType().Name}"); SharedQuizStatus = "Не удалось опубликовать. Проверьте вопросы, подключение и данные локации."; }
        finally { SharedQuizBusy = false; }
    }
    [RelayCommand] private async Task LaunchWholeQuizAsync()
    {
        try { var run = await quizzes.StartDocumentAsync(BuildQuizDocument(), GetQuizTargets()); QuizStatus = $"Викторина запущена: {run.Document.Questions.Count} вопросов."; HasError = false; }
        catch (Exception error) { QuizStatus = error.Message; HasError = true; }
    }
    public string QuizAudience => ActiveClassGroup is null
        ? "Выберите группу на странице класса перед запуском."
        : $"Для группы «{ActiveClassGroup.Name}»";

    private IReadOnlyList<string> GetQuizTargets()
    {
        if (ActiveClassGroup is null) throw new InvalidOperationException("Выберите группу на странице класса.");
        var members = Students.Where(student => student.GroupId == ActiveClassGroup.Id).Select(student => student.Id).ToHashSet();
        var targets = clients.GetAll().Where(client => client.IsOnline && client.StudentId is Guid id && members.Contains(id))
            .Select(client => client.ClientId).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("В выбранной группе пока нет подключённых учеников.");
        return targets;
    }

    public bool ShowQuizEditor => !ShowQuizLibrary;
    public bool HasNoSavedQuizzes => SavedQuizzes.Count == 0;
    public ObservableCollection<QuizLibraryCard> SavedQuizzes { get; } = [];
    partial void OnShowQuizLibraryChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowQuizEditor));
        OnPropertyChanged(nameof(ShowQuizQuestions));
    }
    [RelayCommand] private void SaveQuizAndReturn() => OpenQuizLibrary();

    [RelayCommand] private void OpenQuizLibrary()
    {
        SaveQuizDraft();
        if (HasError) return;
        RefreshQuizLibrary(); ShowQuizSettings = false; ShowQuizLibrary = true;
    }
    [RelayCommand] private void RefreshQuizLibrary()
    {
        SavedQuizzes.Clear();
        var directory = GetQuizLibraryDirectory();
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Where(p => Path.GetFileName(p) != "draft.json"))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var document = JsonSerializer.Deserialize<QuizDocument>(json, QuizJsonOptions);
                    using var parsed = JsonDocument.Parse(json);
                    if (document is not null && !parsed.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, "id", StringComparison.OrdinalIgnoreCase)))
                    {
                        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(document, QuizJsonOptions)); File.Move(path + ".tmp", path, true);
                    }
                    if (document is not null && document.Format == "kiberone-quiz" && document.Version == 1 && document.Questions is not null)
                        SavedQuizzes.Add(new QuizLibraryCard(path, document.Title, document.Questions.Count, document.TimePerQuestionSeconds, document.XpReward));
                }
                catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"Quiz library read failed: {error.GetType().Name}"); }
            }
        var cache = Path.Combine(directory, "shared");
        if (Directory.Exists(cache)) foreach (var path in Directory.EnumerateFiles(cache, "*.json"))
        {
            try
            {
                var item = JsonSerializer.Deserialize<SharedQuiz>(File.ReadAllText(path), QuizJsonOptions);
                if (item is not null) SavedQuizzes.Add(new QuizLibraryCard(path, item.Document.Title, item.Document.Questions.Count, item.Document.TimePerQuestionSeconds, item.Document.XpReward, item.Location));
            }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"Shared quiz cache: {error.GetType().Name}"); }
        }
        OnPropertyChanged(nameof(HasNoSavedQuizzes));
    }
    [RelayCommand] private void EditSavedQuiz(QuizLibraryCard? card) => LoadSavedQuiz(card, true);

    private void LoadSavedQuiz(QuizLibraryCard? card, bool openEditor)
    {
        if (card is null) return;
        try
        {
            var document = card.SourceLocation is null
                ? JsonSerializer.Deserialize<QuizDocument>(File.ReadAllText(card.Path), QuizJsonOptions)
                : JsonSerializer.Deserialize<SharedQuiz>(File.ReadAllText(card.Path), QuizJsonOptions)?.Document;
            if (document is not null && card.SourceLocation is not null && !string.Equals(card.SourceLocation, LocationName, StringComparison.OrdinalIgnoreCase))
            { document.Id = Guid.NewGuid(); document.SharedRevision = 0; }
            document = document
                ?? throw new InvalidDataException("Викторина не найдена.");
            if (!ShowQuizLibrary) { SaveQuizDraft(); if (HasError) return; }
            editingQuizPath = card.SourceLocation is null ? card.Path : null;
            ApplyQuizDocument(document);
            if (openEditor) { ShowQuizLibrary = false; ShowQuizQuestionsPane(); }
            QuizStatus = $"{card.QuestionCount} вопросов · {card.Seconds} с на вопрос"; HasError = false;
        }
        catch (Exception error) { HasError = true; QuizStatus = error.Message; }
    }
    [RelayCommand] private async Task LaunchSavedQuizAsync(QuizLibraryCard? card)
    {
        LoadSavedQuiz(card, false);
        if (card is null || HasError) return;
        try
        {
            var run = await quizzes.StartDocumentAsync(BuildQuizDocument(), GetQuizTargets());
            ShowQuizSettings = false; ShowQuizLibrary = true;
            QuizStatus = $"Викторина запущена: {run.Document.Questions.Count} вопросов. Вопросы переключаются автоматически.";
        }
        catch (Exception error) { HasError = true; QuizStatus = error.Message; }
    }
}
public sealed record QuizLibraryCard(string Path, string Title, int QuestionCount, int Seconds, int Xp, string? SourceLocation = null)
{
    public string Summary => $"{QuestionCount} вопросов · {Seconds} с · {Xp} XP" + (SourceLocation is null ? "" : " · " + SourceLocation);
}
