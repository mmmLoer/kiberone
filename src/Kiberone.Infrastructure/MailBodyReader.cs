using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Kiberone.Core;
using MimeKit;

namespace Kiberone.Infrastructure;

public static class MailBodyReader
{
    public static (string Text, IReadOnlyList<StudentMailLink> Links) Read(MimeMessage message)
    {
        var html = new HtmlDocument();
        html.LoadHtml(message.HtmlBody ?? "");
        foreach (var node in html.DocumentNode.Descendants().Where(n => n.Name is "script" or "style" or "head"
            || n.Attributes.Contains("hidden") || n.GetAttributeValue("style", "").Replace(" ", "").Contains("display:none", StringComparison.OrdinalIgnoreCase)).ToArray())
            node.Remove();
        var links = new List<StudentMailLink>();
        foreach (var anchor in html.DocumentNode.Descendants("a"))
        {
            var href = HtmlEntity.DeEntitize(anchor.GetAttributeValue("href", ""));
            if (href.Length > 8192 || !Uri.TryCreate(href, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) continue;
            if (links.Any(x => x.Url == uri.AbsoluteUri)) continue;
            var label = Clean(HtmlEntity.DeEntitize(anchor.InnerText));
            if (string.IsNullOrWhiteSpace(label)) continue;
            if (label.Length > 160) label = label[..160];
            links.Add(new StudentMailLink(string.IsNullOrWhiteSpace(label) ? "Открыть ссылку" : label, uri.AbsoluteUri));
            if (links.Count == 30) break;
        }
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text))
        {
            var builder = new StringBuilder();
            AppendText(html.DocumentNode, builder, 0);
            text = builder.ToString();
        }
        text = Clean(text ?? "");
        if (text.Length > 100000) text = text[..100000];
        if (string.IsNullOrWhiteSpace(text)) text = "В письме нет текстового содержимого.";
        return (text, links);
    }

    private static void AppendText(HtmlNode node, StringBuilder output, int depth)
    {
        if (depth > 100 || output.Length > 200000 || node.NodeType == HtmlNodeType.Comment) return;
        if (node is HtmlTextNode text) { output.Append(HtmlEntity.DeEntitize(text.Text)); return; }
        var block = node.Name is "p" or "div" or "tr" or "li" or "br" or "h1" or "h2" or "h3";
        if (block) output.AppendLine();
        foreach (var child in node.ChildNodes) AppendText(child, output, depth + 1);
        if (node.Name is "td" or "th") output.Append(' ');
        if (block) output.AppendLine();
    }

    private static string Clean(string text)
    {
        text = Regex.Replace(text.Replace("\r\n", "\n").Replace('\u00a0', ' '), @"[^\S\n]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, @" *\n *", "\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Regex.Replace(text, @"\n{3,}", "\n\n", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    }

    public static string? FindConfirmationCode(string text)
    {
        var match = Regex.Match(text, @"(?:\b(?:code|otp|pin)\b|\bкод(?:\s+(?:подтверждения|проверки|доступа))?)\s*(?:is\s*)?(?:[:=—-]\s*)?(?<code>\d{4,8})(?!\d)",
            RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Groups["code"].Value : null;
    }
}
