using System.Text.Json.Serialization;

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
    [property: JsonPropertyName("enclosure")] List<FeedlyLink>? Enclosure)
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

    public DateTime DatePublished => DateTimeOffset.FromUnixTimeMilliseconds(Crawled).UtcDateTime;
    public DateTime? DateModified => Recrawled is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(Recrawled.Value).UtcDateTime;
    public string? ContentHtml => Content?.Content ?? Summary?.Content;
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
