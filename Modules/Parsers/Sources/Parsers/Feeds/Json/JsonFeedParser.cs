using System.Text.Json;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.Parsers;

/// <summary>
/// Port of <c>JSONFeedParser.swift</c> — JSON Feed v1/v1.1 (https://jsonfeed.org).
/// </summary>
public static class JsonFeedParser
{
    private const string JsonFeedVersionMarker = "://jsonfeed.org/version/";

    public static ParsedFeed? Parse(ParserData parserData)
    {
        using (var doc = JsonUtilities.ParseDictionary(parserData.Data))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FeedParserException(FeedParserErrorKind.InvalidJson);

            if (!TryGetString(root, "version", out var version) || version!.IndexOf(JsonFeedVersionMarker, StringComparison.Ordinal) < 0)
                throw new FeedParserException(FeedParserErrorKind.JsonFeedVersionNotFound);

            if (!JsonUtilities.TryObjectArray(root, "items", out var itemsElem))
                throw new FeedParserException(FeedParserErrorKind.JsonFeedItemsNotFound);

            if (!TryGetString(root, "title", out var title))
                throw new FeedParserException(FeedParserErrorKind.JsonFeedTitleNotFound);

            var homePageUrl = GetString(root, "home_page_url");
            var feedUrl = GetString(root, "feed_url") ?? parserData.Url;
            var desc = GetString(root, "description");
            var nextUrl = GetString(root, "next_url");
            var iconUrl = GetString(root, "icon");
            var faviconUrl = GetString(root, "favicon");
            var expired = root.TryGetProperty("expired", out var expEl) && expEl.ValueKind == JsonValueKind.True;
            var language = GetString(root, "language");

            var authors = ParseAuthors(root);
            var hubs = ParseHubs(root);
            var items = ParseItems(itemsElem, parserData.Url);

            return new ParsedFeed(FeedType.JsonFeed, title, homePageUrl,
                feedUrl, language, desc, nextUrl, iconUrl, faviconUrl, authors, expired, hubs, items);
        }
    }

    private static IReadOnlySet<ParsedAuthor>? ParseAuthors(JsonElement obj)
    {
        if (JsonUtilities.TryObjectArray(obj, "authors", out var arr))
        {
            var set = new HashSet<ParsedAuthor>();
            foreach (var a in arr.EnumerateArray())
            {
                var pa = ParseAuthor(a);
                if (pa is not null) set.Add(pa);
            }
            return set;
        }
        if (obj.TryGetProperty("author", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            var pa = ParseAuthor(single);
            return pa is null ? null : new HashSet<ParsedAuthor> { pa };
        }
        return null;
    }

    private static ParsedAuthor? ParseAuthor(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object) return null;
        var name = GetString(a, "name");
        var url = GetString(a, "url");
        var avatar = GetString(a, "avatar");
        if (name is null && url is null && avatar is null) return null;
        return new ParsedAuthor(name, url, avatar, null);
    }

    private static IReadOnlySet<ParsedHub>? ParseHubs(JsonElement obj)
    {
        if (!JsonUtilities.TryObjectArray(obj, "hubs", out var arr)) return null;
        var set = new HashSet<ParsedHub>();
        foreach (var h in arr.EnumerateArray())
        {
            var url = GetString(h, "url");
            var type = GetString(h, "type");
            if (url is null || type is null) continue;
            set.Add(new ParsedHub(type, url));
        }
        return set.Count == 0 ? null : set;
    }

    private static IReadOnlySet<ParsedItem> ParseItems(JsonElement arr, string feedUrl)
    {
        var set = new HashSet<ParsedItem>();
        foreach (var it in arr.EnumerateArray())
        {
            var p = ParseItem(it, feedUrl);
            if (p is not null) set.Add(p);
        }
        return set;
    }

    private static ParsedItem? ParseItem(JsonElement it, string feedUrl)
    {
        if (it.ValueKind != JsonValueKind.Object) return null;

        string? uniqueId = ParseUniqueId(it);
        if (uniqueId is null) return null;

        var contentHtml = GetString(it, "content_html");
        var contentText = GetString(it, "content_text");
        if (contentHtml is null && contentText is null) return null;

        var url = GetString(it, "url");
        var externalUrl = GetString(it, "external_url");
        var title = ParseTitle(it, feedUrl);
        var language = GetString(it, "language");
        var summary = GetString(it, "summary");
        var imageUrl = GetString(it, "image");
        var bannerImageUrl = GetString(it, "banner_image");
        var datePublished = ParseDate(GetString(it, "date_published"));
        var dateModified = ParseDate(GetString(it, "date_modified"));
        var authors = ParseAuthors(it);

        IReadOnlySet<string>? tags = null;
        if (it.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array
            && tagsEl.EnumerateArray().All(tag => tag.ValueKind == JsonValueKind.String))
        {
            var h = new HashSet<string>();
            foreach (var t in tagsEl.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String) h.Add(t.GetString()!);
            }
            tags = h;
        }

        var attachments = ParseAttachments(it);

        return new ParsedItem(null, uniqueId, feedUrl, url, externalUrl, title, language,
            contentHtml, contentText, null, summary, imageUrl, bannerImageUrl,
            datePublished, dateModified, authors, tags, attachments)
        {
            LegacyUniqueId = it.GetProperty("id").ValueKind == JsonValueKind.Number
                && it.GetProperty("id").GetRawText() != uniqueId ? it.GetProperty("id").GetRawText() : null
        };
    }

    private static string? ParseTitle(JsonElement it, string feedUrl)
    {
        var t = GetString(it, "title");
        if (t is null) return null;
        if (IsSpecialCaseTitleWithEntitiesFeed(feedUrl)) return t.DecodeHtmlEntities();
        return t;
    }

    // Port of isSpecialCaseTitleWithEntitiesFeed — kottke/pxlnv/macstories/macobserver.
    private static bool IsSpecialCaseTitleWithEntitiesFeed(string feedUrl)
    {
        var lower = feedUrl.ToLowerInvariant();
        return lower.Contains("kottke.org", StringComparison.Ordinal)
            || lower.Contains("pxlnv.com", StringComparison.Ordinal)
            || lower.Contains("macstories.net", StringComparison.Ordinal)
            || lower.Contains("macobserver.com", StringComparison.Ordinal);
    }

    private static string? ParseUniqueId(JsonElement it)
    {
        if (it.TryGetProperty("id", out var idEl))
        {
            return idEl.ValueKind switch
            {
                JsonValueKind.String => idEl.GetString(),
                JsonValueKind.Number => JsonUtilities.NumberString(idEl),
                _ => null,
            };
        }
        return null;
    }

    private static DateTime? ParseDate(string? s) => string.IsNullOrEmpty(s) ? null : DateParser.Parse(s);

    private static IReadOnlySet<ParsedAttachment>? ParseAttachments(JsonElement it)
    {
        if (!JsonUtilities.TryObjectArray(it, "attachments", out var arr)) return null;
        var set = new HashSet<ParsedAttachment>();
        foreach (var a in arr.EnumerateArray())
        {
            var url = GetString(a, "url");
            var mime = GetString(a, "mime_type");
            if (url is null || mime is null) continue;
            var title = GetString(a, "title");
            var size = JsonUtilities.GetInt64(a, "size_in_bytes");
            var duration = JsonUtilities.GetInt64(a, "duration_in_seconds");
            var pa = ParsedAttachment.Create(url, mime, title, size, duration);
            if (pa is not null) set.Add(pa);
        }
        return set;
    }

    // ---- JSON helpers ------------------------------------------------

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        value = JsonUtilities.GetString(obj, name);
        return value is not null;
    }

    private static string? GetString(JsonElement obj, string name)
        => TryGetString(obj, name, out var v) ? v : null;
}
