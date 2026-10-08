using Kiberone.Infrastructure;
using Kiberone.Tutor.ViewModels;
using Microsoft.EntityFrameworkCore;
using Kiberone.Core;
using System.Reflection;

namespace Kiberone.Tests;

public sealed class TutorQuizNavigationTests
{
    [Fact]
    public void Save_ReturnsToLibrary_OnlyAfterSuccessfulWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<ClassroomDbContext>().UseSqlite("Data Source=:memory:").Options;
            var clients = new ClientRegistry();
            var queue = new ReliableCommandQueue(clients);
            MainViewModel Create(string directory) => new(new TypingLessonService(options), new ClassroomService(options),
                new FileSyncService(options, root), new AssetDistributionService(root, root), clients, queue,
                new QuizService(options, clients, queue), new AuditService(options), directory);
            var model = Create(Path.Combine(root, "quizzes"));
            model.NewQuizCommand.Execute(null);
            Assert.False(model.ShowQuizLibrary);
            model.SaveQuizAndReturnCommand.Execute(null);
            Assert.False(model.HasError);
            Assert.True(model.ShowQuizLibrary);
            Assert.Single(model.SavedQuizzes);
            model.EditSavedQuizCommand.Execute(model.SavedQuizzes[0]);
            model.SaveQuizDraftCommand.Execute(null);
            Assert.False(model.ShowQuizLibrary);

            var blocked = Path.Combine(root, "file");
            File.WriteAllText(blocked, "not a directory");
            var failing = Create(blocked);
            failing.NewQuizCommand.Execute(null);
            failing.SaveQuizAndReturnCommand.Execute(null);
            Assert.True(failing.HasError);
            Assert.False(failing.ShowQuizLibrary);

            var selectedGroup = new ClassroomGroup { Name = "Selected" };
            var selectedStudent = Guid.NewGuid();
            var otherStudent = Guid.NewGuid();
            // Avoid saving actual user settings when selecting a group in this isolated test.
            typeof(MainViewModel).GetField("activeClassGroup", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(model, new GroupCardViewModel(selectedGroup));
            model.Students.Add(new StudentCardViewModel(new StudentSummary(selectedStudent, "Selected", 10, selectedGroup.Id, "Selected", 0, 0, 1)));
            model.Students.Add(new StudentCardViewModel(new StudentSummary(otherStudent, "Other", 10, Guid.NewGuid(), "Other", 0, 0, 1)));
            clients.Heartbeat(new HeartbeatRequest("selected", "1", "test", root, "1", selectedStudent, null, new ClientRuntimeInfo(false, false, "", null)));
            clients.Heartbeat(new HeartbeatRequest("other", "2", "test", root, "1", otherStudent, null, new ClientRuntimeInfo(false, false, "", null)));
            var targets = (IReadOnlyList<string>)typeof(MainViewModel).GetMethod("GetQuizTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null)!;
            Assert.Equal(new[] { "selected" }, targets);
        }
        finally { Directory.Delete(root, true); }
    }
}
