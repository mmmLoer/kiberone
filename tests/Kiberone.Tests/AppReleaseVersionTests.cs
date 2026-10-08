using Kiberone.Core;

namespace Kiberone.Tests;

public sealed class AppReleaseVersionTests
{
    [Theory]
    [InlineData("2.0.0", "release")]
    [InlineData("2.0.0b", "beta")]
    public void DerivesChannel(string version, string channel) => Assert.Equal(channel, AppReleaseVersion.ChannelFor(version));

    [Theory]
    [InlineData("2.0.1", "2.0.0", true)]
    [InlineData("2.0.1b", "2.0.0b", true)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("2.0.0b", "2.0.0b", false)]
    [InlineData("2.1.0", "2.0.9", true)]
    [InlineData("3.0.0", "2.9.99", true)]
    [InlineData("2.0.0", "2.0.1", false)]
    [InlineData("9.0.0b", "2.0.0", false)]
    [InlineData("9.0.0", "2.0.0b", false)]
    [InlineData("2.0.1beta", "2.0.0", false)]
    public void OnlyNewerSameChannel(string next, string current, bool expected) => Assert.Equal(expected, AppReleaseVersion.IsNewer(next, current));

    [Theory]
    [InlineData("2.0.0-beta")]
    [InlineData("02.0.0")]
    [InlineData("2.0")]
    [InlineData("2.0.0.1")]
    [InlineData("../../2.0.0")]
    public void RejectsInvalidVersions(string value) => Assert.False(AppReleaseVersion.IsValid(value));

    [Fact]
    public void SignaturePayloadSeparatesProducts() => Assert.NotEqual(
        StudentUpdateSignature.PayloadApp("student", "2.0.0", 42, new string('a', 64)),
        StudentUpdateSignature.PayloadApp("tutor", "2.0.0", 42, new string('a', 64)));
}
