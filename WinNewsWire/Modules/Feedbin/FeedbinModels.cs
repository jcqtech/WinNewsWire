using System.Text.Json.Serialization;

namespace WinNewsWire.Feedbin;

/// <summary>Port of <c>FeedbinSubscription</c>.</summary>
public sealed record FeedbinSubscription(
    [property: JsonPropertyName("id")] long SubscriptionID,
    [property: JsonPropertyName("feed_id")] long FeedID,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("feed_url")] string FeedUrl,
    [property: JsonPropertyName("site_url")] string? SiteUrl);

public sealed record FeedbinSubscriptionChoice(
    [property: JsonPropertyName("feed_url")] string FeedUrl,
    [property: JsonPropertyName("title")] string? Title);

/// <summary>Port of <c>FeedbinTag</c>.</summary>
public sealed record FeedbinTag(
    [property: JsonPropertyName("id")] long TagID,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Port of <c>FeedbinTagging</c>.</summary>
public sealed record FeedbinTagging(
    [property: JsonPropertyName("id")] long TaggingID,
    [property: JsonPropertyName("feed_id")] long FeedID,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Port of <c>FeedbinEntry</c>.</summary>
public sealed record FeedbinEntry(
    [property: JsonPropertyName("id")] long ArticleID,
    [property: JsonPropertyName("feed_id")] long FeedID,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("extracted_content_url")] string? ExtractedContentUrl,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("content")] string? ContentHtml,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("published")] DateTime? DatePublished,
    [property: JsonPropertyName("created_at")] DateTime? DateArrived);
