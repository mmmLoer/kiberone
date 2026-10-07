using HtmlAgilityPack;
using MimeKit;
namespace Kiberone.Infrastructure;
public static class MailHtml
{
    public static string? Read(MimeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.HtmlBody)) return null;
        var document = new HtmlDocument();
        document.LoadHtml(message.HtmlBody);
        foreach (var node in document.DocumentNode.SelectNodes("//script|//iframe|//frame|//object|//embed|//form|//input|//button|//base|//meta|//link|//svg") ?? Enumerable.Empty<HtmlNode>()) node.Remove();
        foreach (var node in document.DocumentNode.Descendants().ToArray())
        {
            foreach (var attribute in node.Attributes.ToArray())
            {
                if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || attribute.Name is "srcset" or "action" or "background") node.Attributes.Remove(attribute);
                else if (attribute.Name is "href" or "src")
                {
                    var value = HtmlEntity.DeEntitize(attribute.Value).Trim();
                    if (attribute.Name == "src" && value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                    {
                        var part = message.BodyParts.OfType<MimePart>().FirstOrDefault(p => p.ContentId == value[4..]);
                        if (part?.Content is not null && part.ContentType.MimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp")
                        {
                            using var stream = new MemoryStream();
                            part.Content.DecodeTo(stream);
                            if (stream.Length <= 2 * 1024 * 1024) { attribute.Value = "data:" + part.ContentType.MimeType + ";base64," + Convert.ToBase64String(stream.ToArray()); continue; }
                        }
                    }
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0) node.Attributes.Remove(attribute);
                }
            }
        }
        return "<!doctype html><html><head><meta charset='utf-8'><meta http-equiv='Content-Security-Policy' content=\"default-src 'none'; img-src https: data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><style>body{margin:16px;overflow-wrap:anywhere}img{max-width:100%;height:auto}</style></head><body>" + document.DocumentNode.InnerHtml + "</body></html>";
    }
}
