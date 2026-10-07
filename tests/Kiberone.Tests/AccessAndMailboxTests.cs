using Kiberone.Core;
using Kiberone.Infrastructure;

namespace Kiberone.Tests;

public sealed class AccessAndMailboxTests
{
    [Theory]
    [InlineData("https://Yandex.ru/games/", "yandex.ru/games")]
    [InlineData("figma.com", "figma.com")]
    public void Site_rules_preserve_domain_and_path(string input, string expected)
        => Assert.Equal(expected, SiteRule.Normalize(input));

    [Theory]
    [InlineData("file:///C:/Windows")]
    [InlineData("https://user:password@example.com")]
    public void Site_rules_reject_local_files_and_credentials(string input)
        => Assert.Throws<ArgumentException>(() => SiteRule.Normalize(input));

    [Fact]
    public async Task Group_access_rules_survive_roster_transfer()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceOptions = ClassroomDatabase.CreateOptions(Path.Combine(root, "source.db"));
            var targetOptions = ClassroomDatabase.CreateOptions(Path.Combine(root, "target.db"));
            await ClassroomDatabase.InitializeAsync(sourceOptions);
            await ClassroomDatabase.InitializeAsync(targetOptions);
            var source = new ClassroomService(sourceOptions);
            var group = await source.CreateGroupAsync(new GroupDraft("Дизайн", "Figma", "", "Тест"));
            var json = await source.SaveAccessPolicyAsync(group.Id, new ClassroomAccessPolicy("", true, true,
                ["CHROME.EXE", "chrome.exe"], [], false, ["figma.com"], ["yandex.ru/games"]));
            var saved = ClassroomService.ParseAccessPolicy(json);
            Assert.Single(saved.AllowedApps);
            Assert.NotEmpty(saved.Revision);
            var target = new ClassroomService(targetOptions);
            await target.ReplaceLocationRosterAsync(await source.ExportLocationRosterAsync("Тест"));
            var imported = Assert.Single(await target.ListGroupsAsync("Тест"));
            Assert.Equal(json, imported.AccessPolicyJson);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Mailbox_password_is_stable_and_other_password_cannot_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-mail-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        try
        {
            var provisionCount = 0;
            var store = new StudentMailboxStore(Path.Combine(root, "data"), Path.Combine(root, "mail"))
            { ProvisionMailbox = _ => provisionCount++ };
            var account = store.GetOrCreate(id);
            Assert.Equal(account, store.GetOrCreate(id));
            Assert.Equal(1, provisionCount);
            Assert.True(account.Password.Length >= 24);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadAsync(id, "wrong"));
            var mailbox = Path.Combine(root, "mail", "s-" + id.ToString("N"), "Maildir", "new");
            Directory.CreateDirectory(mailbox);
            await File.WriteAllTextAsync(Path.Combine(mailbox, "message"),
                "From: Figma <hello@figma.com>\r\nTo: " + account.Address + "\r\nSubject: Verify account\r\nDate: Wed, 07 Oct 2026 12:00:00 +0000\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nYour code is 123456\r\n");
            var message = Assert.Single(await store.ReadAsync(id, account.Password));
            Assert.Equal("123456", message.ConfirmationCode);
            Assert.True(message.IsNew);
            Assert.Contains("Your code", message.Body);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Expired_messages_are_deleted_but_credentials_and_fresh_mail_survive()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StudentMailboxStore(Path.Combine(root, "data"), Path.Combine(root, "mail")) { ProvisionMailbox = _ => { } };
            var id = Guid.NewGuid();
            var account = store.GetOrCreate(id);
            var folder = Path.Combine(root, "mail", "s-" + id.ToString("N"), "Maildir", "new");
            Directory.CreateDirectory(folder);
            var old = Path.Combine(folder, "old");
            var fresh = Path.Combine(folder, "fresh");
            await File.WriteAllTextAsync(old, "old");
            await File.WriteAllTextAsync(fresh, "fresh");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-25));
            Assert.Equal(1, store.PurgeExpired());
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
            Assert.Equal(account, store.GetOrCreate(id));
            Assert.Equal(0, store.PurgeExpired());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Named_addresses_are_unique_and_migration_preserves_password()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            var aliases = new List<string>();
            var store = new StudentMailboxStore(Path.Combine(root, "data"), Path.Combine(root, "mail"))
            { ProvisionMailbox = _ => { }, ProvisionAlias = (_, alias) => aliases.Add(alias) };
            var id = Guid.NewGuid();
            var old = store.GetOrCreate(id);
            var first = store.GetOrCreate(id, "Иванов", "Иван");
            var second = store.GetOrCreate(Guid.NewGuid(), "Иванов", "Иван");
            Assert.Equal("ivanovivan@students.nshub.pro", first.Address);
            Assert.Equal("ivanovivan2@students.nshub.pro", second.Address);
            Assert.Equal(old.Password, first.Password);
            Assert.Equal(first, store.GetOrCreate(id, "Иванов", "Иван"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Rich_mail_preserves_images_and_styles_but_removes_active_content()
    {
        using var message = new MimeKit.MimeMessage();
        message.Body = new MimeKit.TextPart("html") { Text = "<style>p{color:red}</style><p onclick='bad()'>Hello</p><img src='https://example.com/logo.png'><script>bad()</script><iframe src='https://example.com'></iframe><a href='javascript:bad()'>Bad</a>" };
        var html = MailHtml.Read(message)!;
        Assert.Contains("color:red", html);
        Assert.Contains("https://example.com/logo.png", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("Content-Security-Policy", html);
    }

}
