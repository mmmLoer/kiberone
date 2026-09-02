using System.Text;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Kiberone.Tests;

public sealed class FileSyncServiceTests : IAsyncLifetime
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"kiberone-sync-{Guid.NewGuid():N}");
    private DbContextOptions<ClassroomDbContext> options = null!;
    private FileSyncService service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(testRoot);
        options = ClassroomDatabase.CreateOptions(Path.Combine(testRoot, "test.db"));
        await ClassroomDatabase.InitializeAsync(options);
        service = new FileSyncService(options, Path.Combine(testRoot, "files"));
    }

    [Fact]
    public async Task DangerousFile_RequiresTutorApproval()
    {
        var prepared = await service.PrepareAsync(new SyncPrepareRequest("pc-01", [new SyncChange("build/game.exe", SyncChangeKind.Created, 4)], false, false, 5));
        Assert.True(prepared.Required);
        Assert.Equal(SyncApprovalStatus.Pending, prepared.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Upload("pc-01", "build/game.exe", "MZ00"));

        await service.DecideAsync(prepared.Id, true);
        var uploaded = await Upload("pc-01", "build/game.exe", "MZ00");
        await service.CompleteAsync("pc-01");

        Assert.Equal("build/game.exe", uploaded.Path);
        Assert.Equal(SyncApprovalStatus.Completed, (await service.GetApprovalAsync("pc-01"))?.Status);
    }

    [Fact]
    public void EnsureGroupFolder_CreatesGroupAndStudentDirectories()
    {
        var groupPath = service.EnsureGroupFolder("Python 01");
        var studentPath = service.EnsureStudentFolder("Python 01", "Иванов", "Артём");

        Assert.True(Directory.Exists(groupPath));
        Assert.True(Directory.Exists(studentPath));
        Assert.Equal("Python 01", Path.GetFileName(groupPath));
        Assert.Equal("Иванов Артём", Path.GetFileName(studentPath));
        Assert.Equal(groupPath, Directory.GetParent(studentPath)?.FullName);
    }

    [Fact]
    public void EnsureGroupFolder_SanitizesEmptyAndPathCharacters()
    {
        var empty = service.EnsureGroupFolder("   ");
        var nested = service.EnsureGroupFolder("Python/01");

        Assert.True(Directory.Exists(empty));
        Assert.Equal("без имени", Path.GetFileName(empty));
        Assert.True(Directory.Exists(nested));
        Assert.Equal("Python_01", Path.GetFileName(nested));
        Assert.DoesNotContain(Path.DirectorySeparatorChar, Path.GetFileName(nested));
    }

    [Fact]
    public async Task ModifiedFile_IsVersionedAndCanBeRestored()
    {
        await PrepareSafe("pc-02", "project/main.cs", SyncChangeKind.Created);
        await Upload("pc-02", "project/main.cs", "version one");
        await service.CompleteAsync("pc-02");
        await PrepareSafe("pc-02", "project/main.cs", SyncChangeKind.Modified);
        await Upload("pc-02", "project/main.cs", "version two");

        var versions = await service.ListVersionsAsync("pc-02", "project/main.cs");
        var first = versions.Single(x => x.Label == "Первая загрузка");
        await service.RestoreVersionAsync(new RestoreVersionRequest("pc-02", "project/main.cs", first.Id));
        await using var restored = await service.OpenDownloadAsync("pc-02", "project/main.cs");
        using var reader = new StreamReader(restored!, Encoding.UTF8);

        Assert.Equal("version one", await reader.ReadToEndAsync());
        Assert.Contains(versions, x => x.Label == "До изменения");
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData(".git/config")]
    [InlineData("node_modules/pkg.js")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("D:\\secret.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData("   ")]
    [InlineData("")]
    public async Task UnsafeOrExcludedPaths_AreRejected(string path)
    {
        await Assert.ThrowsAsync<LessonValidationException>(() => service.PrepareAsync(
            new SyncPrepareRequest("pc-03", [new SyncChange(path, SyncChangeKind.Created, 1)], false, false, 5)));
    }

    [Fact]
    public async Task MassChangesAndEmptyFolder_RequireApproval()
    {
        var changes = Enumerable.Range(0, 6).Select(i => new SyncChange($"file-{i}.txt", SyncChangeKind.Deleted, 0)).ToList();
        var prepared = await service.PrepareAsync(new SyncPrepareRequest("pc-04", changes, true, true, 5));

        Assert.True(prepared.Required);
        Assert.Contains("больше 5", prepared.Reason);
        Assert.Contains("стала пустой", prepared.Reason);
    }

    [Fact]
    public async Task StudentFolder_IsUnderGroupAndModule_AndPullsExistingSave()
    {
        var classroom = new ClassroomService(options);
        var group = await classroom.CreateGroupAsync(new GroupDraft("Python 01", "Python", "циклы"));
        var student = await classroom.CreateStudentAsync(new StudentDraft("Иванов", "Артём", 12, group.Id, "", "", ""));
        service.BindClient("pc-student", student.Id);

        var moduleFolder = service.EnsureStudentModuleFolder("Python 01", "Иванов", "Артём", "Python");
        await File.WriteAllTextAsync(Path.Combine(moduleFolder, "save.dat"), "урок 1");

        var prepared = await service.PrepareAsync(new SyncPrepareRequest(
            "pc-student", [], false, true, 5, student.Id, []));

        Assert.False(prepared.Required);
        Assert.Contains("save.dat", prepared.DownloadPaths ?? []);
        Assert.Empty(prepared.UploadPaths ?? []);
        Assert.Equal("save.dat", (await service.ListFilesAsync("pc-student")).Single().Path);
        Assert.Contains(Path.Combine("Python 01", "Иванов Артём", "Python"), service.GetClientFolderPath("pc-student"));
        var home = await service.ResolveStudentHomeAsync(student.Id);
        Assert.Equal("Иванов Артём", home?.DisplayName);
        Assert.Equal("Python", home?.Module);
        var desktop = FileSyncService.StudentDesktopFolder(home!.DisplayName, home.Module);
        Assert.Contains("Иванов Артём", desktop);
        Assert.EndsWith("Python", desktop);
    }

    [Fact]
    public async Task LessonModuleOverride_UsesOnlyModulesFromStudentGroup()
    {
        var classroom = new ClassroomService(options);
        var homeGroup = await classroom.CreateGroupAsync(new GroupDraft("Python 01", "Python", "", "ШБ"));
        var otherGroup = await classroom.CreateGroupAsync(new GroupDraft("Дизайн 01", "Figma", "", "ШБ"));
        var student = await classroom.CreateStudentAsync(new StudentDraft("Иванов", "Артём", 12, homeGroup.Id, "", "", ""));
        service.BindClient("pc-override", student.Id);

        await using (var db = new ClassroomDbContext(options))
        {
            db.GroupProgramModules.AddRange(
                new GroupProgramModule
                {
                    GroupId = homeGroup.Id,
                    Name = "Python",
                    StartDate = DateOnly.Parse("2026-09-01"),
                    EndDate = DateOnly.Parse("2026-10-01")
                },
                new GroupProgramModule
                {
                    GroupId = homeGroup.Id,
                    Name = "Scratch",
                    StartDate = DateOnly.Parse("2026-10-02"),
                    EndDate = DateOnly.Parse("2026-11-01")
                },
                new GroupProgramModule
                {
                    GroupId = otherGroup.Id,
                    Name = "Figma",
                    StartDate = DateOnly.Parse("2026-09-01"),
                    EndDate = DateOnly.Parse("2026-10-01")
                });
            await db.SaveChangesAsync();
        }

        service.SetLessonModule(student.Id, "Figma");
        var home = await service.ResolveStudentHomeAsync(student.Id);
        Assert.Equal("Python", home?.Module);

        service.SetLessonModule(student.Id, "Scratch");
        home = await service.ResolveStudentHomeAsync(student.Id);
        Assert.Equal("Scratch", home?.Module);
        Assert.Contains(Path.Combine("Python 01", "Иванов Артём", "Scratch"), service.GetClientFolderPath("pc-override"));

        service.SetLessonModule(student.Id, null);
        home = await service.ResolveStudentHomeAsync(student.Id);
        Assert.Equal("Python", home?.Module);
    }

    [Fact]
    public async Task ConflictingSave_WaitsForTutor_UpdateKeepsStudent_RestoreKeepsTutor()
    {
        var classroom = new ClassroomService(options);
        var group = await classroom.CreateGroupAsync(new GroupDraft("Unity 01", "Unity", ""));
        var student = await classroom.CreateStudentAsync(new StudentDraft("Петрова", "Софья", 11, group.Id, "", "", ""));
        service.BindClient("pc-conflict", student.Id);

        await service.PrepareAsync(new SyncPrepareRequest("pc-conflict", [new SyncChange("progress.json", SyncChangeKind.Created, 4)], false, false, 5, student.Id));
        await Upload("pc-conflict", "progress.json", "tutor-copy");
        await service.CompleteAsync("pc-conflict");

        var studentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("student-copy"u8));
        var conflict = await service.PrepareAsync(new SyncPrepareRequest(
            "pc-conflict",
            [new SyncChange("progress.json", SyncChangeKind.Modified, 12)],
            true, false, 5, student.Id,
            [new SyncFileFingerprint("progress.json", 12, studentHash)]));

        Assert.True(conflict.Required);
        Assert.Equal(SyncApprovalStatus.Pending, conflict.Status);
        Assert.Contains("версии различаются", conflict.Reason);

        var restored = await service.DecideAsync(conflict.Id, "restore");
        Assert.Equal(SyncApprovalStatus.Restore, restored?.Status);
        Assert.Contains("progress.json", restored?.DownloadPaths ?? []);
        Assert.DoesNotContain("progress.json", restored?.UploadPaths ?? []);

        await service.CompleteAsync("pc-conflict");
        await using var tutorCopy = await service.OpenDownloadAsync("pc-conflict", "progress.json");
        using var reader = new StreamReader(tutorCopy!);
        Assert.Equal("tutor-copy", await reader.ReadToEndAsync());
    }

    private async Task PrepareSafe(string clientId, string path, SyncChangeKind kind) =>
        await service.PrepareAsync(new SyncPrepareRequest(clientId, [new SyncChange(path, kind, 10)], false, false, 5));

    private async Task<SyncedFileInfo> Upload(string clientId, string path, string text)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await service.UploadAsync(clientId, path, stream);
    }

    public async Task DisposeAsync()
    {
        await Task.Yield();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
    }
}
