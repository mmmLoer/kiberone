using Kiberone.Core;
using Kiberone.Infrastructure;
using Kiberone.Student.ViewModels;
using System.Text.Json;

namespace Kiberone.Tests;

public sealed class MixedGroupsTests
{
    [Fact]
    public async Task ConcurrentGroups_KeepPoliciesHomesAndCommandsSeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-mixed-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var options = ClassroomDatabase.CreateOptions(Path.Combine(root, "classroom.db"));
            await ClassroomDatabase.InitializeAsync(options);
            var classroom = new ClassroomService(options);
            var sync = new FileSyncService(options, Path.Combine(root, "files"));
            var registry = new ClientRegistry();
            var queue = new ReliableCommandQueue(registry);
            var groups = new[] {
                await classroom.CreateGroupAsync(new GroupDraft("Group A", "Python", "", "TEST")),
                await classroom.CreateGroupAsync(new GroupDraft("Group B", "Figma", "", "TEST")) };
            for (var i = 0; i < 2; i++)
                await classroom.SaveAccessPolicyAsync(groups[i].Id,
                    new ClassroomAccessPolicy("", true, true, [i == 0 ? "code.exe" : "figma.exe"], [], false, [], []));
            var students = new List<(Guid Id, string Client, int Group)>();
            for (var i = 0; i < 12; i++)
            {
                var g = i % 2;
                var student = await classroom.CreateStudentAsync(new StudentDraft("SameSurname", "Child" + i, 10, groups[g].Id, "", "", ""));
                var client = "mixed-pc-" + i;
                students.Add((student.Id, client, g));
                registry.Heartbeat(new HeartbeatRequest(client, "1", client, root, "test", student.Id, null, new ClientRuntimeInfo(false, false, "", 90)));
                sync.BindClient(client, student.Id);
            }
            var homes = await Task.WhenAll(students.Select(x => sync.ResolveStudentHomeAsync(x.Id)));
            Assert.Equal(12, students.Select(x => sync.GetClientFolderPath(x.Client)).Distinct().Count());
            await Task.WhenAll(students.Select(async x => {
                var policy = await classroom.GetStudentAccessPolicyAsync(x.Id);
                Assert.Equal(x.Group == 0 ? "code.exe" : "figma.exe", Assert.Single(policy.AllowedApps));
                Assert.Equal(x.Group == 0 ? "Python" : "Figma", (await sync.ResolveStudentHomeAsync(x.Id))!.Module);
            }));
            queue.Enqueue(new EnqueueCommandRequest(students.Where(x => x.Group == 0).Select(x => x.Client).ToArray(),
                ClassroomCommandKinds.Message, JsonSerializer.SerializeToElement(new { text = "Only A" })));
            foreach (var x in students)
                Assert.Equal(x.Group == 0 ? 1 : 0, queue.GetPending(x.Client).Count);
            Assert.Equal(12, registry.GetAll().Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TwoLoggedInClients_IgnoreGroupChangesIndependently()
    {
        var a = new StudentSummary(Guid.NewGuid(), "A", 10, Guid.NewGuid(), "Group A", 0, 0, 1);
        var b = new StudentSummary(Guid.NewGuid(), "B", 10, Guid.NewGuid(), "Group B", 0, 0, 1);
        var first = new MainViewModel(); var second = new MainViewModel();
        first.SetStudents([a,b], "Group A"); second.SetStudents([a,b], "Group B");
        first.ConfirmStudentCommand.Execute(null); second.ConfirmStudentCommand.Execute(null);
        for (var i = 0; i < 10; i++)
        {
            var preferred = i % 2 == 0 ? "Group A" : "Group B";
            foreach (var client in new[] { first, second }) {
                client.ApplyPreferredGroup(preferred); client.SetStudents([a,b], preferred);
            }
            Assert.Equal(a.Id, first.SelectedStudent!.Id); Assert.Equal(b.Id, second.SelectedStudent!.Id);
        }
    }
}
