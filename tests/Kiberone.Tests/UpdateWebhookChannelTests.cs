using Kiberone.Infrastructure;
namespace Kiberone.Tests;
public sealed class UpdateWebhookChannelTests
{
    [Theory]
    [InlineData("refs/heads/main", "release.service")]
    [InlineData("refs/heads/beta", "beta.service")]
    [InlineData("refs/heads/feature", null)]
    [InlineData("beta", null)]
    public void RoutesOnlyConfiguredBranches(string branch, string? expected) => Assert.Equal(expected,
        GithubPushReleaseHook.SelectReleaseUnit(branch, "main", "release.service", "beta.service"));
    [Fact]
    public void BetaDisabledUnlessConfigured() => Assert.Null(
        GithubPushReleaseHook.SelectReleaseUnit("refs/heads/beta", "main", "release.service", null));
}
