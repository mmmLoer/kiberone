using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kiberone.Core;
using Kiberone.Student.ViewModels;
using Kiberone.Student.Views;

[assembly: AvaloniaTestApplication(typeof(Kiberone.Tests.MailTestAppBuilder))]

namespace Kiberone.Tests;

public sealed class MailTestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class MailTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<MailTestApplication>()
        .WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class StudentMailViewTests
{
    [AvaloniaFact]
    public void Expired_open_letter_is_removed_from_memory()
    {
        var model = new MainViewModel();
        var message = new MailMessageCard(new StudentMailMessage("old", "Figma", "Expired", "secret", DateTimeOffset.UtcNow.AddDays(-2), true, null, ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)));
        model.MailMessages.Add(message);
        model.SelectedMailMessage = message;
        model.RemoveExpiredMail();
        Assert.Empty(model.MailMessages);
        Assert.Null(model.SelectedMailMessage);
    }

    [AvaloniaFact]
    public async Task Inbox_renders_and_opens_letter()
    {
        var model = new MainViewModel { SelectedSectionIndex = 9 };
        var message = new MailMessageCard(new StudentMailMessage("test", "Figma", "Verification",
            "Your code is 123456", DateTimeOffset.UtcNow, true, "123456",
            [new StudentMailLink("Verify email", "https://example.com/verify")]));
        model.MailMessages.Add(message);
        var view = new MailView { DataContext = model };
        var window = new Window { Width = 1360, Height = 900, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
            var letterButton = Assert.Single(buttons, x => ReferenceEquals(x.Command, model.OpenMailMessageCommand));
            Assert.Same(message, letterButton.CommandParameter);
            letterButton.Command!.Execute(letterButton.CommandParameter);
            window.UpdateLayout();
            Assert.Same(message, model.SelectedMailMessage);
            Assert.True(model.IsMessageVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "Your code is 123456");
            Assert.Single(view.GetVisualDescendants().OfType<Button>(), x => ReferenceEquals(x.Command, model.OpenMailLinkCommand));
            Assert.Equal("Письмо", model.SectionTitle);
            string? copied = null;
            model.CopyTextRequested = text => { copied = text; return Task.CompletedTask; };
            await model.CopyMailCodeCommand.ExecuteAsync(null);
            Assert.Equal("123456", copied);
            model.CloseMailMessageCommand.Execute(null);
            window.UpdateLayout();
            Assert.True(model.IsInboxVisible);
        }
        finally { window.Close(); }
    }
}
