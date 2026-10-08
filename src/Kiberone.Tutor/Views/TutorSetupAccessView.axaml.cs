using Avalonia.Controls;
using Avalonia.Interactivity;
using Kiberone.Tutor.ViewModels;
namespace Kiberone.Tutor.Views;
public partial class TutorSetupAccessView : UserControl
{
    public TutorSetupAccessView() => InitializeComponent();
    private void OnRemoveSite(object? sender, RoutedEventArgs args)
    {
        if (sender is Control { DataContext: AccessSiteCard site } && DataContext is MainViewModel model)
            model.RemoveSetupSiteCommand.Execute(site);
    }
}
