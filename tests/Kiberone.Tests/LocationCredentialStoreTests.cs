using Kiberone.Infrastructure;
using System.Security.Cryptography;
using System.Text;
namespace Kiberone.Tests;
public sealed class LocationCredentialStoreTests
{
    [Fact]
    public void Scope_is_stable_but_isolated_by_server_and_location()
    {
        Assert.Equal(LocationCredentialStore.Scope("https://example.com/", " ШБ "), LocationCredentialStore.Scope("https://example.com", "шб"));
        Assert.NotEqual(LocationCredentialStore.Scope("https://example.com", "ШБ"), LocationCredentialStore.Scope("https://example.com", "Другая"));
        Assert.NotEqual(LocationCredentialStore.Scope("https://example.com", "ШБ"), LocationCredentialStore.Scope("https://other.example.com", "ШБ"));
    }
    [Fact]
    public void Windows_password_survives_restart_is_encrypted_and_can_be_replaced()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "kiberone-credential-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocationCredentialStore(root);
            const string secret = "test-пароль-!\"42";
            Assert.Null(store.Read("https://example.com", "ШБ"));
            store.Save("https://example.com", "ШБ", secret);
            Assert.Equal(secret, new LocationCredentialStore(root).Read("https://example.com", "ШБ"));
            Assert.Null(store.Read("https://example.com", "Другая"));
            Assert.Null(store.Read("https://other.example.com", "ШБ"));
            var ciphertext = File.ReadAllBytes(Assert.Single(Directory.GetFiles(root)));
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(ciphertext));
            store.Save("https://example.com", "ШБ", "replacement");
            Assert.Equal("replacement", new LocationCredentialStore(root).Read("https://example.com", "ШБ"));
            var wrongScope = Path.Combine(root, LocationCredentialStore.Scope("https://example.com", "Другая") + ".dpapi");
            File.WriteAllBytes(wrongScope, ciphertext);
            Assert.Throws<CryptographicException>(() => store.Read("https://example.com", "Другая"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
