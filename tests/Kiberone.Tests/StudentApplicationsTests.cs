using Kiberone.Core;
using Kiberone.Student.ViewModels;
using Kiberone.Student.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kiberone.Tests;

public sealed class StudentApplicationsTests
{
    [AvaloniaFact]
    public void ApplicationsView_RendersAllowedIconAndRefreshesAfterPermissionChange()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aS1cAAAAASUVORK5CYII=";
        var model = new MainViewModel();
        model.SetInstalledApplications([new("editor.exe", "Редактор", png), new("game.exe", "Игра")]);
        model.SetAccessPolicy(ClassroomAccessPolicy.Empty with { AllowedApps = ["editor.exe"] });
        var view = new ApplicationsView { DataContext = model };
        var window = new Window { Width = 980, Height = 680, Content = view };
        window.Show();
        try
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(Assert.Single(model.AllowedApplications).HasIcon);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Редактор");
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Игра");
            Assert.Contains(view.GetVisualDescendants().OfType<Image>(), image => image.IsVisible && image.Source is not null);
            model.SetAccessPolicy(ClassroomAccessPolicy.Empty);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(model.AllowedApplications);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Редактор");
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Catalogue_ContainsOnlyExplicitPermissions_AndUpdatesWithoutRescan()
    {
        var model = new MainViewModel();
        model.SetInstalledApplications([new("chrome.exe", "Chrome"), new("notepad.exe", "Блокнот"),
            new("CHROME.EXE", "Duplicate"), new("game.exe", "Игра")]);
        Assert.Empty(model.AllowedApplications);
        model.SetAccessPolicy(ClassroomAccessPolicy.Empty with { AllowedApps = [" CHROME ", "notepad.exe"] });
        Assert.Equal(2, model.AllowedApplications.Count);
        Assert.DoesNotContain(model.AllowedApplications, card => card.Executable == "game.exe");
        model.SetAccessPolicy(ClassroomAccessPolicy.Empty with { AllowedApps = ["chrome.exe", "notepad.exe"], BlockedApps = ["chrome"] });
        Assert.Equal("Блокнот", Assert.Single(model.AllowedApplications).Name);
        model.SetAccessPolicy(ClassroomAccessPolicy.Empty);
        Assert.Empty(model.AllowedApplications);
        Assert.True(model.HasNoAllowedApplications);
    }

    [Fact]
    public void MissingOrDamagedIcon_DoesNotPreventDisplayingAllowedApplication()
    {
        var model = new MainViewModel();
        model.SetAccessPolicy(ClassroomAccessPolicy.Empty with { AllowedApps = ["editor.exe"] });
        model.SetInstalledApplications([new("editor.exe", "Редактор", "broken base64!")]);
        var card = Assert.Single(model.AllowedApplications);
        Assert.True(card.HasNoIcon);
        Assert.Equal("Р", card.Initial);
    }
}
