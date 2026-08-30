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
}