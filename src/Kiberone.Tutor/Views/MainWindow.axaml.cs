using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Kiberone.Tutor;

namespace Kiberone.Tutor.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (Application.Current is App app)
                app.ShutdownServicesAndExit();
        };
    }

    public async Task<string?> PickVpnConfigsFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка с VPN-конфигами WireGuard",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickQuizExportPathAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт викторины в JSON",
            SuggestedFileName = "quiz.json",
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] }
            ]
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickQuizImportPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт викторины из JSON",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] }
            ]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickQuizMediaPathAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Медиа для вопроса",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Изображения и видео")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.mp4", "*.webm"]
                }
            ]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
