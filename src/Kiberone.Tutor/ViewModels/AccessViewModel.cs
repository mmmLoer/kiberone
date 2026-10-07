using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Avalonia.Media.Imaging;

namespace Kiberone.Tutor.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private GroupCardViewModel? accessGroup;
    [ObservableProperty] private int accessPage;
    [ObservableProperty] private bool accessEnabled;
    [ObservableProperty] private bool onlyAllowedApps;
    [ObservableProperty] private bool onlyAllowedSites;
    [ObservableProperty] private string accessSearch = "";
    [ObservableProperty] private string newSite = "";
    [ObservableProperty] private string accessStatus = "Выберите группу. Правила будут общими для всех её компьютеров.";
    [ObservableProperty] private string tutorTunnelName = "KIBERoneTutor";
    [ObservableProperty] private string tutorVpnStatus = "Подключение ещё не проверено";
    [ObservableProperty] private bool tutorVpnConnected;
    [ObservableProperty] private bool tutorVpnBusy;
    [ObservableProperty] private string mailServerUrl = Environment.GetEnvironmentVariable("KIBERONE_MAIL_API") ?? "https://nshub.pro/";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, StudentMailAccount> issuedMailAccounts = new();
    private async Task<StudentMailAccount> ProvideMailAccountAsync(Guid studentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(LocationUploadPassword)) throw new InvalidOperationException("Тьютору нужно указать пароль локации в настройках для выдачи почты.");
        using var mailbox = new StudentMailboxClient(MailServerUrl);
        var location = LocationName; var password = LocationUploadPassword; var server = CredentialServer;
        var roster = await classroom.ExportLocationRosterAsync(location, ct);
        var student = roster.Students.FirstOrDefault(x => x.Id == studentId)
            ?? throw new InvalidOperationException("Ученик не найден в локации.");
        var group = roster.Groups.FirstOrDefault(x => x.Id == student.GroupId)
            ?? throw new InvalidOperationException("Группа ученика не найдена.");
        var account = await mailbox.ProvisionAsync(new StudentMailProvisionRequest(location, password, studentId, student, group), ct);
        RememberLocationPassword(server, location, password);
        account = account with { ServerUrl = MailServerUrl };
        issuedMailAccounts[studentId] = account;
        return account;
    }
    public bool IsAccessOverview => AccessPage == 0;
    public bool IsAccessApps => AccessPage == 1;
    public bool IsAccessSites => AccessPage == 2;
    public bool IsSection19 => SelectedSectionIndex == 19;
    public bool IsSection20 => SelectedSectionIndex == 20;
    public ObservableCollection<AccessApplicationCard> AccessApplications { get; } = [];
    public ObservableCollection<AccessSiteCard> AccessSites { get; } = [];
    partial void OnAccessPageChanged(int value)
    {
        OnPropertyChanged(nameof(IsAccessOverview)); OnPropertyChanged(nameof(IsAccessApps)); OnPropertyChanged(nameof(IsAccessSites));
    }
    partial void OnAccessGroupChanged(GroupCardViewModel? value)
    {
        foreach (var app in AccessApplications) app.Icon?.Dispose();
        AccessApplications.Clear();
        var policy = ClassroomService.ParseAccessPolicy(value?.AccessPolicyJson);
        AccessEnabled = policy.Enabled; OnlyAllowedApps = policy.OnlyAllowedApps; OnlyAllowedSites = policy.OnlyAllowedSites;
        AccessSites.Clear();
        foreach (var site in policy.AllowedSites) AccessSites.Add(new AccessSiteCard(site, true));
        foreach (var site in policy.BlockedSites) AccessSites.Add(new AccessSiteCard(site, false));
        RefreshAccessApplications();
    }
    partial void OnAccessSearchChanged(string value)
    {
        foreach (var app in AccessApplications) app.IsVisible = app.Name.Contains(value, StringComparison.OrdinalIgnoreCase) || app.Executable.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
    [RelayCommand] private void OpenAccessPage(string? page)
    {
        SelectedSectionIndex = 19;
        AccessPage = int.TryParse(page, out var number) ? number : 0;
        AccessGroup ??= ActiveClassGroup ?? Groups.FirstOrDefault();
        RefreshAccessApplications();
    }
    [RelayCommand] private void RefreshAccessApplications()
    {
        var edited = AccessApplications.ToDictionary(x => x.Executable, x => x.IsAllowed, StringComparer.OrdinalIgnoreCase);
        var policy = ClassroomService.ParseAccessPolicy(AccessGroup?.AccessPolicyJson);
        var groupStudentIds = Students.Where(x => x.GroupId == AccessGroup?.Id).Select(x => x.Id).ToHashSet();
        var ids = clients.GetAll().Where(x => x.StudentId is Guid id && groupStudentIds.Contains(id)).Select(x => x.ClientId);
        var found = clients.GetApplicationInventory(ids).ToList();
        foreach (var app in policy.AllowedApps.Concat(policy.BlockedApps))
            if (found.All(x => !string.Equals(x.Application.Executable, app, StringComparison.OrdinalIgnoreCase)))
                found.Add(new ClassroomApplication(new InstalledApplication(app, app), []));
        foreach (var app in AccessApplications) app.Icon?.Dispose();
        AccessApplications.Clear();
        foreach (var app in found.OrderBy(x => x.Application.Name))
            AccessApplications.Add(new AccessApplicationCard(app, edited.TryGetValue(app.Application.Executable, out var allowed) ? allowed :
                policy.OnlyAllowedApps ? policy.AllowedApps.Contains(app.Application.Executable, StringComparer.OrdinalIgnoreCase) : !policy.BlockedApps.Contains(app.Application.Executable, StringComparer.OrdinalIgnoreCase)));
        OnAccessSearchChanged(AccessSearch);
        AccessStatus = found.Count == 0 ? "Каталог появится после подключения и выбора имени ученика. Нажмите «Обновить каталог»." : $"В каталоге {found.Count} приложений. Изменения применятся после публикации.";
    }
    [RelayCommand] private void AddAccessSite()
    {
        try
        {
            var normalized = SiteRule.Normalize(NewSite);
            if (AccessSites.All(x => x.Address != normalized)) AccessSites.Add(new AccessSiteCard(normalized, true));
            NewSite = ""; AccessStatus = "Сайт добавлен. Опубликуйте изменения для группы.";
        }
        catch (Exception error) { AccessStatus = error.Message; }
    }
    [RelayCommand] private void RemoveAccessSite(AccessSiteCard? site) { if (site is not null) AccessSites.Remove(site); }
    [RelayCommand] private async Task PublishAccessAsync()
    {
        if (AccessGroup is null) { AccessStatus = "Выберите группу."; return; }
        var group = AccessGroup;
        try
        {
            var policy = new ClassroomAccessPolicy("", AccessEnabled, OnlyAllowedApps,
                AccessApplications.Where(x => x.IsAllowed).Select(x => x.Executable).ToArray(),
                AccessApplications.Where(x => !x.IsAllowed).Select(x => x.Executable).ToArray(), OnlyAllowedSites,
                AccessSites.Where(x => x.IsAllowed).Select(x => x.Address).ToArray(), AccessSites.Where(x => !x.IsAllowed).Select(x => x.Address).ToArray());
            group.AccessPolicyJson = await classroom.SaveAccessPolicyAsync(group.Id, policy);
            AccessStatus = $"Опубликовано для группы «{group.Name}». Новые ПК получат правила при подключении. При необходимости перезапустите браузер.";
        }
        catch (Exception error) { AccessStatus = "Не удалось опубликовать: " + error.Message; }
    }
    [RelayCommand] private async Task CheckTutorVpnAsync()
    {
        if (!OperatingSystem.IsWindows()) { TutorVpnStatus = "На macOS используйте приложение VPN для подключения."; return; }
        try
        {
            var result = await RunTutorServiceCommandAsync("query");
            TutorVpnConnected = result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            TutorVpnStatus = result.ExitCode == 0 ? TutorVpnConnected ? "Подключено" : "Отключено" : "Подключение не подготовлено. Обратитесь к администратору.";
        }
        catch (Exception error) { TutorVpnStatus = error.Message; }
    }
    [RelayCommand] private async Task ToggleTutorVpnAsync()
    {
        if (TutorVpnBusy || !OperatingSystem.IsWindows()) { await CheckTutorVpnAsync(); return; }
        TutorVpnBusy = true;
        try
        {
            var result = await RunTutorServiceCommandAsync(TutorVpnConnected ? "stop" : "start");
            if (result.ExitCode != 0) { TutorVpnStatus = "Не удалось изменить подключение. Обратитесь к администратору."; return; }
            await Task.Delay(1500);
            await CheckTutorVpnAsync();
        }
        catch (Exception error) { TutorVpnStatus = error.Message; }
        finally { TutorVpnBusy = false; }
    }
    private async Task<(int ExitCode, string Output)> RunTutorServiceCommandAsync(string action)
    {
        if (string.IsNullOrWhiteSpace(TutorTunnelName) || TutorTunnelName.Any(x => !char.IsLetterOrDigit(x) && x is not ('-' or '_')))
            throw new ArgumentException("Имя туннеля: только буквы, цифры, дефис и подчёркивание.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(action); start.ArgumentList.Add("WireGuardTunnel$" + TutorTunnelName);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось обратиться к службе.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }
}

public partial class AccessApplicationCard : ObservableObject
{
    public string Executable { get; }
    public string Name { get; }
    public string Details { get; }
    public Bitmap? Icon { get; }
    [ObservableProperty] private bool isAllowed;
    [ObservableProperty] private bool isVisible = true;
    public AccessApplicationCard(ClassroomApplication app, bool allowed)
    {
        Executable = app.Application.Executable; Name = app.Application.Name; Details = $"Есть на {app.ClientIds.Count} ПК"; IsAllowed = allowed;
        try { if (app.Application.IconBase64 is not null) Icon = new Bitmap(new MemoryStream(Convert.FromBase64String(app.Application.IconBase64))); } catch { }
    }
}
public partial class AccessSiteCard(string address, bool allowed) : ObservableObject
{
    public string Address { get; } = address;
    [ObservableProperty] private bool isAllowed = allowed;
}
