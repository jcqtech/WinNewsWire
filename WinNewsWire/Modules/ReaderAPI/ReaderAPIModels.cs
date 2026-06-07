using System.Text.Json.Serialization;

namespace WinNewsWire.ReaderAPI;

public sealed record ReaderAPISubscription(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? FeedUrl,
    [property: JsonPropertyName("htmlUrl")] string? HomePageUrl,
    [property: JsonPropertyName("categories")] List<ReaderAPICategory>? Categories);

public sealed record ReaderAPICategory(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string? Label);

public sealed record ReaderAPISubscriptionsResponse(
    [property: JsonPropertyName("subscriptions")] List<ReaderAPISubscription> Subscriptions);

public sealed record ReaderAPITag(
    [property: JsonPropertyName("id")] string Id);

public sealed record ReaderAPITagsResponse(
    [property: JsonPropertyName("tags")] List<ReaderAPITag> Tags);

public sealed record ReaderAPIEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("published")] long Published,
    [property: JsonPropertyName("updated")] long Updated,
    [property: JsonPropertyName("summary")] ReaderAPIContent? Summary,
    [property: JsonPropertyName("content")] ReaderAPIContent? Content,
    [property: JsonPropertyName("alternate")] List<ReaderAPIHref>? Alternate,
    [property: JsonPropertyName("origin")] ReaderAPIOrigin? Origin,
    [property: JsonPropertyName("categories")] List<string>? Categories);

public sealed record ReaderAPIContent([property: JsonPropertyName("content")] string? Content);
public sealed record ReaderAPIHref([property: JsonPropertyName("href")] string? Href);
public sealed record ReaderAPIOrigin(
    [property: JsonPropertyName("streamId")] string? StreamId,
    [property: JsonPropertyName("title")] string? Title);

public sealed record ReaderAPIStreamContentsResponse(
    [property: JsonPropertyName("items")] List<ReaderAPIEntry> Items,
    [property: JsonPropertyName("continuation")] string? Continuation);

public sealed record ReaderAPIUnreadCountsResponse(
    [property: JsonPropertyName("unreadcounts")] List<ReaderAPIUnreadCount> Counts);

public sealed record ReaderAPIUnreadCount(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("count")] int Count);
