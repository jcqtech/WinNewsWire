using System.Text.Json.Serialization;

namespace WinNewsWire.Feedbin;

/// <summary>Port of <c>FeedbinSubscription</c>.</summary>
public sealed record FeedbinSubscription(
    [property: JsonPropertyName("id")] long SubscriptionID,
    [property: JsonPropertyName("feed_id")] long FeedID,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("feed_url")] string FeedUrl,
    [property: JsonPropertyName("site_url")] string? SiteUrl,
    [property: JsonPropertyName("json_feed")] FeedbinSubscriptionJsonFeed? JsonFeed = null);

public sealed record FeedbinSubscriptionJsonFeed(
    [property: JsonPropertyName("favicon")] string? Favicon,
    [property: JsonPropertyName("icon")] string? Icon);

public sealed record FeedbinSubscriptionChoice(
    [property: JsonPropertyName("feed_url")] string FeedUrl,
    [property: JsonPropertyName("title")] string? Title);

public abstract record FeedbinCreateSubscriptionResult
{
    public sealed record Created(FeedbinSubscription Subscription) : FeedbinCreateSubscriptionResult;
    public sealed record MultipleChoice(IReadOnlyList<FeedbinSubscriptionChoice> Choices) : FeedbinCreateSubscriptionResult;
    public sealed record AlreadySubscribed : FeedbinCreateSubscriptionResult;
    public sealed record NotFound : FeedbinCreateSubscriptionResult;
}

public sealed record FeedbinEntryPage(IReadOnlyList<FeedbinEntry> Entries, DateTime? ServerDate);

public sealed record FeedbinConditionalResponse<T>(IReadOnlyList<T>? Items, Web.ConditionalGetInfo? ConditionalGetInfo);

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
