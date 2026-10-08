using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tutor.ViewModels;

public partial class MainViewModel
{
    private ClassroomAccessPolicy defaultAccessPolicy = ClassroomAccessPolicy.Empty;
    public Func<IReadOnlyList<InstalledApplication>> SetupApplicationsProvider { get; set; } = InstalledApplicationCatalog.Read;
    public ObservableCollection<AccessApplicationCard> SetupApplications { get; } = [];
    public ObservableCollection<AccessSiteCard> SetupSites { get; } = [];
    [ObservableProperty] private string setupSite = "";
    [ObservableProperty] private string setupSearch = "";
    [ObservableProperty] private string setupError = "";
    [ObservableProperty] private bool setupOnlyAllowedApps = true;
    [ObservableProperty] private bool setupOnlyAllowedSites;
    [ObservableProperty] private bool setupApplicationsBusy;
    public bool IsSetupAccess => NeedsLocationSetup && SetupStep == 3;

    partial void OnSetupSearchChanged(string value)
    {
        foreach (var app in SetupApplications)
            app.IsVisible = app.Name.Contains(value, StringComparison.CurrentCultureIgnoreCase);
    }

    [RelayCommand] private async Task RefreshSetupApplicationsAsync()
    {
        if (SetupApplicationsBusy) return;
        SetupApplicationsBusy = true;
        SetupError = "";
        try
        {
            var selected = SetupApplications.ToDictionary(x => x.Executable, x => x.IsAllowed, StringComparer.OrdinalIgnoreCase);
            var inventory = await Task.Run(SetupApplicationsProvider);
            foreach (var item in SetupApplications) item.Icon?.Dispose();
            SetupApplications.Clear();
            foreach (var app in inventory.DistinctBy(x => x.Executable, StringComparer.OrdinalIgnoreCase))
                SetupApplications.Add(new AccessApplicationCard(new ClassroomApplication(app, []),
                    selected.TryGetValue(app.Executable, out var allowed) ? allowed :
                    defaultAccessPolicy.AllowedApps.Contains(app.Executable, StringComparer.OrdinalIgnoreCase)));
            OnSetupSearchChanged(SetupSearch);
        }
        catch { SetupError = "Не удалось загрузить приложения."; }
        finally { SetupApplicationsBusy = false; }
    }

    [RelayCommand] private void AddSetupSite()
    {
        try
        {
            var site = SiteRule.Normalize(SetupSite);
            if (SetupSites.All(x => x.Address != site)) SetupSites.Add(new AccessSiteCard(site, true));
            SetupSite = ""; SetupError = "";
        }
        catch (ArgumentException error) { SetupError = error.Message; }
    }
    [RelayCommand] private void RemoveSetupSite(AccessSiteCard? site) { if (site is not null) SetupSites.Remove(site); }

    private async Task OpenSetupAccessAsync()
    {
        SetupStep = 3;
        SetupOnlyAllowedApps = defaultAccessPolicy == ClassroomAccessPolicy.Empty || defaultAccessPolicy.OnlyAllowedApps;
        SetupOnlyAllowedSites = defaultAccessPolicy.OnlyAllowedSites;
        SetupSites.Clear();
        foreach (var site in defaultAccessPolicy.AllowedSites) SetupSites.Add(new AccessSiteCard(site, true));
        await RefreshSetupApplicationsAsync();
    }

    public ClassroomAccessPolicy BuildSetupAccessPolicy()
    {
        if (SetupOnlyAllowedApps && !SetupApplications.Any(x => x.IsAllowed))
            throw new ArgumentException("Выберите хотя бы одно приложение.");
        if (SetupOnlyAllowedSites && SetupSites.Count == 0)
            throw new ArgumentException("Добавьте хотя бы один сайт.");
        return new ClassroomAccessPolicy("", true, SetupOnlyAllowedApps,
            SetupApplications.Where(x => x.IsAllowed).Select(x => x.Executable).ToArray(),
            [],
            SetupOnlyAllowedSites, SetupSites.Select(x => x.Address).ToArray(), []);
    }

    [RelayCommand] private async Task FinishTutorSetupAsync()
    {
        if (IsBusy || SetupApplicationsBusy) return;
        IsBusy = true; SetupError = "";
        try
        {
            var policy = BuildSetupAccessPolicy();
            foreach (var group in await classroom.ListGroupsAsync(LocationName))
                await classroom.SaveAccessPolicyAsync(group.Id, policy);
            defaultAccessPolicy = policy;
            await RefreshCoreAsync();
            NeedsLocationSetup = false;
            SaveSettings();
            SetupLocationPassword = "";
            SetupStep = 0;
        }
        catch (ArgumentException error) { SetupError = error.Message; }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceWarning($"Tutor setup failed: {error.GetType().Name}");
            NeedsLocationSetup = true; SetupStep = 3;
            SetupError = "Не удалось сохранить настройки. Попробуйте ещё раз.";
        }
        finally { IsBusy = false; }
    }

    private async Task ApplyDefaultAccessToNewGroupsAsync()
    {
        if (defaultAccessPolicy == ClassroomAccessPolicy.Empty) return;
        foreach (var group in await classroom.ListGroupsAsync(LocationName))
            if (string.IsNullOrWhiteSpace(group.AccessPolicyJson))
                await classroom.SaveAccessPolicyAsync(group.Id, defaultAccessPolicy);
    }
}
