using Kiberone.Infrastructure;
using LibGit2Sharp;
namespace Kiberone.Tests;
public sealed class ProjectGitStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kiberone-git-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "lesson.txt");
    public ProjectGitStoreTests() => Directory.CreateDirectory(root);
    [Fact] public void CaptureRestoreAndRestartPreserveHistory()
    {
        var store = new ProjectGitStore(root); File.WriteAllText(FilePath, "first");
        var first = store.Capture("first"); Assert.Equal(first.Sha, store.Capture("unchanged").Sha);
        File.WriteAllText(FilePath, "second"); var second = store.Capture("second");
        Assert.Contains("second", store.Diff(second.Sha));
        store.Restore(first.Sha); Assert.Equal("first", File.ReadAllText(FilePath));
        Assert.Contains(new ProjectGitStore(root).History(), c => c.Sha == second.Sha);
    }
    [Fact] public void BranchSwitchSavesDirtyFilesAndMergeCombinesSeparateChanges()
    {
        var store = new ProjectGitStore(root); File.WriteAllText(FilePath, "base"); store.Capture("base");
        store.CreateBranch("experiment"); store.Checkout("experiment");
        File.WriteAllText(Path.Combine(root, "new.txt"), "new");
        store.Checkout("main"); Assert.False(File.Exists(Path.Combine(root, "new.txt")));
        File.WriteAllText(FilePath, "main change"); store.Merge("experiment");
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "new.txt"))); Assert.Equal("main change", File.ReadAllText(FilePath));
        Assert.Equal(2, store.Branches().Count);
    }
    [Fact] public void ConflictLeavesWorkFilesIntact()
    {
        var store = new ProjectGitStore(root); File.WriteAllText(FilePath, "base\n"); store.Capture("base"); store.CreateBranch("other");
        store.Checkout("other"); File.WriteAllText(FilePath, "other\n"); store.Checkout("main"); File.WriteAllText(FilePath, "main\n");
        Assert.Throws<InvalidOperationException>(() => store.Merge("other")); Assert.Equal("main\n", File.ReadAllText(FilePath));
        Assert.Contains(store.History(), c => c.Message == "Перед объединением веток");
    }
    [Theory] [InlineData("../bad")] [InlineData("HEAD~1")] [InlineData("")]
    public void InvalidBranchIsRejected(string name) => Assert.Throws<ArgumentException>(() => new ProjectGitStore(root).CreateBranch(name));
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreSupportsFileDirectoryTransitions(bool originalDirectory)
    {
        var store = new ProjectGitStore(root);
        var path = Path.Combine(root, "shape");
        WriteShape(path, originalDirectory, "original");
        var original = store.Capture("original");
        DeleteShape(path);
        WriteShape(path, !originalDirectory, "dirty");

        store.Restore(original.Sha);

        AssertShape(path, originalDirectory, "original");
        var dirty = Assert.Single(store.History(), c => c.Message == "Перед восстановлением Git");
        store.Restore(dirty.Sha);
        AssertShape(path, !originalDirectory, "dirty");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckoutSupportsFileDirectoryTransitionsAndSavesDirtyShape(bool originalDirectory)
    {
        var store = new ProjectGitStore(root);
        var path = Path.Combine(root, "shape");
        WriteShape(path, originalDirectory, "main");
        store.Capture("main");
        store.CreateBranch("other");
        store.Checkout("other");
        DeleteShape(path);
        WriteShape(path, !originalDirectory, "dirty other");

        store.Checkout("main");
        AssertShape(path, originalDirectory, "main");
        store.Checkout("other");
        AssertShape(path, !originalDirectory, "dirty other");
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedApplyRollsBackTransitionsAndPreservesBranch(bool targetDirectory, bool checkout)
    {
        var store = new ProjectGitStore(root);
        var shape = Path.Combine(root, "a-shape");
        var blocked = Path.Combine(root, "z-blocked");
        WriteShape(shape, targetDirectory, "target");
        File.WriteAllText(blocked, "target file");
        var target = store.Capture("target");
        store.CreateBranch("target");
        store.Checkout("main");
        DeleteShape(shape);
        WriteShape(shape, !targetDirectory, "dirty");
        File.Delete(blocked);
        var excluded = Path.Combine(blocked, "node_modules", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(excluded)!);
        File.WriteAllText(excluded, "excluded content");
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(FilePath, "dirty lesson");

        var error = Record.Exception(() => { if (checkout) store.Checkout("target"); else store.Restore(target.Sha); });

        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        AssertShape(shape, !targetDirectory, "dirty");
        Assert.Equal("dirty lesson", File.ReadAllText(FilePath));
        Assert.Equal("excluded content", File.ReadAllText(excluded));
        Assert.True(Directory.Exists(Path.Combine(root, "empty")));
        Assert.Equal("main", File.ReadAllText(Path.Combine(root, ".history", "git-branch.txt")).Trim());
        using var repo = new LibGit2Sharp.Repository(Path.Combine(root, ".history", "git"));
        Assert.Equal("main", repo.Head.FriendlyName);
        Assert.Equal("dirty", ((LibGit2Sharp.Blob)(!targetDirectory
            ? repo.Head.Tip.Tree["a-shape/nested/content.txt"].Target
            : repo.Head.Tip.Tree["a-shape"].Target)).GetContentText());
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, ".history"), "apply-*"));
    }
    private static void WriteShape(string path, bool directory, string content)
    {
        var file = directory ? Path.Combine(path, "nested", "content.txt") : path;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }
    private static void DeleteShape(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
    }
    private static void AssertShape(string path, bool directory, string content)
    {
        Assert.Equal(directory, Directory.Exists(path));
        Assert.Equal(!directory, File.Exists(path));
        Assert.Equal(content, File.ReadAllText(directory ? Path.Combine(path, "nested", "content.txt") : path));
    }
    public void Dispose() { if (Directory.Exists(root)) { foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal); Directory.Delete(root, true); } }
}
