using Kiberone.Infrastructure;
namespace Kiberone.Tests;
public sealed class HubTransportAuditTests
{
    [Fact]
    public void DisabledMail_DoesNotTouchMailboxDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiberone-disabled-mail-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root, "mail-accounts"), "owned by separate service");
            var store = new StudentMailboxStore(root, null);
            Assert.False(store.Enabled);
            Assert.Throws<InvalidOperationException>(() => store.GetOrCreate(Guid.NewGuid()));
        } finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(null, "https://nshub.pro/")]
    [InlineData("http://193.182.145.64:8787", "https://nshub.pro/")]
    [InlineData("http://127.0.0.1:8787", "http://127.0.0.1:8787/")]
    [InlineData("https://example.com/hub", "https://example.com/hub/")]
    public void KnownProductionHub_UsesHttps_WithoutChangingCustomServers(string? input, string expected)
        => Assert.Equal(expected, ClassroomHubClient.ResolveBaseAddress(input).AbsoluteUri);
}
