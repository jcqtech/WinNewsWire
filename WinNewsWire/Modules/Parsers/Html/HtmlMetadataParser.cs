using System.Text;
using HtmlAgilityPack;

namespace WinNewsWire.Parsers;

/// <summary>Port of <c>RSHTMLMetadataParser</c> using HtmlAgilityPack.</summary>
public static class HtmlMetadataParser
{
    public static HtmlMetadata Parse(ParserData parserData)
    {
        var html = Encoding.UTF8.GetString(parserData.Data);
        var doc = new HtmlDocument { OptionFixNestedTags = true, OptionAutoCloseOnEnd = true };
        doc.LoadHtml(html);

        Uri? pageBase = Uri.TryCreate(parserData.Url, UriKind.Absolute, out var b) ? b : null;
        string? Resolve(string? s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (pageBase is null) return s;
            if (Uri.TryCreate(s, UriKind.Absolute, out _)) return s;
            return Uri.TryCreate(pageBase, s, out var abs) ? abs.AbsoluteUri : s;
        }

        string? favicon = null;
        string? ogImage = null;
        string? twImage = null;
        var feedLinks = new List<HtmlMetadataFeedLink>();
        var appleIcons = new List<HtmlMetadataAppleTouchIcon>();

        foreach (var node in doc.DocumentNode.Descendants("link"))
        {
            var rel = node.GetAttributeValue("rel", null)?.ToLowerInvariant();
            // Some feeds use src instead of href (e.g. sixcolors shortcut icon).
            var href = node.GetAttributeValue("href", null);
            if (string.IsNullOrEmpty(href)) href = node.GetAttributeValue("src", null);
            if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(href)) continue;
            var type = node.GetAttributeValue("type", null);
            var title = node.GetAttributeValue("title", null);
            var sizes = node.GetAttributeValue("sizes", null);
            if (rel == "alternate" && (type?.Contains("rss") == true || type?.Contains("atom") == true || type?.Contains("json") == true))
                feedLinks.Add(new HtmlMetadataFeedLink(title, type, Resolve(href)!));
            else if (rel == "icon" || rel == "shortcut icon")
                favicon ??= Resolve(href);
            else if (rel.Contains("apple-touch-icon"))
                appleIcons.Add(new HtmlMetadataAppleTouchIcon(rel, sizes, Resolve(href)!));
        }

        foreach (var meta in doc.DocumentNode.Descendants("meta"))
        {
            var prop = meta.GetAttributeValue("property", null) ?? meta.GetAttributeValue("name", null);
            var content = meta.GetAttributeValue("content", null);
            if (string.IsNullOrEmpty(prop) || string.IsNullOrEmpty(content)) continue;
            if (prop.Equals("og:image", StringComparison.OrdinalIgnoreCase)) ogImage ??= Resolve(content);
            else if (prop.Equals("twitter:image", StringComparison.OrdinalIgnoreCase)
                  || prop.Equals("twitter:image:src", StringComparison.OrdinalIgnoreCase))
                twImage ??= Resolve(content);
        }

        return new HtmlMetadata
        {
            FaviconLink = favicon,
            OpenGraphImageUrl = ogImage,
            TwitterImageUrl = twImage,
            FeedLinks = feedLinks,
            AppleTouchIcons = appleIcons,
        };
    }
}
