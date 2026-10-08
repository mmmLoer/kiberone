using Bitmap = Avalonia.Media.Imaging.Bitmap;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
using System.Collections.ObjectModel;

namespace Kiberone.Student.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<InstalledApplication> applicationInventory = [];
    private ClassroomAccessPolicy applicationPolicy = ClassroomAccessPolicy.Empty;
    [ObservableProperty] private bool appsBusy;
    [ObservableProperty] private string appsStatus = "Тьютор выберет приложения для твоей группы.";
    public ObservableCollection<StudentAppCard> AllowedApplications { get; } = [];
    public bool HasNoAllowedApplications => !AppsBusy && AllowedApplications.Count == 0;

    partial void OnAppsBusyChanged(bool value) => OnPropertyChanged(nameof(HasNoAllowedApplications));

    public void SetAccessPolicy(ClassroomAccessPolicy policy)
    {
        applicationPolicy = policy;
        RebuildAllowedApplications();
    }

    public void SetInstalledApplications(IReadOnlyList<InstalledApplication> applications)
    {
        applicationInventory = applications;
        RebuildAllowedApplications();
    }

    private static string NormalizeApplication(string name)
    {
        var value = name.Trim();
        if (value.Length == 0 || value.IndexOfAny(['/', '\\', ':']) >= 0) return string.Empty;
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value : value + ".exe";
    }

    private void RebuildAllowedApplications()
    {
        var allowed = applicationPolicy.AllowedApps.Select(NormalizeApplication).Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocked = applicationPolicy.BlockedApps.Select(NormalizeApplication).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previous = AllowedApplications.ToArray();
        AllowedApplications.Clear();
        foreach (var app in applicationInventory
            .Where(app => allowed.Contains(NormalizeApplication(app.Executable)) && !blocked.Contains(NormalizeApplication(app.Executable)))
            .DistinctBy(app => NormalizeApplication(app.Executable), StringComparer.OrdinalIgnoreCase)
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase))
            AllowedApplications.Add(new StudentAppCard(app));
        foreach (var card in previous) card.Dispose();
        AppsStatus = allowed.Count == 0
            ? "Тьютор ещё не выбрал приложения для твоей группы."
            : AllowedApplications.Count == 0
                ? "Разрешённых приложений пока нет на этом компьютере. Попроси тьютора помочь."
                : "Эти приложения можно использовать на занятии.";
        OnPropertyChanged(nameof(HasNoAllowedApplications));
    }

    [RelayCommand] private async Task RefreshInstalledAppsAsync()
    {
        if (AppsBusy || InstalledAppsRequested is null) return;
        AppsBusy = true;
        try { SetInstalledApplications(await Task.Run(InstalledAppsRequested)); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"Application inventory: {error.GetType().Name}");
            AppsStatus = "Не получилось обновить приложения. Попробуй ещё раз.";
        }
        finally { AppsBusy = false; }
    }
}

public sealed class StudentAppCard : IDisposable
{
    public string Name { get; }
    public string Executable { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool HasNoIcon => Icon is null;
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "▦" : Name[..1].ToUpperInvariant();

    public StudentAppCard(InstalledApplication application)
    {
        Name = application.Name;
        Executable = application.Executable;
        try
        {
            if (application.IconBase64 is { Length: > 0 and <= 32000 } icon)
            {
                using var stream = new MemoryStream(Convert.FromBase64String(icon));
                Icon = new Bitmap(stream);
            }
        }
        catch (Exception error) when (error is FormatException or ArgumentException or IOException or NotSupportedException)
        { /* Missing or damaged icons use the application's initial. */ }
    }
    public void Dispose() => Icon?.Dispose();
}
