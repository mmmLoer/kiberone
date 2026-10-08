using System.Reflection;
using System.Text.Json;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Kiberone.Tutor.ViewModels;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kiberone.Tutor.Views;

namespace Kiberone.Tests;

public sealed class TutorSetupAndAuthorizationTests
{
    [Fact]
    public async Task Setup_UsesTutorInventory_PreservesSelection_AndValidatesSiteRules()
    {
        await using var test = await Fixture.Create();
        var model = test.Model;
        model.SetupApplicationsProvider = () => [new("code.exe", "Code"), new("chrome.exe", "Chrome"), new("CHROME.EXE", "Duplicate")];
        await model.RefreshSetupApplicationsCommand.ExecuteAsync(null);
        Assert.Equal(2, model.SetupApplications.Count);
        Assert.All(model.SetupApplications, app => Assert.False(app.IsAllowed));
        model.SetupApplications.Single(x => x.Executable == "chrome.exe").IsAllowed = true;
        model.SetupApplications.Single(x => x.Executable == "code.exe").IsAllowed = false;
        await model.RefreshSetupApplicationsCommand.ExecuteAsync(null);
        Assert.False(model.SetupApplications.Single(x => x.Executable == "code.exe").IsAllowed);
        model.SetupSite = "https://FIGMA.com/";
        model.AddSetupSiteCommand.Execute(null);
        model.SetupSite = "figma.com";
        model.AddSetupSiteCommand.Execute(null);
        Assert.Single(model.SetupSites);
        model.SetupOnlyAllowedSites = true;
        var policy = model.BuildSetupAccessPolicy();
        Assert.Equal(["chrome.exe"], policy.AllowedApps);
        Assert.Empty(policy.BlockedApps);
        Assert.Equal(["figma.com"], policy.AllowedSites);
        model.SetupApplicationsProvider = () => Enumerable.Range(0, 401).Select(i => new InstalledApplication($"app{i}.exe", $"App {i}")).Append(new InstalledApplication("chrome.exe", "Chrome")).ToArray();
        await model.RefreshSetupApplicationsCommand.ExecuteAsync(null);
        var largeInventoryPolicy = model.BuildSetupAccessPolicy();
        Assert.Single(largeInventoryPolicy.AllowedApps);
        Assert.Empty(largeInventoryPolicy.BlockedApps);
        model.SetupSites.Clear();
        Assert.Throws<ArgumentException>(() => model.BuildSetupAccessPolicy());
        model.SetupOnlyAllowedSites = false;
        foreach (var app in model.SetupApplications) app.IsAllowed = false;
        Assert.Throws<ArgumentException>(() => model.BuildSetupAccessPolicy());
    }

    [Fact]
    public async Task Finish_AppliesPolicyToLocationGroups_PersistsWithoutPassword_AndStopsWizard()
    {
        await using var test = await Fixture.Create();
        var first = await test.Classroom.CreateGroupAsync(new("First", "Python", "", "VM TEST"));
        await test.Classroom.CreateGroupAsync(new("Second", "Figma", "", "VM TEST"));
        var other = await test.Classroom.CreateGroupAsync(new("Other", "Python", "", "OTHER"));
        var model = test.Model;
        model.NeedsLocationSetup = true;
        model.SetupStep = 3;
        model.SetupLocationPassword = "test-setup-secret";
        model.SetupApplicationsProvider = () => [new("chrome.exe", "Chrome")];
        await model.RefreshSetupApplicationsCommand.ExecuteAsync(null);
        model.SetupApplications.Single().IsAllowed = true;
        await model.FinishTutorSetupCommand.ExecuteAsync(null);
        Assert.Equal("", model.SetupError);
        Assert.False(model.NeedsLocationSetup);
        Assert.Equal("", model.SetupLocationPassword);
        foreach (var group in await test.Classroom.ListGroupsAsync("VM TEST"))
            Assert.Contains("chrome.exe", ClassroomService.ParseAccessPolicy(group.AccessPolicyJson).AllowedApps);
        Assert.False(ClassroomService.ParseAccessPolicy((await test.Classroom.ListGroupsAsync("OTHER")).Single().AccessPolicyJson).Enabled);
        var settingsText = await File.ReadAllTextAsync(Path.Combine(test.Root, "settings", "settings.json"));
        Assert.DoesNotContain("test-setup-secret", settingsText);
        var settings = JsonSerializer.Deserialize<TutorLocalSettings>(settingsText)!;
        Assert.True(settings.LocationSetupCompleted);
        Assert.Contains("chrome.exe", settings.DefaultAccessPolicy!.AllowedApps);
        Assert.NotEqual(first.Id, other.Id);
        model.GroupName = "New group after setup"; model.GroupModule = "Python";
        await model.CreateGroupCommand.ExecuteAsync(null);
        Assert.Contains("chrome.exe", ClassroomService.ParseAccessPolicy((await test.Classroom.ListGroupsAsync("VM TEST")).Single(x => x.Name == model.GroupName || x.Name == "New group after setup").AccessPolicyJson).AllowedApps);
    }

