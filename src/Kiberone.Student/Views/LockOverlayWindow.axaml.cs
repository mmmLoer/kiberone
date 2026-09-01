using Avalonia.Controls;
using Avalonia.Input;

namespace Kiberone.Student.Views;

public partial class LockOverlayWindow : Window
{
    public bool AllowClose { get; set; }

    public LockOverlayWindow()
    {
        InitializeComponent();
        Closing += (_, eventArgs) =>
        {
            if (!AllowClose) eventArgs.Cancel = true;
        };
        KeyDown += (_, eventArgs) => eventArgs.Handled = true;
    }
}
