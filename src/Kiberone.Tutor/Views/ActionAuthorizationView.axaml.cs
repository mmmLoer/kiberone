using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Kiberone.Tutor.ViewModels;
namespace Kiberone.Tutor.Views;
public partial class ActionAuthorizationView : UserControl
{
    public ActionAuthorizationView()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (DataContext is not MainViewModel model) return;
            var command = e.Key == Key.Escape ? model.CancelActionAuthorizationCommand : e.Key == Key.Enter ? model.ConfirmActionAuthorizationCommand : null;
            if (command?.CanExecute(null) == true) { command.Execute(null); e.Handled = true; }
        };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
            Dispatcher.UIThread.Post(() => PasswordInput?.Focus());
    }
}