    [Fact]
    public async Task SettingsWriteFailure_LeavesSetupOpenForRetry()
    {
        await using var test = await Fixture.Create();
        File.WriteAllText(Path.Combine(test.Root, "settings"), "not a directory");
        var model = test.Model;
        model.NeedsLocationSetup = true; model.SetupStep = 3;
        model.SetupApplicationsProvider = () => [new("chrome.exe", "Chrome")];
        await model.RefreshSetupApplicationsCommand.ExecuteAsync(null);
        model.SetupApplications.Single().IsAllowed = true;
        await model.FinishTutorSetupCommand.ExecuteAsync(null);
        Assert.True(model.NeedsLocationSetup);
        Assert.Equal(3, model.SetupStep);
        Assert.NotEmpty(model.SetupError);
    }

    [AvaloniaFact]
    public async Task SetupAndAuthorizationViews_RenderControls_AndCancelThroughBoundCommand()
    {
        await using var test = await Fixture.Create();
        var model = test.Model;
        model.SetupApplicationsProvider = () => [new("chrome.exe", "Chrome")];
        await model.RefreshSetupApplicationsCommand.ExecuteAsync(null);
        model.SetupSite = "figma.com"; model.AddSetupSiteCommand.Execute(null);
        var setup = new TutorSetupAccessView { DataContext = model };
        var window = new Window { Width = 680, Height = 760, Content = setup };
        window.Show();
        try
        {
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(setup.GetVisualDescendants().OfType<CheckBox>(), x => Equals(x.Content, "Chrome"));
            var remove = setup.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Удалить"));
            remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(model.SetupSites);
            var prompt = new ActionAuthorizationView { DataContext = model };
            prompt.Bind(Control.IsVisibleProperty, new Binding(nameof(MainViewModel.ShowActionAuthorization)));
            window.Content = prompt;
            var pending = model.RequestLocationAuthorizationAsync("Protected action");
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(prompt.IsVisible);
            Assert.Equal('•', prompt.GetVisualDescendants().OfType<TextBox>().Single().PasswordChar);
            var cancel = prompt.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Отмена"));
            cancel.Command!.Execute(null);
            Assert.False(await pending);
            Dispatcher.UIThread.RunJobs();
            Assert.False(prompt.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task DestructiveAction_WaitsForPassword_WrongPasswordDoesNotDelete_CancelAborts_ValidPasswordDeletes()
    {
        await using var test = await Fixture.Create();
        var group = await test.Classroom.CreateGroupAsync(new("Do not delete without confirmation", "Python", "", "VM TEST"));
        var model = test.Model;
        model.SelectedGroup = new GroupCardViewModel(group);
        model.LocationPasswordValidator = (_, _, password) => Task.FromResult(password == "test-password");
        var action = model.DeleteGroupCommand.ExecuteAsync(null);
        Assert.True(model.ShowActionAuthorization);
        Assert.False(action.IsCompleted);
        model.AuthorizationPassword = "wrong";
        await model.ConfirmActionAuthorizationCommand.ExecuteAsync(null);
        Assert.True(model.ShowActionAuthorization);
        Assert.Single(await test.Classroom.ListGroupsAsync("VM TEST"));
        model.CancelActionAuthorizationCommand.Execute(null);
        await action;
        Assert.Single(await test.Classroom.ListGroupsAsync("VM TEST"));
        action = model.DeleteGroupCommand.ExecuteAsync(null);
        model.AuthorizationPassword = "test-password";
        await model.ConfirmActionAuthorizationCommand.ExecuteAsync(null);
        await action;
        Assert.False(model.ShowActionAuthorization);
        Assert.Equal("", model.AuthorizationPassword);
        Assert.Empty(await test.Classroom.ListGroupsAsync("VM TEST"));
    }

    [Fact]
    public async Task BackgroundCredentialUse_HasNoDialog_ConfirmationDoesNotAuthorizeChangedLocation()
    {
        await using var test = await Fixture.Create();
        var model = test.Model;
        model.LocationUploadPassword = "existing-test-password";
        Assert.True(await model.RequestLocationAuthorizationAsync("Background", requireConfirmation: false));
        Assert.False(model.ShowActionAuthorization);
        var pending = model.RequestLocationAuthorizationAsync("Protected action");
        model.LocationPasswordValidator = (_, _, _) => Task.FromResult(true);
        model.AuthorizationPassword = "test-password";
        model.LocationName = "OTHER";
        await model.ConfirmActionAuthorizationCommand.ExecuteAsync(null);
        Assert.False(await pending);
        Assert.False(model.ShowActionAuthorization);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required MainViewModel Model { get; init; }
        public required ClassroomService Classroom { get; init; }
        public static async Task<Fixture> Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "kiberone-setup-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = ClassroomDatabase.CreateOptions(Path.Combine(root, "classroom.db"));
            await ClassroomDatabase.InitializeAsync(options);
            var classroom = new ClassroomService(options);
            var clients = new ClientRegistry();
            var commands = new ReliableCommandQueue(clients);
            var model = new MainViewModel(new TypingLessonService(options), classroom, new FileSyncService(options, root),
                new AssetDistributionService(root, root), clients, commands, new QuizService(options, clients, commands),
                new AuditService(options), Path.Combine(root, "quizzes"), Path.Combine(root, "settings"), new LocationCredentialStore(Path.Combine(root, "credentials")));
            typeof(MainViewModel).GetField("loadingSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, true);
            model.LocationName = "VM TEST";
            model.StudentSavesFolder = Path.Combine(root, "saves");
            return new Fixture { Root = root, Classroom = classroom, Model = model };
        }
        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
