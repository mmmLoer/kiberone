using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
namespace Kiberone.Tutor.ViewModels;
public partial class MainViewModel
{
    public ObservableCollection<ProjectCommitInfo> GitCommits { get; } = [];
    public ObservableCollection<string> GitBranches { get; } = [];
    [ObservableProperty] private ProjectCommitInfo? selectedGitCommit;
    [ObservableProperty] private string? selectedGitBranch;
    [ObservableProperty] private string newGitBranch = "";
    [ObservableProperty] private string currentGitBranch = "Основная ветка";
    [ObservableProperty] private string gitDiff = "Выберите сохранение и нажмите «Сравнить».";
    [ObservableProperty] private bool gitBusy;
    [RelayCommand] private async Task LoadGitHistoryAsync()
    {
        var client = SelectedSyncClient?.ClientId;
        if (client is null) return;
        try
        {
            var history = await fileSync.GetGitHistoryAsync(client);
            var branches = await fileSync.GetGitBranchesAsync(client);
            if (SelectedSyncClient?.ClientId != client) return;
            GitCommits.Clear(); foreach (var item in history) GitCommits.Add(item);
            GitBranches.Clear(); foreach (var branch in branches) GitBranches.Add(branch);
            SelectedGitCommit = GitCommits.FirstOrDefault();
            SelectedGitBranch = SelectedGitCommit?.Branch;
            CurrentGitBranch = "Текущая ветка: " + (SelectedGitCommit?.Branch ?? "main");
        }
        catch (Exception error) { StatusMessage = "Не удалось открыть историю: " + error.Message; }
    }
    [RelayCommand] private async Task ShowGitDiffAsync()
    {
        var client = SelectedSyncClient?.ClientId; var commit = SelectedGitCommit;
        if (client is null || commit is null) return;
        try { var diff = await fileSync.GetGitDiffAsync(client, commit.Sha); if (SelectedSyncClient?.ClientId == client) GitDiff = string.IsNullOrEmpty(diff) ? "Файлы не изменились." : diff; }
        catch (Exception error) { StatusMessage = error.Message; }
    }
    [RelayCommand] private async Task CreateGitBranchAsync() => await PerformGitAsync("create", NewGitBranch);
    [RelayCommand] private async Task SwitchGitBranchAsync() => await PerformGitAsync("checkout", SelectedGitBranch);
    [RelayCommand] private async Task MergeGitBranchAsync() => await PerformGitAsync("merge", SelectedGitBranch);
    [RelayCommand] private async Task RestoreGitCommitAsync() => await PerformGitAsync("restore", SelectedGitCommit?.Sha);
    private async Task PerformGitAsync(string operation, string? value)
    {
        var client = SelectedSyncClient?.ClientId;
        if (GitBusy || client is null || string.IsNullOrWhiteSpace(value)) return;
        if (operation != "create" && !await RequestLocationAuthorizationAsync("Изменить сохранения проекта")) return;
        GitBusy = true;
        try
        {
            if (operation == "create") await fileSync.CreateGitBranchAsync(client, value);
            else await fileSync.ChangeGitProjectAsync(client, operation, value);
            if (SelectedSyncClient?.ClientId == client) await LoadSyncedFilesAsync();
            StatusMessage = operation == "create" ? "Ветка создана." : "Готово. Ученику изменения передадутся при синхронизации.";
        }
        catch (Exception error) { StatusMessage = "Не удалось изменить проект: " + error.Message; }
        finally { GitBusy = false; }
    }
}
