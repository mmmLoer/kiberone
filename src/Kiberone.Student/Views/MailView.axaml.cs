using Avalonia;
using Avalonia.Threading;
using Kiberone.Student.ViewModels;
namespace Kiberone.Student.Views;
public partial class MailView : Avalonia.Controls.UserControl
{
    private readonly DispatcherTimer expiry = new() { Interval = TimeSpan.FromSeconds(30) };
    public MailView()
    {
        InitializeComponent();
        expiry.Tick += (_, _) => { if (DataContext is MainViewModel vm) vm.RemoveExpiredMail(); };
        AttachedToVisualTree += (_, _) => { if (DataContext is MainViewModel vm) vm.RemoveExpiredMail(); expiry.Start(); };
        DetachedFromVisualTree += (_, _) => expiry.Stop();
    }
}
