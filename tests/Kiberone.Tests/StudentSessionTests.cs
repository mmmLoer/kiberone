using Kiberone.Core;
using Kiberone.Infrastructure;
using Kiberone.Student.ViewModels;

namespace Kiberone.Tests;

public sealed class StudentSessionTests
{
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
