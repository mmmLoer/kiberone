using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
using Kiberone.Infrastructure;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;

namespace Kiberone.Student.ViewModels;

public partial class MainViewModel
{
    private StudentMailAccount? mailAccount;
    private long mailSessionVersion;
    private DateTimeOffset nextMailAttempt;
    public void EnsureMailAccount()
    {
        if (IsLoginVisible || IsChangingStudent || !IsConnected || HasMailAccount || MailBusy
            || MailAccountRequested is null || DateTimeOffset.UtcNow < nextMailAttempt) return;
        nextMailAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
        _ = RefreshMailAsync();
    }
    public Func<CancellationToken, Task<StudentMailAccount>>? MailAccountRequested { get; set; }
    public Func<string, Task>? CopyTextRequested { get; set; }
    public Func<IReadOnlyList<InstalledApplication>>? InstalledAppsRequested { get; set; }
    [ObservableProperty] private string accessStatus = "Каталог приложений отправляется тьютору после подключения.";
    [ObservableProperty] private string mailAddress = "Почта пока не выдана";
    [ObservableProperty] private string mailStatus = "Подключись к тьютору и обнови входящие.";
    [ObservableProperty] private bool mailBusy;
    [ObservableProperty] private MailMessageCard? selectedMailMessage;
    [ObservableProperty] private string newMailServiceName = "";
    [ObservableProperty] private string newMailServiceUrl = "";
    public ObservableCollection<MailMessageCard> MailMessages { get; } = [];
    public ObservableCollection<MailServiceCard> MailServices { get; } = [];
    public ObservableCollection<StudentAppCard> InstalledApps { get; } = [];
    public bool IsMailSection => SelectedSectionIndex == 9;
    public bool IsAppsSection => SelectedSectionIndex == 8;
    public bool IsInboxVisible => SelectedMailMessage is null;
    public bool IsMessageVisible => SelectedMailMessage is not null;
    public bool HasMailAccount => mailAccount is not null;
    public bool IsMailEmpty => MailMessages.Count == 0;
    public string MailUnreadLabel => $"{MailMessages.Count(x => x.IsNew)} новых";
    partial void OnSelectedMailMessageChanged(MailMessageCard? value)
    { OnPropertyChanged(nameof(IsInboxVisible)); OnPropertyChanged(nameof(IsMessageVisible)); OnPropertyChanged(nameof(SectionTitle)); }
    [RelayCommand] private async Task RefreshMailAsync()
    {
        if (MailBusy) return;
        MailBusy = true;
        try
        {
            if (IsLoginVisible || SelectedStudent is null || MailAccountRequested is null) throw new InvalidOperationException("Сначала выбери своё имя и подключись к тьютору.");
            var requestedStudentId = SelectedStudent.Id;
            if (mailAccount?.StudentId != requestedStudentId) ResetMail();
            nextMailAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
            var sessionVersion = mailSessionVersion;
            try
            {
                var receivedAccount = await MailAccountRequested(CancellationToken.None);
                if (SelectedStudent?.Id != requestedStudentId || sessionVersion != mailSessionVersion || IsLoginVisible) return;
                if (receivedAccount.StudentId != requestedStudentId) throw new InvalidDataException("Получен чужой ящик.");
                var firstLoad = mailAccount is null;
                mailAccount = receivedAccount;
                OnPropertyChanged(nameof(HasMailAccount));
                MailAddress = mailAccount.Address;
                if (firstLoad) LoadMailServices();
            }
            catch (Exception error) when (mailAccount?.StudentId == requestedStudentId)
            { Trace.TraceWarning(error.ToString()); }
            using var mailbox = new StudentMailboxClient(mailAccount.ServerUrl);
            var messages = await mailbox.ReadAsync(mailAccount);
            if (SelectedStudent?.Id != requestedStudentId || sessionVersion != mailSessionVersion || IsLoginVisible) return;
            var openId = SelectedMailMessage?.Id;
            MailMessages.Clear();
            foreach (var message in messages) MailMessages.Add(new MailMessageCard(message));
            if (openId is not null) SelectedMailMessage = MailMessages.FirstOrDefault(m => m.Id == openId);
            OnPropertyChanged(nameof(MailUnreadLabel));
            OnPropertyChanged(nameof(IsMailEmpty));
            MailStatus = messages.Count == 0 ? "Писем пока нет. Нажми «Обновить», когда придёт письмо." : "Письма удаляются через 24 часа после получения.";
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Тьютору нужно"))
        { MailStatus = "Попроси тьютора настроить почту для класса."; }
        catch (Exception error) { Trace.TraceError(error.ToString()); MailStatus = "Не удалось получить письма. Проверь подключение и попробуй ещё раз."; }
        finally { MailBusy = false; }
    }
    public void RemoveExpiredMail()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var message in MailMessages.Where(m => m.ExpiresAt <= now).ToArray()) MailMessages.Remove(message);
        if (SelectedMailMessage?.ExpiresAt <= now) SelectedMailMessage = null;
        OnPropertyChanged(nameof(MailUnreadLabel)); OnPropertyChanged(nameof(IsMailEmpty));
    }
    public void ResetMail()
    {
        mailSessionVersion++;
        nextMailAttempt = DateTimeOffset.MinValue;
        mailAccount = null; MailAddress = "Почта пока не выдана"; MailMessages.Clear(); MailServices.Clear(); SelectedMailMessage = null;
        OnPropertyChanged(nameof(MailUnreadLabel));
        OnPropertyChanged(nameof(HasMailAccount)); OnPropertyChanged(nameof(IsMailEmpty));
    }
    [RelayCommand] private async Task CopyMailAddressAsync()
    { if (mailAccount is not null && CopyTextRequested is not null) { await CopyTextRequested(mailAccount.Address); MailStatus = "Адрес скопирован."; } }
    [RelayCommand] private async Task CopyMailPasswordAsync()
    { if (mailAccount is not null && CopyTextRequested is not null) { await CopyTextRequested(mailAccount.Password); MailStatus = "Пароль скопирован. Не показывай его другим."; } }
    [RelayCommand] private async Task CopyMailCodeAsync()
    { RemoveExpiredMail(); if (SelectedMailMessage?.Code is string code && CopyTextRequested is not null) { await CopyTextRequested(code); MailStatus = "Код скопирован."; } }
    [RelayCommand] private void OpenMailMessage(MailMessageCard? message) { RemoveExpiredMail(); SelectedMailMessage = message?.ExpiresAt <= DateTimeOffset.UtcNow ? null : message; }
    [RelayCommand] private void CloseMailMessage() => SelectedMailMessage = null;
    [RelayCommand] private void OpenMailLink(StudentMailLink? link)
    {
        if (link is null || !Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { MailStatus = "Не удалось открыть ссылку: " + error.Message; }
    }
    [RelayCommand] private void AddMailService()
    {
        try
        {
            if (mailAccount is null) throw new InvalidOperationException("Сначала получи почту.");
            var name = NewMailServiceName.Trim();
            if (name.Length is < 1 or > 80) throw new ArgumentException("Укажи название сервиса.");
            if (!Uri.TryCreate(NewMailServiceUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
                throw new ArgumentException("Укажи HTTPS-адрес сервиса.");
            if (MailServices.All(x => x.Url != uri.AbsoluteUri)) MailServices.Add(new MailServiceCard(new StudentMailService(name, uri.AbsoluteUri)));
            var path = MailServicesPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(MailServices.Select(x => new StudentMailService(x.Name, x.Url))));
            File.Move(path + ".tmp", path, true); NewMailServiceName = ""; NewMailServiceUrl = "";
            MailStatus = "Сервис сохранён на этом компьютере.";
        }
        catch (Exception error) { MailStatus = error.Message; }
    }
    [RelayCommand] private void OpenMailService(MailServiceCard? service)
    {
        if (service is null || !Uri.TryCreate(service.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { MailStatus = error.Message; }
    }
    private string MailServicesPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "mail-services", mailAccount!.StudentId.ToString("N") + ".json");
    private void LoadMailServices()
    {
        try
        {
            var path = MailServicesPath();
            if (File.Exists(path)) foreach (var service in JsonSerializer.Deserialize<List<StudentMailService>>(File.ReadAllText(path)) ?? []) MailServices.Add(new MailServiceCard(service));
        }
        catch { MailStatus = "Не удалось прочитать сохранённые сервисы."; }
    }
    [RelayCommand] private async Task RefreshInstalledAppsAsync()
    {
        if (InstalledAppsRequested is null) return;
        try
        {
            var apps = await Task.Run(InstalledAppsRequested);
            InstalledApps.Clear(); foreach (var app in apps) InstalledApps.Add(new StudentAppCard(app));
        }
        catch (Exception error) { AccessStatus = "Не удалось прочитать приложения: " + error.Message; }
    }
}
public sealed class MailMessageCard(StudentMailMessage message)
{
    public string Id => message.Id;
    public string Sender => message.Sender;
    public string Subject => message.Subject;
    public string Body => message.Body;
    public string? HtmlBody => message.HtmlBody;
    public bool HasHtml => !string.IsNullOrWhiteSpace(message.HtmlBody);
    public DateTimeOffset? ExpiresAt => message.ExpiresAt;
    public IReadOnlyList<StudentMailLink> Links => message.Links ?? [];
    public string Preview => message.Body.Length > 90 ? message.Body[..90] + "…" : message.Body;
    public string Date => message.ReceivedAt.ToLocalTime().ToString("g");
    public bool IsNew => message.IsNew;
    public string? Code => message.ConfirmationCode;
    public bool HasCode => Code is not null;
    public string Initial => string.IsNullOrEmpty(Sender) ? "✉" : Sender[..1].ToUpperInvariant();
}
public sealed class MailServiceCard(StudentMailService service)
{
    public string Name => service.Name;
    public string Url => service.Url;
    public string Initial => string.IsNullOrEmpty(Name) ? "↗" : Name[..1].ToUpperInvariant();
}
public sealed class StudentAppCard(InstalledApplication application)
{
    public string Name => application.Name;
    public string Executable => application.Executable;
}
