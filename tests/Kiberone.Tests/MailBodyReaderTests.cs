using Kiberone.Infrastructure;
using MimeKit;

namespace Kiberone.Tests;

public sealed class MailBodyReaderTests
{
    [Theory]
    [InlineData("San Francisco, CA, 94102", null)]
    [InlineData("Your code is 123456", "123456")]
    [InlineData("Код подтверждения: 654321", "654321")]
    public void Confirmation_code_requires_explicit_context(string text, string? expected)
        => Assert.Equal(expected, MailBodyReader.FindConfirmationCode(text));
    [Fact]
    public void Blank_plain_part_uses_html_and_keeps_verification_link()
    {
        using var message = new MimeMessage
        {
            Body = new MultipartAlternative
            {
                new TextPart("plain") { Text = "\n" },
                new TextPart("html") { Text = "<html><head><style>css must not appear</style></head><body><p>Verify your email address</p><p>Hello&nbsp;student!</p><a href='https://example.com/verify?token=test&amp;lang=en'>Verify email</a><a href='javascript:alert(1)'>Bad link</a><script>hidden script</script></body></html>" }
            }
        };
        var result = MailBodyReader.Read(message);
        Assert.Contains("Verify your email address", result.Text);
        Assert.Contains("Hello student!", result.Text);
        Assert.DoesNotContain("css must not appear", result.Text);
        Assert.DoesNotContain("hidden script", result.Text);
        var link = Assert.Single(result.Links);
        Assert.Equal("Verify email", link.Label);
        Assert.Equal("https://example.com/verify?token=test&lang=en", link.Url);
    }

    [Fact]
    public void Meaningful_plain_text_is_preferred_over_html()
    {
        using var message = new MimeMessage
        {
            Body = new MultipartAlternative
            {
                new TextPart("plain") { Text = "Your code: 123456" },
                new TextPart("html") { Text = "<p>Other text</p><a href='https://example.com/'>Open</a>" }
            }
        };
        var result = MailBodyReader.Read(message);
        Assert.Equal("Your code: 123456", result.Text);
        Assert.Single(result.Links);
    }
}
