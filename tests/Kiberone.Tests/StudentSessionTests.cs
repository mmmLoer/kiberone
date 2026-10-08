using Kiberone.Core;
using Kiberone.Infrastructure;
using Kiberone.Student.ViewModels;

namespace Kiberone.Tests;

public sealed class StudentSessionTests
{
    [Fact]
    public void LoggedInStudent_IsPreserved_WhenTutorChangesGroupOrRoster()
    {
        var model = new MainViewModel();
        var first = new StudentSummary(Guid.NewGuid(), "First", 10, Guid.NewGuid(), "Group A", 0, 0, 1);
        var second = new StudentSummary(Guid.NewGuid(), "Second", 10, Guid.NewGuid(), "Group B", 0, 0, 1);
        model.SetStudents([first, second], "Group A");
        model.ConfirmStudentCommand.Execute(null);
        model.ApplyPreferredGroup("Group B");
        Assert.Equal(first.Id, model.SelectedStudent!.Id);
        model.SetStudents([first, second], "Group B");
        Assert.Equal(first.Id, model.SelectedStudent!.Id);
        model.SetStudents([second], "Group B");
        Assert.Equal(first.Id, model.SelectedStudent!.Id);
    }

    [Fact]
    public void ExitProtection_TracksSuccessfulTutorCommands()
    {
        var model = new MainViewModel { WatchdogEnabled = () => { }, WatchdogDisabled = () => { } };
        ClassroomCommand Command(string kind) => new(Guid.NewGuid(), kind,
            System.Text.Json.JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(model.ApplyCommand(Command(ClassroomCommandKinds.WatchdogOn)).Succeeded);
        Assert.True(model.IsExitBlocked);
        Assert.True(model.ApplyCommand(Command(ClassroomCommandKinds.WatchdogOff)).Succeeded);
        Assert.False(model.IsExitBlocked);
        model.WatchdogEnabled = () => throw new IOException("failed");
        Assert.False(model.ApplyCommand(Command(ClassroomCommandKinds.WatchdogOn)).Succeeded);
        Assert.False(model.IsExitBlocked);
    }

    [Fact]
    public void UpdateFailure_AllowsRetry_WithoutHeartbeatResettingProgress()
    {
        var model = new MainViewModel();
        var update = new StudentUpdateInfo("test", "test", 1);
        var requests = 0;
        model.UpdateRequested = () => requests++;
        model.SetUpdate(update);
        model.InstallUpdateCommand.Execute(null);
        var progress = model.UpdateLabel;
        model.SetUpdate(update);
        Assert.False(model.HasUpdate);
        Assert.Equal(progress, model.UpdateLabel);
        model.SetUpdateFailed();
        Assert.True(model.HasUpdate);
        model.InstallUpdateCommand.Execute(null);
        Assert.Equal(2, requests);
    }

    [Fact]
    public void LeavingTrainer_PausesWithoutLosingTypedText()
    {
        var model = new MainViewModel { TargetText = "abcdef", SelectedSectionIndex = 3 };
        model.StartLesson();
        model.HandleCharacter('a');
        model.NavigateCommand.Execute("6");
        Assert.True(model.IsPaused);
        Assert.Equal(6, model.SelectedSectionIndex);
        Assert.Equal("a", model.TypedText);
        model.TogglePause();
        Assert.False(model.IsPaused);
        Assert.Equal(3, model.SelectedSectionIndex);
    }

    [Fact]
    public void Mail_IsRequestedAfterLoginAndRetriesAreThrottled()
    {
        var calls = 0;
        var model = new MainViewModel();
        model.SetStudents([new StudentSummary(Guid.NewGuid(), "Новый Ученик", 10, Guid.NewGuid(), "Группа", 0, 0, 1)]);
        model.MailAccountRequested = _ => { calls++; throw new HttpRequestException("offline"); };
        model.SetConnection(new StudentConnectionState(true, "", null, DateTimeOffset.UtcNow));
        Assert.Equal(0, calls);
        model.ConfirmStudentCommand.Execute(null);
        Assert.Equal(1, calls);
        Assert.False(model.HasMailAccount);
        model.EnsureMailAccount();
        Assert.Equal(1, calls);
        model.ResetMail();
        model.EnsureMailAccount();
        Assert.Equal(2, calls);
        model.IsLoginVisible = true;
        model.ResetMail();
        model.EnsureMailAccount();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Checkpoint_PreservesNestedFilesForOriginalStudent()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-session-" + Guid.NewGuid());
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(Path.Combine(workspace, "project"));
            await File.WriteAllTextAsync(Path.Combine(workspace, "project", "main.py"), "print('saved')");
            var owner = Guid.NewGuid();
            var sync = new StudentFileSyncClient(Guid.NewGuid().ToString(), workspace) { StudentId = owner };
            var checkpoint = await sync.SaveSessionCheckpointAsync(owner, Path.Combine(root, "backups"));
            Assert.NotNull(checkpoint);
            Assert.Contains(owner.ToString("N"), checkpoint);
            Assert.Equal("print('saved')", await File.ReadAllTextAsync(Path.Combine(checkpoint!, "files", "project", "main.py")));
            Assert.True(File.Exists(Path.Combine(checkpoint!, "session.json")));
            sync.EndSession();
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SaveSessionCheckpointAsync(owner));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ChangeStudent_WaitsForSavingBeforeShowingLogin()
    {
        var completion = new TaskCompletionSource<string>();
        var model = new MainViewModel { IsLoginVisible = false, CurrentStudentName = "Первый", LogoutRequested = _ => completion.Task };
        var change = model.ChangeStudentCommand.ExecuteAsync(null);
        Assert.True(model.IsChangingStudent);
        Assert.False(model.IsLoginVisible);
        completion.SetResult("Сохранено");
        await change;
        Assert.True(model.IsLoginVisible);
        Assert.False(model.IsChangingStudent);
        Assert.Equal("Ученик", model.CurrentStudentName);
        Assert.Equal("Сохранено", model.LoginMessage);
    }

    [Fact]
    public async Task ChangeStudent_SaveFailureKeepsCurrentIdentity()
    {
        var model = new MainViewModel { IsLoginVisible = false, CurrentStudentName = "Первый", LogoutRequested = _ => throw new IOException("locked") };
        await model.ChangeStudentCommand.ExecuteAsync(null);
        Assert.False(model.IsLoginVisible);
        Assert.Equal("Первый", model.CurrentStudentName);
        Assert.True(model.IsNotificationVisible);
        Assert.False(model.IsChangingStudent);
    }
}
