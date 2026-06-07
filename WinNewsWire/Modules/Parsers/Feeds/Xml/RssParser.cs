using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.Parsers;

/// <summary>
/// Port of <c>RSRSSParser</c> — RSS 2.0 and RSS 1.0 (RDF). Written against
/// <see cref="XmlReader"/> in place of the Objective-C SAX parser but preserving the
/// same element semantics (see RSRSSParser.m comments for each tag handler).
/// </summary>
public static class RssParser
{
    public static ParsedFeed? Parse(ParserData parserData)
    {
        using var stream = new MemoryStream(parserData.Data);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            XmlResolver = null,
            ConformanceLevel = ConformanceLevel.Fragment,
        };
        using var reader = XmlReader.Create(stream, settings);

        string feedUrl = parserData.Url;
        string? feedTitle = null;
        string? homepageUrl = null;
        string? language = null;
        string? channelImageUrl = null;
        bool isRdf = false;

        var items = new List<ParsedItem>();
        var dateParsed = DateTime.UtcNow;

        try
        {
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                var name = reader.LocalName;
                if (name == "rss") { /* container */ continue; }
                if (name == "RDF") { isRdf = true; continue; }
                if (name == "channel") continue;

                if (name == "title" && feedTitle is null) { feedTitle = ReadInnerText(reader); continue; }
                if (name == "link" && homepageUrl is null) { homepageUrl = ReadInnerText(reader); continue; }
                if (name == "language") { language = ReadInnerText(reader); continue; }
                if (name == "description") { /* channel description — unused by ParsedFeed */ continue; }
                if (name == "image") { channelImageUrl = ReadChannelImageUrl(reader); continue; }
                if (name == "item") { var p = ReadItem(reader, feedUrl, homepageUrl); if (p is not null) items.Add(p); continue; }
            }
        }
        catch (XmlException ex)
        {
            throw new FeedParserException(FeedParserErrorKind.InvalidXml, ex.Message, ex);
        }

            return new ParsedFeed(FeedType.Rss, feedTitle, homepageUrl, feedUrl, language,
            FeedDescription: null, NextUrl: null, IconUrl: channelImageUrl, FaviconUrl: null,
            Authors: null, Expired: false, Hubs: null,
            Items: new HashSet<ParsedItem>(items));
    }

    private static string? ReadChannelImageUrl(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;
        string? url = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "image") break;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "url") url = ReadInnerText(reader);
        }
        return url;
    }

    private static ParsedItem? ReadItem(XmlReader reader, string feedUrl, string? homepageUrl)
    {
        if (reader.IsEmptyElement) return null;

        string? title = null, link = null, body = null, markdown = null;
        string? permalink = null, guid = null, author = null;
        string? imageUrl = null;
        DateTime? datePublished = null;
        var authors = new HashSet<ParsedAuthor>();
        var enclosures = new HashSet<ParsedAttachment>();
        bool guidIsPermaLink = true;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "item") break;
            if (reader.NodeType != XmlNodeType.Element) continue;

            var prefix = reader.Prefix;
            var local = reader.LocalName;
            var ns = reader.NamespaceURI;

            // Namespaced elements
            if (ns.Contains("purl.org/dc/elements") || prefix == "dc")
            {
                if (local == "creator")
                {
                    var s = ReadInnerText(reader);
                    if (!string.IsNullOrWhiteSpace(s))
                        authors.Add(ParsedAuthor.FromSingleString(s!));
                }
                else if (local == "date")
                {
                    var s = ReadInnerText(reader);
                    if (s is not null) datePublished ??= DateParser.Parse(s);
                }
                else Skip(reader);
                continue;
            }

            if (ns.Contains("purl.org/rss/1.0/modules/content") || prefix == "content")
            {
                if (local == "encoded")
                {
                    var s = ReadInnerText(reader);
                    if (!string.IsNullOrWhiteSpace(s)) body = s;
                }
                else Skip(reader);
                continue;
            }

            // Media RSS — both Yahoo (search.yahoo.com/mrss/) and RSSBoard
            // (www.rssboard.org/media-rss) variants. Provides the lead image
            // for many mainstream feeds (SCMP, NYT, etc.). Without this, the
            // article renders without its hero image.
            if (ns.Contains("mrss") || ns.Contains("media-rss") || prefix == "media")
            {
                if (local == "thumbnail" || local == "content")
                {
                    var mUrl = reader.GetAttribute("url");
                    var mType = reader.GetAttribute("type");
                    var medium = reader.GetAttribute("medium");
                    if (!string.IsNullOrEmpty(mUrl))
                    {
                        bool looksLikeImage =
                            medium == "image" ||
                            local == "thumbnail" ||
                            (mType is not null && mType.StartsWith("image", StringComparison.OrdinalIgnoreCase)) ||
                            LooksLikeImageUrl(mUrl);
                        if (looksLikeImage)
                            imageUrl ??= mUrl;
                        else
                        {
                            var att = ParsedAttachment.Create(mUrl, mType, null, null, null);
                            if (att is not null) enclosures.Add(att);
                        }
                    }
                }
                Skip(reader);
                continue;
            }

            if (prefix == "source" && local == "markdown")
            {
                markdown = ReadInnerText(reader);
                continue;
            }

            // Unprefixed (RSS 2.0) elements
            switch (local)
            {
                case "title":
                    title = ReadInnerText(reader);
                    break;
                case "link":
                    if (link is null) { var s = ReadInnerText(reader); if (!string.IsNullOrWhiteSpace(s)) link = ResolveUrl(s!, homepageUrl); }
                    break;
                case "guid":
                    {
                        string? isPerm = reader.GetAttribute("isPermaLink");
                        if (isPerm is not null && isPerm.Equals("false", StringComparison.OrdinalIgnoreCase)) guidIsPermaLink = false;
                        guid = ReadInnerText(reader);
                        if (guidIsPermaLink && guid is not null && LooksLikeUrlOrPath(guid))
                            permalink = ResolveUrl(guid, homepageUrl);
                    }
                    break;
                case "pubDate":
                    {
                        var s = ReadInnerText(reader);
                        if (s is not null) datePublished = DateParser.Parse(s);
                    }
                    break;
                case "author":
                    {
                        author = ReadInnerText(reader);
                        if (!string.IsNullOrWhiteSpace(author))
                            authors.Add(ParsedAuthor.FromSingleString(author!));
                    }
                    break;
                case "description":
                    if (body is null) body = ReadInnerText(reader);
                    break;
                case "enclosure":
                    {
                        var url = reader.GetAttribute("url");
                        var mime = reader.GetAttribute("type");
                        long? len = null;
                        var lenStr = reader.GetAttribute("length");
                        if (long.TryParse(lenStr, out var l)) len = l;
                        var att = ParsedAttachment.Create(url, mime, null, len, null);
                        if (att is not null) enclosures.Add(att);
                        // An image enclosure also serves as the item's lead image —
                        // surface it on ParsedItem.ImageUrl so the article renderer
                        // can inject it when the <description> has no <img>.
                        if (!string.IsNullOrEmpty(url) && imageUrl is null &&
                            ((mime is not null && mime.StartsWith("image", StringComparison.OrdinalIgnoreCase)) ||
                             LooksLikeImageUrl(url)))
                        {
                            imageUrl = url;
                        }
                        Skip(reader);
                    }
                    break;
                default:
                    Skip(reader);
                    break;
            }
        }

        // Swift's RSParsedFeedTransformer uses `articleID` which is guid-or-MD5.
        string uniqueId = guid ?? ComputeArticleId(permalink, link, title, body, datePublished);

        return new ParsedItem(
            syncServiceId: null,
            uniqueId: uniqueId,
            feedUrl: feedUrl,
            url: permalink,
            externalUrl: link,
            title: title,
            language: null,
            contentHtml: body,
            contentText: null,
            markdown: markdown,
            summary: null,
            imageUrl: imageUrl,
            bannerImageUrl: null,
            datePublished: datePublished,
            dateModified: null,
            authors: authors.Count == 0 ? null : authors,
            tags: null,
            attachments: enclosures.Count == 0 ? null : enclosures);
    }

    private static bool LooksLikeImageUrl(string url)
    {
        int q = url.IndexOf('?');
        var path = q < 0 ? url : url.Substring(0, q);
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadInnerText(XmlReader reader)
    {
        if (reader.IsEmptyElement) return null;
        var sb = new StringBuilder();
        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType == XmlNodeType.Text ||
                reader.NodeType == XmlNodeType.CDATA ||
                reader.NodeType == XmlNodeType.SignificantWhitespace)
            {
                sb.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.Element)
            {
                // Nested HTML inside e.g. <description> — capture as text. ReadOuterXml()
                // leaves the reader positioned on the node AFTER the inner element, so we
                // must re-check for our closing tag before letting the outer Read() advance
                // again (otherwise we skip past </description> and desynchronize the parser,
                // causing subsequent <item>s to be silently dropped).
                sb.Append(reader.ReadOuterXml());
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
                if (reader.NodeType == XmlNodeType.Text ||
                    reader.NodeType == XmlNodeType.CDATA ||
                    reader.NodeType == XmlNodeType.SignificantWhitespace)
                {
                    sb.Append(reader.Value);
                }
            }
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    private static void Skip(XmlReader reader)
    {
        if (reader.IsEmptyElement) return;
        int depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) return;
        }
    }
    private static string ComputeArticleId(string? permalink, string? link, string? title, string? body, DateTime? datePublished)
    {
        string ts = datePublished is DateTime d
            ? ((long)(d.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString()
            : string.Empty;
        string s;
        if (!string.IsNullOrEmpty(permalink) && ts.Length > 0) s = permalink + ts;
        else if (!string.IsNullOrEmpty(link) && ts.Length > 0) s = link + ts;
        else if (!string.IsNullOrEmpty(title) && ts.Length > 0) s = title + ts;
        else if (ts.Length > 0) s = ts;
        else if (!string.IsNullOrEmpty(permalink)) s = permalink;
        else if (!string.IsNullOrEmpty(link)) s = link;
        else if (!string.IsNullOrEmpty(title)) s = title;
        else s = body ?? string.Empty;
        return Md5Hash.Hex(s);
    }

    private static bool LooksLikeUrlOrPath(string s)
    {
        if (s.Contains(' ')) return false;
        if (!s.Contains('/')) return false;
        if (s.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string ResolveUrl(string s, string? homepageUrl)
    {
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return s;
        if (homepageUrl is null) return s;
        if (!Uri.TryCreate(homepageUrl, UriKind.Absolute, out var baseUri)) return s;
        if (Uri.TryCreate(baseUri, s, out var resolved)) return resolved.AbsoluteUri;
        return s;
    }
}


