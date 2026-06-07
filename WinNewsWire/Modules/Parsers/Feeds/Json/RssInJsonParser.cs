using System.Globalization;
using System.Text.Json;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.Parsers;

/// <summary>
/// Port of <c>RSSInJSONParser.swift</c>. Accepts the Dave Winer-style
/// <c>{ "rss": { "channel": { "item": [...] } } }</c> structure.
/// </summary>
public static class RssInJsonParser
{
    public static ParsedFeed? Parse(ParserData parserData)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(parserData.Data); }
        catch (JsonException ex) { throw new FeedParserException(FeedParserErrorKind.InvalidJson, ex.Message, ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FeedParserException(FeedParserErrorKind.InvalidJson);

            if (!root.TryGetProperty("rss", out var rssObj) || rssObj.ValueKind != JsonValueKind.Object)
                throw new FeedParserException(FeedParserErrorKind.RssChannelNotFound);
            if (!rssObj.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.Object)
                throw new FeedParserException(FeedParserErrorKind.RssChannelNotFound);

            // Items may live under any of several names — see original Swift comment.
            JsonElement items = default;
            if (!(TryArray(channel, "item", out items) || TryArray(root, "item", out items)
               || TryArray(channel, "items", out items) || TryArray(root, "items", out items)))
            {
                throw new FeedParserException(FeedParserErrorKind.RssItemsNotFound);
            }

            string? title = GetString(channel, "title");
            string? homePage = GetString(channel, "link");
            string feedUrl = parserData.Url;
            string? desc = GetString(channel, "description");
            string? language = GetString(channel, "language");

            string? iconUrl = null;
            if (channel.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object)
            {
                iconUrl = GetString(img, "url");
            }

            var parsedItems = new HashSet<ParsedItem>();
            foreach (var it in items.EnumerateArray())
            {
                var p = ParseItem(it, feedUrl);
                if (p is not null) parsedItems.Add(p);
            }

            return new ParsedFeed(FeedType.RssInJson, title, homePage, feedUrl, language, desc,
                NextUrl: null, IconUrl: iconUrl, FaviconUrl: null, Authors: null,
                Expired: false, Hubs: null, Items: parsedItems);
        }
    }

    private static ParsedItem? ParseItem(JsonElement item, string feedUrl)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var externalUrl = GetString(item, "link");
        var title = GetString(item, "title");
        string? contentHtml = GetString(item, "description");
        string? contentText = null;
        if (contentHtml is not null && !contentHtml.Contains('<'))
        {
            contentText = contentHtml;
            contentHtml = null;
        }
        if (contentHtml is null && contentText is null && title is null) return null;

        DateTime? datePublished = null;
        var pubDateStr = GetString(item, "pubDate");
        if (pubDateStr is not null) datePublished = DateParser.Parse(pubDateStr);

        IReadOnlySet<ParsedAuthor>? authors = null;
        var authorEmail = GetString(item, "author");
        if (authorEmail is not null)
            authors = new HashSet<ParsedAuthor> { new ParsedAuthor(null, null, null, authorEmail) };

        IReadOnlySet<string>? tags = null;
        if (item.TryGetProperty("category", out var catEl))
        {
            if (catEl.ValueKind == JsonValueKind.Object)
            {
                var v = GetString(catEl, "#value");
                if (v is not null) tags = new HashSet<string> { v };
            }
            else if (catEl.ValueKind == JsonValueKind.Array)
            {
                var set = new HashSet<string>();
                foreach (var c in catEl.EnumerateArray())
                {
                    var v = GetString(c, "#value");
                    if (v is not null) set.Add(v);
                }
                if (set.Count > 0) tags = set;
            }
        }

        IReadOnlySet<ParsedAttachment>? attachments = null;
        if (item.TryGetProperty("enclosure", out var enc) && enc.ValueKind == JsonValueKind.Object)
        {
            var url = GetString(enc, "url");
            if (url is not null)
            {
                long? size = null;
                if (enc.TryGetProperty("length", out var len))
                {
                    if (len.ValueKind == JsonValueKind.Number) size = len.GetInt64();
                    else if (len.ValueKind == JsonValueKind.String && long.TryParse(len.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) size = l;
                }
                var mime = GetString(enc, "type");
                var att = ParsedAttachment.Create(url, mime, null, size, null);
                if (att is not null) attachments = new HashSet<ParsedAttachment> { att };
            }
        }

        string? uniqueId = GetString(item, "guid");
        if (uniqueId is null)
        {
            // Reproduce the Swift fallback: hash a synthesis of published date, title,
            // external url, author email, first attachment url; else content.
            var sb = new System.Text.StringBuilder();
            if (datePublished is DateTime d) sb.Append(((DateTimeOffset)DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeSeconds());
            if (title is not null) sb.Append(title);
            if (externalUrl is not null) sb.Append(externalUrl);
            if (authors is not null) { var first = System.Linq.Enumerable.FirstOrDefault(authors); if (first?.EmailAddress is not null) sb.Append(first.EmailAddress); }
            if (attachments is not null) { var first = System.Linq.Enumerable.FirstOrDefault(attachments); if (first is not null) sb.Append(first.Url); }
            if (sb.Length == 0)
            {
                if (contentHtml is not null) sb.Append(contentHtml);
                if (contentText is not null) sb.Append(contentText);
            }
            uniqueId = Md5Hash.Hex(sb.ToString());
        }

        return new ParsedItem(null, uniqueId, feedUrl, null, externalUrl, title, null,
            contentHtml, contentText, null, null, null, null, datePublished, null,
            authors, tags, attachments);
    }

    private static bool TryArray(JsonElement obj, string name, out JsonElement arr)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array)
        {
            arr = el;
            return true;
        }
        arr = default;
        return false;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
