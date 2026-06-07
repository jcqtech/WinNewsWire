using System.Text;
using HtmlAgilityPack;

namespace WinNewsWire.Parsers;

/// <summary>Port of <c>RSHTMLLinkParser</c> / <c>RSHTMLLink</c>.</summary>
public sealed record HtmlLink(string? Href, string? Text, string? Title);

public static class HtmlLinkParser
{
    public static IReadOnlyList<HtmlLink> ParseLinks(ParserData parserData)
    {
        var html = Encoding.UTF8.GetString(parserData.Data);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var list = new List<HtmlLink>();
        foreach (var a in doc.DocumentNode.Descendants("a"))
        {
            var href = a.GetAttributeValue("href", null);
            var title = a.GetAttributeValue("title", null);
            var text = HtmlEntity.DeEntitize(a.InnerText)?.Trim();
            if (string.IsNullOrEmpty(href) && string.IsNullOrEmpty(text)) continue;
            list.Add(new HtmlLink(href, text, title));
        }
        return list;
    }
}
