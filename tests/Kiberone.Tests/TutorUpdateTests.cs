using System.Security.Cryptography;
using System.Text;
using Kiberone.Core;
using Kiberone.Infrastructure;
using Kiberone.Tutor.ViewModels;

namespace Kiberone.Tests;

public sealed class TutorUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kiberone-tutor-update-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("student-binary");
    private static readonly AppUpdateManifest SignedStudent = new("9.1.0", "KIBERoneStudent.exe", Bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(Bytes)), DateTimeOffset.UtcNow,
        "Gh2/yvyfllQPb+uw3Vs6kYE7l3VYaQIEStWY7JebgE6gQvF+CpoQ38GPZxhWiTe7gVVbI2JJCrgZ96+Ibq7wgjTGHhXqYEC7jgUx5TEaevb7aV0pBvECNWwgSQ1MsHw9DMImp1zYYadEWrKVtn9sMqInx2A486VHKEW46BL46KCIKAIuyRkGAZ1AJe1iZ6jANAJcXWE7XmogQm76lxH+t/re+C8bvC90rt/bU9J0sUjQIk3ZBAJP60l+XTXqfmXsncdNHmBxBqc93nWNy1JINodcRVWHaqwiP6Z9TTiR2+qVtuLRKw/3jkQ1ABe7y+iefq0hr82d4xRmkL4acBZDLFlc9BA9uLjAR65nIjITCA+AZOhJM2MqlQCXL0o700Nc2FFiLGimtWSOeMUvwMpSC0fXybUV1nRfV/8McyHjfQJ7D+v94Qw15zEzc4Zv+1ztaMw1WaTxBfgfw+TQir/qOxyM5qIBsCXVIY1J98fTY01oXi7kel5oQEEhsS8pRbER");

    [Fact]
    public async Task WritableTargetUsesSharedValidationAndPreservesBackup()
    {
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "KIBERoneStudent.exe");
        await File.WriteAllTextAsync(target, "old package");
        Assert.True(AppUpdateInstaller.CanWriteTargetDirectory(target));
        Assert.DoesNotContain("администратора", AppUpdateInstaller.TutorInstallStatus(target));
        var package = await AppUpdateInstaller.StageAsync("student", SignedStudent, Bytes, "9.0.0", root);
        await AppUpdateInstaller.ApplyVerifiedPackageAsync("student", SignedStudent, "9.0.0", package, target);
        Assert.Equal(Bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal("old package", await File.ReadAllTextAsync(target + ".previous"));
    }

    [Fact]
    public async Task WrongProductChannelTamperingAndCancellationCannotReplaceTarget()
    {
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "KIBERoneStudent.exe");
        await File.WriteAllTextAsync(target, "original");
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateInstaller.StageAsync("tutor", SignedStudent, Bytes, "9.0.0", root));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateInstaller.StageAsync("student", SignedStudent, Bytes, "9.0.0b", root));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateInstaller.StageAsync("student", SignedStudent, Bytes, "9.1.0", root));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateInstaller.StageAsync("student", SignedStudent, [1, 2, 3], "9.0.0", root));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppUpdateInstaller.StageAsync("student", SignedStudent, Bytes, "9.0.0", root, cancel.Token));
        var package = await AppUpdateInstaller.StageAsync("student", SignedStudent, Bytes, "9.0.0", root);
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateInstaller.ApplyVerifiedPackageAsync("student", SignedStudent, "9.0.0", package, Path.Combine(root, "KIBERoneTutor.exe")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppUpdateInstaller.ApplyVerifiedPackageAsync("student", SignedStudent, "9.0.0", package, target, cancel.Token));
        await File.WriteAllBytesAsync(package, Encoding.UTF8.GetBytes("tampered-bytes"));
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateInstaller.ApplyVerifiedPackageAsync("student", SignedStudent, "9.0.0", package, target));
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.False(File.Exists(target + ".previous"));
        Assert.Empty(Directory.GetFiles(root, "*.update-*"));
    }

    [Fact]
    public async Task ApplyFailureKeepsOriginalRunnableFile()
    {
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "KIBERoneStudent.exe");
        await File.WriteAllTextAsync(target, "original");
        var package = await AppUpdateInstaller.StageAsync("student", SignedStudent, Bytes, "9.0.0", root);
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => AppUpdateInstaller.ApplyVerifiedPackageAsync("student", SignedStudent, "9.0.0", package, target));
        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(root, "*.update-*"));
    }

    [Fact]
    public void OnlyUnwritableTargetsShowAdministratorConsentMessage()
    {
        var target = Path.Combine(root, "missing", "Kiberone.Tutor.exe");
        Assert.False(AppUpdateInstaller.CanWriteTargetDirectory(target));
        Assert.Contains("администратора", AppUpdateInstaller.TutorInstallStatus(target));
    }
    [Fact]
    public void SingleFileDetectionUsesRunningAssemblyRatherThanLeftoverDlls()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Kiberone.Tutor.dll"), "leftover old installer file");
        // Unit tests run from separate assemblies. The published child probe exercises the bundle.
        Assert.False(AppUpdateInstaller.IsRunningSingleFileBundle());
    }

    [Theory]
    [InlineData("Kiberone.Tutor.exe", true)]
    [InlineData("KIBERoneTutor.exe", true)]
    [InlineData("KIBERoneStudent.exe", false)]
    [InlineData("dotnet.exe", false)]
    public void TutorExecutableNames(string name, bool expected) => Assert.Equal(expected, AppUpdateInstaller.IsTutorExecutable(name));

    [Fact]
    public async Task TutorCheckUsesOwnBuildChannelAndDoesNotDownloadOrInstallAutomatically()
    {
        var model = await Model();
        var requests = new List<(string App, string Channel)>();
        model.UseTestStudentUpdates = BuildInfo.Channel != "beta";
        model.UpdateManifestProvider = (app, channel, _) =>
        { requests.Add((app, channel)); return Task.FromResult<AppUpdateManifest?>(null); };
        model.UpdatePackageProvider = (_, _, _) => throw new Exception("Unexpected download");
        model.TutorUpdateRestartRequested = () => throw new Exception("Unexpected shutdown");
        await model.CheckTutorUpdateCommand.ExecuteAsync(null);
        Assert.Equal([("tutor", BuildInfo.Channel)], requests);
        Assert.False(model.CanInstallTutorUpdate);
        Assert.False(model.InstallTutorUpdateCommand.CanExecute(null));
        Assert.False(model.IsTutorUpdateBusy);
    }

    [Fact]
    public async Task StudentRelayAlwaysChecksBothChannelsAndContinuesAfterOneFails()
    {
        var model = await Model();
        var requests = new List<(string App, string Channel)>();
        model.UseTestStudentUpdates = true;
        model.UpdateManifestProvider = (app, channel, _) =>
        {
            requests.Add((app, channel));
            if (channel == "release") throw new HttpRequestException("release unavailable");
            return Task.FromResult<AppUpdateManifest?>(null);
        };
        await model.DownloadStudentUpdateFromHubCommand.ExecuteAsync(null);
        Assert.Equal([("student", "release"), ("student", "beta")], requests);
        Assert.Contains("release unavailable", model.StudentUpdateStatus);
        Assert.Contains("beta:", model.StudentUpdateStatus);
    }

    private async Task<MainViewModel> Model()
    {
        Directory.CreateDirectory(root);
        var options = ClassroomDatabase.CreateOptions(Path.Combine(root, "classroom.db"));
        await ClassroomDatabase.InitializeAsync(options);
        var classroom = new ClassroomService(options);
        var registry = new ClientRegistry();
        var commands = new ReliableCommandQueue(registry);
        return new MainViewModel(new TypingLessonService(options), classroom, new FileSyncService(options, Path.Combine(root, "sync")),
            new AssetDistributionService(root, root), registry, commands, new QuizService(options, registry, commands),
            new AuditService(options), Path.Combine(root, "quizzes"), Path.Combine(root, "settings"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
