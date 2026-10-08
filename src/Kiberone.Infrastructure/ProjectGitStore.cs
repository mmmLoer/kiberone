using System.Collections.Concurrent;
using LibGit2Sharp;
using Kiberone.Core;
namespace Kiberone.Infrastructure;
public sealed class ProjectGitStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { ".history", ".git", ".venv", "node_modules", "__pycache__", "$RECYCLE.BIN" };
    private readonly string root;
    private string RepositoryPath => Path.Combine(root, ".history", "git");
    private string BranchFile => Path.Combine(root, ".history", "git-branch.txt");
    private string CurrentBranch => File.Exists(BranchFile) ? File.ReadAllText(BranchFile).Trim() : "main";
    private object Gate => Gates.GetOrAdd(root, _ => new object());
    public ProjectGitStore(string root) { this.root = Path.GetFullPath(root); Directory.CreateDirectory(this.root); }
    private Repository Open()
    { if (!Repository.IsValid(RepositoryPath)) { Directory.CreateDirectory(Path.GetDirectoryName(RepositoryPath)!); Repository.Init(RepositoryPath, true); } return new Repository(RepositoryPath); }
    public ProjectCommitInfo Capture(string message)
    {
        lock (Gate)
        {
            using var repo = Open(); var branch = CurrentBranch; var parent = repo.Branches[branch]?.Tip;
            var definition = new TreeDefinition();
            foreach (var file in Files())
            { using var input = File.OpenRead(file); definition.Add(Path.GetRelativePath(root, file).Replace('\\','/'), repo.ObjectDatabase.CreateBlob(input), Mode.NonExecutableFile); }
            var tree = repo.ObjectDatabase.CreateTree(definition);
            if (parent?.Tree.Id == tree.Id) return Map(parent, branch);
            var signature = new Signature("KIBERone", "classroom@localhost", DateTimeOffset.UtcNow);
            var commit = repo.ObjectDatabase.CreateCommit(signature, signature, message, tree, parent is null ? [] : [parent], false);
            repo.Refs.Add("refs/heads/" + branch, commit.Id, true);
            repo.Refs.UpdateTarget("HEAD", "refs/heads/" + branch);
            return Map(commit, branch);
        }
    }
    public IReadOnlyList<ProjectCommitInfo> History()
    { lock (Gate) { using var repo = Open(); var branch = CurrentBranch; var tip = repo.Branches[branch]?.Tip; return tip is null ? [] : repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = tip }).Take(100).Select(c => Map(c, branch)).ToArray(); } }
    public IReadOnlyList<string> Branches()
    { lock (Gate) { using var repo = Open(); return repo.Branches.Select(b => b.FriendlyName).OrderBy(b => b).ToArray(); } }
    public string Diff(string sha)
    {
        lock (Gate)
        {
            using var repo = Open(); var commit = Lookup(repo, sha);
            var patch = repo.Diff.Compare<Patch>(commit.Parents.FirstOrDefault()?.Tree, commit.Tree).Content;
            return patch.Length > 200000 ? patch[..200000] + "\n…" : patch;
        }
    }
    public void CreateBranch(string name)
    {
        ValidateBranch(name); lock (Gate) { Capture("Перед созданием ветки"); using var repo = Open(); if (repo.Branches[name] is not null) throw new InvalidOperationException("Ветка уже существует."); repo.CreateBranch(name, repo.Branches[CurrentBranch].Tip); }
    }
    public void Checkout(string name)
    {
        ValidateBranch(name); lock (Gate) { Capture("Перед переключением ветки"); using var repo = Open(); var branch = repo.Branches[name] ?? throw new KeyNotFoundException("Ветка не найдена."); Apply(branch.Tip.Tree); File.WriteAllText(BranchFile, name); repo.Refs.UpdateTarget("HEAD", "refs/heads/" + name); }
    }
    public void Restore(string sha)
    { lock (Gate) { Capture("Перед восстановлением Git"); using var repo = Open(); var commit = Lookup(repo, sha); Apply(commit.Tree); Capture("Восстановлено: " + sha[..8]); } }
    public void Merge(string branch)
    {
        ValidateBranch(branch); lock (Gate)
        {
            Capture("Перед объединением веток"); using var repo = Open(); var name = CurrentBranch;
            var ours = repo.Branches[name].Tip; var theirs = repo.Branches[branch]?.Tip ?? throw new KeyNotFoundException("Ветка не найдена.");
            if (ours.Id == theirs.Id) return;
            var merged = repo.ObjectDatabase.MergeCommits(ours, theirs, new MergeTreeOptions());
            var tree = merged.Tree;
            if (merged.Conflicts.Any() || tree is null) throw new InvalidOperationException("Есть конфликт изменений. Файлы сохранены. Сравните версии и внесите исправления перед объединением.");
            var signature = new Signature("KIBERone", "classroom@localhost", DateTimeOffset.UtcNow);
            var commit = repo.ObjectDatabase.CreateCommit(signature, signature, "Объединение: " + branch, tree, [ours, theirs], false);
            Apply(tree); repo.Refs.Add("refs/heads/" + name, commit.Id, true);
        }
    }
    private IEnumerable<string> Files()
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory)) if (!Excluded.Contains(Path.GetFileName(child)) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(directory)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && Path.GetFileName(file) is not "desktop.ini" and not "Thumbs.db" and not ".DS_Store") yield return file;
        }
    }
    private void Apply(Tree tree)
    {
        var entries = new List<(string Path, Blob Blob)>();
        void Read(Tree current, string prefix)
        {
            foreach (var entry in current)
            {
                var path = prefix + entry.Name;
                if (entry.Target is Tree sub) Read(sub, path + "/");
                else if (entry.Target is Blob blob) entries.Add((path, blob));
                else throw new IOException("Неподдерживаемый объект проекта.");
            }
        }
        Read(tree, ""); var wanted = entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.Path));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || entry.Path.Split('/').Any(Excluded.Contains)) throw new IOException("Некорректный путь проекта.");
            var ancestor = Path.GetDirectoryName(path);
            while (ancestor is not null && ancestor.Length >= root.Length) { if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки в проекте не поддерживаются."); ancestor = Path.GetDirectoryName(ancestor); }
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки в проекте не поддерживаются.");
        }
        var staging = Path.Combine(root, ".history", "apply-" + Guid.NewGuid().ToString("N"));
        var incoming = Path.Combine(staging, "incoming"); var backup = Path.Combine(staging, "backup");
        var previous = Files().ToArray();
        var started = false;
        var preserveBackup = false;
        var previousDirectories = ManagedDirectories(root).ToArray();
        try
        {
            foreach (var file in previous)
            {
                var saved = Path.Combine(backup, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(file, saved);
            }
            foreach (var entry in entries)
            {
                var staged = Path.Combine(incoming, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                using var input = entry.Blob.GetContentStream(); using var output = File.Create(staged); input.CopyTo(output);
            }
            started = true;
            // Remove obsolete files before creating parents or replacing directories with files.
            foreach (var file in previous) if (!wanted.Contains(Path.GetRelativePath(root, file).Replace('\\','/'))) File.Delete(file);
            RemoveEmptyDirectories(root);
            foreach (var entry in entries)
            {
                var path = Path.Combine(root, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Move(Path.Combine(incoming, entry.Path), path, true);
            }
        }
        catch (Exception applyError)
        {
            if (started)
            {
                try
                {
                    foreach (var file in Files().ToArray()) if (!previous.Contains(file, StringComparer.OrdinalIgnoreCase)) File.Delete(file);
                    RemoveEmptyDirectories(root);
                    foreach (var directory in previousDirectories) Directory.CreateDirectory(directory);
                    foreach (var file in previous)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        File.Copy(Path.Combine(backup, Path.GetRelativePath(root, file)), file, true);
                    }
                }
                catch (Exception rollbackError)
                {
                    preserveBackup = true;
                    throw new AggregateException("Восстановление не завершено. Резервная копия: " + backup, applyError, rollbackError);
                }
            }
            throw;
        }
        finally { if (!preserveBackup && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static IEnumerable<string> ManagedDirectories(string directory)
    {
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (Excluded.Contains(Path.GetFileName(child)) || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            yield return child;
            foreach (var descendant in ManagedDirectories(child)) yield return descendant;
        }
    }
    private static void RemoveEmptyDirectories(string directory)
    {
        foreach (var child in ManagedDirectories(directory).OrderByDescending(p => p.Length).ToArray())
            if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
    }
    private static Commit Lookup(Repository repo, string sha)
    { if (sha.Length != 40 || sha.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Некорректный ID версии."); return repo.Lookup<Commit>(sha) ?? throw new KeyNotFoundException("Версия не найдена."); }
    private static void ValidateBranch(string name)
    { if (name.Length is < 1 or > 80 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Название ветки: латинские буквы, цифры, дефис или подчёркивание."); }
    private static ProjectCommitInfo Map(Commit c, string branch) => new(c.Sha, c.MessageShort, c.Author.When, branch);
}
