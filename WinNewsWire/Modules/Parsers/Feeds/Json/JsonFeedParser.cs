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
        JsonDocument doc;
        try { doc = JsonDocument.Parse(parserData.Data); }
        catch (JsonException ex) { throw new FeedParserException(FeedParserErrorKind.InvalidJson, ex.Message, ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FeedParserException(FeedParserErrorKind.InvalidJson);

            if (!TryGetString(root, "version", out var version) || version!.IndexOf(JsonFeedVersionMarker, StringComparison.Ordinal) < 0)
                throw new FeedParserException(FeedParserErrorKind.JsonFeedVersionNotFound);

            if (!root.TryGetProperty("items", out var itemsElem) || itemsElem.ValueKind != JsonValueKind.Array)
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

            return new ParsedFeed(FeedType.JsonFeed, title, homePageUrl?.NilIfEmptyOrWhitespace(),
                feedUrl, language, desc, nextUrl, iconUrl, faviconUrl, authors, expired, hubs, items);
        }
    }

    private static IReadOnlySet<ParsedAuthor>? ParseAuthors(JsonElement obj)
    {
        if (obj.TryGetProperty("authors", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var set = new HashSet<ParsedAuthor>();
            foreach (var a in arr.EnumerateArray())
            {
                var pa = ParseAuthor(a);
                if (pa is not null) set.Add(pa);
            }
            return set.Count == 0 ? null : set;
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
        if (!obj.TryGetProperty("hubs", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
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
        if (it.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            var h = new HashSet<string>();
            foreach (var t in tagsEl.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String) h.Add(t.GetString()!);
            }
            tags = h.Count == 0 ? null : h;
        }

        var attachments = ParseAttachments(it);

        return new ParsedItem(null, uniqueId, feedUrl, url, externalUrl, title, language,
            contentHtml, contentText, null, summary, imageUrl, bannerImageUrl,
            datePublished, dateModified, authors, tags, attachments);
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
                JsonValueKind.Number => idEl.GetRawText(),
                _ => null,
            };
        }
        return null;
    }

    private static DateTime? ParseDate(string? s) => string.IsNullOrEmpty(s) ? null : DateParser.Parse(s);

    private static IReadOnlySet<ParsedAttachment>? ParseAttachments(JsonElement it)
    {
        if (!it.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var set = new HashSet<ParsedAttachment>();
        foreach (var a in arr.EnumerateArray())
        {
            var url = GetString(a, "url");
            var mime = GetString(a, "mime_type");
            if (url is null || mime is null) continue;
            var title = GetString(a, "title");
            long? size = a.TryGetProperty("size_in_bytes", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt64() : null;
            int? dur = a.TryGetProperty("duration_in_seconds", out var de) && de.ValueKind == JsonValueKind.Number ? de.GetInt32() : null;
            var pa = ParsedAttachment.Create(url, mime, title, size, dur);
            if (pa is not null) set.Add(pa);
        }
        return set.Count == 0 ? null : set;
    }

    // ---- JSON helpers ------------------------------------------------

    private static bool TryGetString(JsonElement obj, string name, out string? value)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString();
            return value is not null;
        }
        value = null;
        return false;
    }

    private static string? GetString(JsonElement obj, string name)
        => TryGetString(obj, name, out var v) ? v : null;
}
