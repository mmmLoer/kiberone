using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Kiberone.Tutor;
using Kiberone.Tutor.ViewModels;

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

    private void OnClassStudentPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicks on the ⋯ button should open its flyout, not only select the row.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null)
            return;
        if (sender is not Control control || control.DataContext is not StudentCardViewModel student)
            return;
        if (DataContext is not MainViewModel viewModel)
            return;

        var toggle = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        viewModel.SelectClassStudentCore(student, toggle);
        e.Handled = true;
    }

    private void OnClassStudentMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not StudentCardViewModel student)
            return;
        if (DataContext is MainViewModel viewModel)
            viewModel.SelectClassStudentCore(student, toggle: false);
    }

    public async Task<string?> PickStudentSavesFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка сохранений учеников",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
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

    public async Task<IReadOnlyList<string>> PickStarterFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Файлы стартового пакета",
            AllowMultiple = true
        });
        return files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>().ToList();
    }

    public async Task<string?> PickStarterFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка в стартовый пакет",
            AllowMultiple = false
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickWallpaperFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Обои для компьютеров учеников",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Изображения")
                {
                    Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp"]
                }
            ]
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
