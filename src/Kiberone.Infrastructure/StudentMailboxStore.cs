using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MimeKit;
using Kiberone.Core;

namespace Kiberone.Infrastructure;

public sealed class StudentMailboxStore
{
    private readonly string directory;
    private readonly string? mailRoot;
    private readonly object gate = new();
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    public int PurgeExpired()
    {
        if (mailRoot is null || !Directory.Exists(mailRoot)) return 0;
        var removed = 0;
        foreach (var mailbox in Directory.EnumerateDirectories(mailRoot))
        {
            if (!Regex.IsMatch(Path.GetFileName(mailbox), @"^s-[0-9a-f]{32}$") || (File.GetAttributes(mailbox) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var folder in new[] { "new", "cur" })
            {
                var path = Path.Combine(mailbox, "Maildir", folder);
                if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in Directory.EnumerateFiles(path))
                    if (File.GetLastWriteTimeUtc(file) <= DateTime.UtcNow - Retention)
                    { try { File.Delete(file); removed++; } catch (IOException) { } }
            }
        }
        return removed;
    }
    public bool Enabled => mailRoot is not null;
    public Action<string, string>? ProvisionAlias { get; set; }
    public Action<string>? ProvisionMailbox { get; set; }
    public StudentMailboxStore(string dataDirectory, string? mailRoot)
    {
        directory = Path.Combine(dataDirectory, "mail-accounts");
        this.mailRoot = string.IsNullOrWhiteSpace(mailRoot) ? null : Path.GetFullPath(mailRoot);
        if (!Enabled) return;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public StudentMailAccount GetOrCreate(Guid studentId, string? lastName = null, string? firstName = null)
    {
        if (!Enabled || (ProvisionMailbox is null && ProvisionAlias is null)) throw new InvalidOperationException("Почтовый сервис ещё не настроен на сервере.");
        lock (gate)
        {
            var path = AccountPath(studentId);
            var existing = File.Exists(path) ? JsonSerializer.Deserialize<StudentMailAccount>(File.ReadAllText(path)) : null;
            if (existing is not null && (!existing.Address.StartsWith("s-") || string.IsNullOrWhiteSpace(lastName))) return existing;
            var username = "s-" + studentId.ToString("N");
            var alias = string.IsNullOrWhiteSpace(lastName) ? username : MailboxName.Create(lastName, firstName ?? "");
            var occupied = Directory.EnumerateFiles(directory, "*.json").Select(f => JsonSerializer.Deserialize<StudentMailAccount>(File.ReadAllText(f))!).Where(a => a.StudentId != studentId).Select(a => a.Address.Split('@')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidate = alias;
            for (var suffix = 2; occupied.Contains(candidate); suffix++) candidate = alias[..Math.Min(alias.Length, 55)] + suffix;
            if (ProvisionAlias is not null)
            {
                for (var suffix = 2; ; suffix++)
                {
                    try { ProvisionAlias(username, candidate); break; }
                    catch (MailboxAliasUnavailableException) when (suffix < 10000)
                    {
                        candidate = alias[..Math.Min(alias.Length, 55)] + suffix;
                        while (occupied.Contains(candidate)) candidate = alias[..Math.Min(alias.Length, 55)] + ++suffix;
                    }
                }
            }
            else ProvisionMailbox!(username);
            var account = new StudentMailAccount(studentId, candidate + "@students.nshub.pro", existing?.Password ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(account));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
            return account;
        }
    }
    public async Task<IReadOnlyList<StudentMailMessage>> ReadAsync(Guid studentId, string password, CancellationToken ct = default)
    {
        if (!Enabled) throw new InvalidOperationException("Почтовый сервис ещё не настроен на сервере.");
        var path = AccountPath(studentId);
        if (!File.Exists(path)) throw new UnauthorizedAccessException("Почта не выдана.");
        var account = JsonSerializer.Deserialize<StudentMailAccount>(File.ReadAllText(path))!;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(account.Password)), SHA256.HashData(Encoding.UTF8.GetBytes(password))))
            throw new UnauthorizedAccessException("Нет доступа к ящику.");
        PurgeExpired();
        var root = Path.Combine(mailRoot!, "s-" + studentId.ToString("N"), "Maildir");
        var files = new[] { "new", "cur" }.SelectMany(folder => Directory.Exists(Path.Combine(root, folder))
            ? Directory.EnumerateFiles(Path.Combine(root, folder)) : []).OrderByDescending(File.GetLastWriteTimeUtc).Take(50).ToList();
        var result = new List<StudentMailMessage>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var message = await ReadMessageAsync(file, ct);
            if (message is not null) result.Add(message);
        }
        return result;
    }
    private static async Task<StudentMailMessage?> ReadMessageAsync(string file, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(file).Length > 10 * 1024 * 1024) return null;
            using var message = await MimeMessage.LoadAsync(file, ct);
            var (body, links) = MailBodyReader.Read(message);
            var code = MailBodyReader.FindConfirmationCode(body);
            return new StudentMailMessage(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(file)))),
                message.From.ToString(), message.Subject ?? "Без темы", body, message.Date,
                Path.GetFileName(Path.GetDirectoryName(file)) == "new", code, links, MailHtml.Read(message), new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) + Retention);
        }
        // Maildir delivery/retention may remove or rename a file after enumeration.
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private string AccountPath(Guid studentId) => Path.Combine(directory, studentId.ToString("N") + ".json");
}

public sealed class MailboxAliasUnavailableException : IOException { }
