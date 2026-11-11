using System.Text.Json.Serialization;
using WinNewsWire.Parsers;

namespace WinNewsWire.Feedly;

// Port of Modules/Account/Sources/Account/Feedly/FeedlyModel.swift + related service types.
// Feedly's JSON uses camelCase; we rely on JsonPropertyName attributes because Json.Net source-gen
// + camel-case policy would conflict with the snake_case used elsewhere.

public sealed record FeedlyCategory(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("id")] string Id);

public sealed record FeedlyTag(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string? Label);

public sealed record FeedlyLink(
    [property: JsonPropertyName("href")] string Href,
    [property: JsonPropertyName("type")] string? Type);

public sealed record FeedlyOrigin(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("streamId")] string? StreamId,
    [property: JsonPropertyName("htmlUrl")] string? HtmlUrl);

public sealed record FeedlyFeed(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("updated")] long? Updated,
    [property: JsonPropertyName("website")] string? Website);

public sealed record FeedlyCollection(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("feeds")] List<FeedlyFeed> Feeds);

public sealed record FeedlyEntryContent(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("direction")] string? Direction);

public sealed record FeedlyEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("content")] FeedlyEntryContent? Content,
    [property: JsonPropertyName("summary")] FeedlyEntryContent? Summary,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("crawled")] long Crawled,
    [property: JsonPropertyName("recrawled")] long? Recrawled,
    [property: JsonPropertyName("origin")] FeedlyOrigin? Origin,
    [property: JsonPropertyName("canonical")] List<FeedlyLink>? Canonical,
    [property: JsonPropertyName("alternate")] List<FeedlyLink>? Alternate,
    [property: JsonPropertyName("unread")] bool Unread,
    [property: JsonPropertyName("tags")] List<FeedlyTag>? Tags,
    [property: JsonPropertyName("categories")] List<FeedlyCategory>? Categories,
    [property: JsonPropertyName("enclosure")] List<FeedlyLink>? Enclosure,
    [property: JsonPropertyName("published")] long? Published = null)
{
    public string? ExternalUrl
    {
        get
        {
            IEnumerable<FeedlyLink> flat = Enumerable.Empty<FeedlyLink>();
            if (Canonical is not null) flat = flat.Concat(Canonical);
            if (Alternate is not null) flat = flat.Concat(Alternate);
            return flat.FirstOrDefault(l => l.Type is null || l.Type == "text/html")?.Href;
        }
    }

    public DateTime DatePublished => DateTimeOffset.FromUnixTimeMilliseconds(Published is { } published && published <= Crawled ? published : Crawled).UtcDateTime;
    public DateTime? DateModified => Recrawled is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(Recrawled.Value).UtcDateTime;
    public string? ContentHtml => Content?.Content ?? Summary?.Content;
}

public static class FeedlyRTLTextSanitizer
{
    private const string Prefix = "<div style=\"direction:rtl;text-align:right\">";
    private const string Suffix = "</div>";

    public static string? Sanitize(string? text)
        => text is not null && text.StartsWith(Prefix, StringComparison.Ordinal) && text.EndsWith(Suffix, StringComparison.Ordinal)
            ? text[Prefix.Length..^Suffix.Length] : text;
}

public sealed class FeedlyCollectionParser(FeedlyCollection collection)
{
    public string FolderName => FeedlyRTLTextSanitizer.Sanitize(collection.Label) ?? "";
    public string ExternalID => collection.Id;
}

public sealed class FeedlyFeedParser(FeedlyFeed feed)
{
    public string? Title => FeedlyRTLTextSanitizer.Sanitize(feed.Title);
    public string FeedID => feed.Id;
    public string Url => FeedlyResourceIds.UrlFromFeedId(feed.Id);
    public string? HomePageUrl => feed.Website;
}

public sealed class FeedlyEntryParser(FeedlyEntry entry)
{
    public string ID => entry.Id;
    public string? FeedUrl => entry.Origin?.StreamId;
    public string? ExternalUrl => entry.ExternalUrl;
    public string? Title => FeedlyRTLTextSanitizer.Sanitize(entry.Title);
    public string? ContentHtml => entry.ContentHtml;
    public string? Summary => FeedlyRTLTextSanitizer.Sanitize(entry.Summary?.Content);
    public DateTime DatePublished => entry.DatePublished;
    public DateTime? DateModified => entry.DateModified;
    public IReadOnlySet<ParsedAuthor>? Authors => entry.Author is { } name
        ? new HashSet<ParsedAuthor> { new(name, null, null, null) } : null;
    public IReadOnlySet<string>? Tags => entry.Tags?.Where(t => t.Label is not null).Select(t => t.Label!).ToHashSet() is { Count: > 0 } tags ? tags : null;
    public IReadOnlySet<ParsedAttachment>? Attachments => entry.Enclosure?
        .Select(link => ParsedAttachment.Create(link.Href, link.Type, null, null, null))
        .OfType<ParsedAttachment>().ToHashSet() is { Count: > 0 } attachments ? attachments : null;

    public ParsedItem? ParsedItemRepresentation => FeedUrl is { } feedUrl
        ? new(ID, ID, feedUrl, null, ExternalUrl, Title, null, ContentHtml, null, null, Summary, null, null,
            DatePublished, DateModified, Authors, Tags, Attachments) : null;
}

public sealed record FeedlyStream(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("updated")] long? Updated,
    [property: JsonPropertyName("continuation")] string? Continuation,
    [property: JsonPropertyName("items")] List<FeedlyEntry> Items)
{
    public bool IsStreamEnd => string.IsNullOrEmpty(Continuation);
}

public sealed record FeedlyStreamIds(
    [property: JsonPropertyName("continuation")] string? Continuation,
    [property: JsonPropertyName("ids")] List<string> Ids)
{
    public bool IsStreamEnd => string.IsNullOrEmpty(Continuation);
}

public sealed record FeedlyFeedsSearchResult(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("feedId")] string FeedId);

public sealed record FeedlyFeedsSearchResponse(
    [property: JsonPropertyName("results")] List<FeedlyFeedsSearchResult> Results);

/// <summary>Port of <c>FeedlyMarkAction</c>. Serialized as <c>actionValue</c> in the markers API.</summary>
public enum FeedlyMarkAction
{
    Read,       // "markAsRead"
    Unread,     // "keepUnread"
    Saved,      // "markAsSaved"
    Unsaved,    // "markAsUnsaved"
}

public static class FeedlyMarkActionExtensions
{
    public static string ActionValue(this FeedlyMarkAction a) => a switch
    {
        FeedlyMarkAction.Read => "markAsRead",
        FeedlyMarkAction.Unread => "keepUnread",
        FeedlyMarkAction.Saved => "markAsSaved",
        FeedlyMarkAction.Unsaved => "markAsUnsaved",
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };
}
