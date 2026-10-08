using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tutor.ViewModels;

public partial class MainViewModel
{
    private AppUpdateManifest? tutorUpdate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallTutorUpdate))]
    [NotifyCanExecuteChangedFor(nameof(InstallTutorUpdateCommand))]
    private bool isTutorUpdateBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallTutorUpdate))]
    [NotifyCanExecuteChangedFor(nameof(InstallTutorUpdateCommand))]
    private bool tutorUpdateAvailable;
    [ObservableProperty] private string tutorUpdateStatus = "Обновления Tutor ещё не проверены.";
    [ObservableProperty] private string studentUpdateStatus = "Пакеты Student ещё не проверены.";
    private bool syncingStudentUpdates;

    public string TutorUpdateVersionLabel => $"Tutor {BuildInfo.Version} · {(BuildInfo.Channel == "beta" ? "бета" : "релиз")}";
    public bool CanInstallTutorUpdate => TutorUpdateAvailable && !IsTutorUpdateBusy;
    // Transport seams keep update checks testable without hitting the real Hub.
    public Func<string, string, CancellationToken, Task<AppUpdateManifest?>>? UpdateManifestProvider { get; set; }
    public Func<string, string, CancellationToken, Task<byte[]>>? UpdatePackageProvider { get; set; }
    public Func<string, AppUpdateManifest, CancellationToken, Task>? TutorUpdateInstaller { get; set; }
    public Action? TutorUpdateRestartRequested { get; set; }

    private Task<AppUpdateManifest?> FetchUpdateManifestAsync(string app, string channel, CancellationToken ct) =>
        UpdateManifestProvider?.Invoke(app, channel, ct) ?? CreateHubClient().GetAppUpdateAsync(app, channel, ct);
    private Task<byte[]> FetchUpdatePackageAsync(string app, string channel, AppUpdateManifest manifest, CancellationToken ct) =>
        UpdatePackageProvider?.Invoke(app, channel, ct) ?? CreateHubClient().DownloadAppUpdateFileAsync(app, channel, manifest, ct);

    [RelayCommand]
    private async Task CheckTutorUpdateAsync()
    {
        if (IsTutorUpdateBusy) return;
        IsTutorUpdateBusy = true;
        TutorUpdateAvailable = false;
        tutorUpdate = null;
        TutorUpdateStatus = "Проверяем обновления Tutor…";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var manifest = await FetchUpdateManifestAsync("tutor", BuildInfo.Channel, deadline.Token);
            if (manifest is null || manifest.Version == BuildInfo.Version)
            {
                TutorUpdateStatus = "Установлена актуальная версия Tutor.";
                return;
            }
            if (!AppReleaseVersion.IsNewer(manifest.Version, BuildInfo.Version))
            {
                TutorUpdateStatus = "В этом канале нет более новой версии Tutor.";
                return;
            }
            AppUpdateInstaller.ValidateManifest("tutor", manifest, BuildInfo.Version);
            tutorUpdate = manifest;
            TutorUpdateAvailable = true;
            TutorUpdateStatus = $"Доступен Tutor {manifest.Version}. Установка закроет и перезапустит Tutor.";
        }
        catch (Exception error)
        {
            TutorUpdateStatus = $"Не удалось проверить обновления Tutor: {error.Message}";
        }
        finally { IsTutorUpdateBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInstallTutorUpdate))]
    private async Task InstallTutorUpdateAsync()
    {
        var manifest = tutorUpdate;
        if (manifest is null || IsTutorUpdateBusy) return;
        IsTutorUpdateBusy = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            if (TutorUpdateRestartRequested is null)
                throw new InvalidOperationException("Перезапуск Tutor недоступен.");
            TutorUpdateStatus = "Скачиваем и проверяем обновление Tutor…";
            var bytes = await FetchUpdatePackageAsync("tutor", BuildInfo.Channel, manifest, deadline.Token);
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KIBERone Classroom", "tutor-updates");
            var package = await AppUpdateInstaller.StageAsync("tutor", manifest, bytes, BuildInfo.Version, directory, deadline.Token);
            TutorUpdateStatus = AppUpdateInstaller.TutorInstallStatus(Environment.ProcessPath
                ?? throw new InvalidOperationException("Не найден EXE Tutor."));
            await (TutorUpdateInstaller ?? AppUpdateInstaller.BeginTutorInstallAsync)(package, manifest, deadline.Token);
            TutorUpdateStatus = "Установка подготовлена. Перезапускаем Tutor…";
            TutorUpdateRestartRequested();
        }
        catch (Exception error)
        {
            TutorUpdateStatus = $"Обновление не установлено: {error.Message}";
        }
        finally { IsTutorUpdateBusy = false; }
    }

    private async Task TrySyncStudentUpdateFromHubAsync(bool quiet)
    {
        if (syncingStudentUpdates) return;
        syncingStudentUpdates = true;
        var reports = new List<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            StudentUpdateStatus = "Проверяем пакеты Student: релиз и бета…";
            foreach (var channel in new[] { "release", "beta" })
            {
                try
                {
                    var manifest = await FetchUpdateManifestAsync("student", channel, deadline.Token);
                    if (manifest is null) { reports.Add($"{channel}: нет пакета"); continue; }
                    if (!AppReleaseVersion.IsValid(manifest.Version) || AppReleaseVersion.ChannelFor(manifest.Version) != channel
                        || !StudentUpdateSignature.VerifyApp("student", manifest.Version, manifest.Size, manifest.Sha256, manifest.Signature))
                        throw new InvalidDataException("Недействительный канал или подпись пакета Student.");
                    var local = assets.GetStudentRelease(channel);
                    if (local is not null && !AppReleaseVersion.IsNewer(manifest.Version, local.Version))
                    { reports.Add($"{channel}: {local.Version}, уже актуально"); continue; }
                    var bytes = await FetchUpdatePackageAsync("student", channel, manifest, deadline.Token);
                    var stored = assets.ImportStudentRelease(manifest, bytes);
                    reports.Add($"{channel}: {stored.Version} готов к раздаче");
                }
                catch (Exception error) { reports.Add($"{channel}: {error.Message}"); }
            }
            StudentUpdateStatus = string.Join(" · ", reports);
            if (!quiet) { HubStatus = StudentUpdateStatus; StatusMessage = StudentUpdateStatus; }
        }
        finally { syncingStudentUpdates = false; }
    }
}
