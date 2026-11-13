using System.Text.Json.Serialization;
using System.Text.Json;
using System.Globalization;
using WinNewsWire.Parsers;
using WinNewsWire.Account;

namespace WinNewsWire.ReaderAPI;

public sealed record ReaderAPISubscription(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("url")] string? FeedUrl,
    [property: JsonPropertyName("htmlUrl")] string? HomePageUrl,
    [property: JsonPropertyName("categories")] List<ReaderAPICategory>? Categories,
    [property: JsonPropertyName("iconUrl")] string? IconUrl = null)
{
    [JsonIgnore]
    public string Url => FeedUrl ?? (Id.StartsWith("feed/", StringComparison.Ordinal) ? Id[5..] : Id);
}

public sealed record ReaderAPICategory(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string? Label);

public sealed record ReaderAPISubscriptionsResponse(
    [property: JsonPropertyName("subscriptions")] List<ReaderAPISubscription> Subscriptions);

public sealed record ReaderAPITag(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string? Type = null)
{
    [JsonIgnore]
    public string? FolderName => Id.IndexOf("/label/", StringComparison.Ordinal) is var index && index >= 0 ? Id[(index + 7)..] : null;
}

public sealed record ReaderAPITagsResponse(
    [property: JsonPropertyName("tags")] List<ReaderAPITag> Tags);

public sealed record ReaderAPIEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("published")] double? Published,
    [property: JsonPropertyName("updated")] double? Updated,
    [property: JsonPropertyName("summary")] ReaderAPIContent? Summary,
    [property: JsonPropertyName("content")] ReaderAPIContent? Content,
    [property: JsonPropertyName("alternate")] List<ReaderAPIHref>? Alternate,
    [property: JsonPropertyName("origin")] ReaderAPIOrigin? Origin,
    [property: JsonPropertyName("categories")] List<string>? Categories,
    [property: JsonPropertyName("enclosure")] List<ReaderAPIEnclosure>? Enclosures = null)
{
    public IReadOnlyList<ParsedAttachment>? Attachments => Enclosures?.Select(enclosure => enclosure.ParsedAttachment)
        .OfType<ParsedAttachment>().ToArray() is { Length: > 0 } attachments ? attachments : null;
    public string? ImageUrl => Enclosures?.FirstOrDefault(enclosure => enclosure.IsImage)?.Url;

    public string UniqueID(ReaderAPIVariant variant)
        => UniqueID(Id, variant);

    internal static string UniqueID(string id, ReaderAPIVariant variant)
    {
        var part = id[(id.LastIndexOf('/') + 1)..];
        if (variant == ReaderAPIVariant.TheOldReader) return part;
        return ulong.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number)
            ? unchecked((long)number).ToString(CultureInfo.InvariantCulture) : id;
    }

    public DateTime? ParseDatePublished() => Published is { } value ? DateTime.UnixEpoch.AddSeconds(value) : null;
}

public sealed record ReaderAPIEnclosure(
    [property: JsonPropertyName("href"), JsonConverter(typeof(OptionalMediaStringConverter))] string? Url,
    [property: JsonPropertyName("type"), JsonConverter(typeof(OptionalMediaStringConverter))] string? Type,
    [property: JsonPropertyName("length")] JsonElement? Length = null)
{
    private string? MimeType => Type?.Contains('/') == true ? Type : null;
    private ParsedAttachment.AttachmentMedium? Medium => MimeType is null ? Parsers.ParsedAttachment.ParseMedium(Type) : null;
    public bool IsImage => Medium == Parsers.ParsedAttachment.AttachmentMedium.Image
        || MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    private long? SizeInBytes
    {
        get
        {
            long size;
            bool parsed = Length is { ValueKind: JsonValueKind.Number } number ? number.TryGetInt64(out size)
                : long.TryParse(Length is { ValueKind: JsonValueKind.String } text ? text.GetString() : null,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            return parsed && size > 0 ? size : null;
        }
    }

    public ParsedAttachment? ParsedAttachment => Parsers.ParsedAttachment.Create(Url, MimeType, null, SizeInBytes, null, medium: Medium);
}

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

public sealed record ReaderAPIQuickAddResult(
    [property: JsonPropertyName("numResults")] int NumResults,
    [property: JsonPropertyName("streamId")] string? StreamId);

public sealed record ReaderAPIReference([property: JsonPropertyName("id")] string? ID);
public sealed record ReaderAPIReferenceWrapper(
    [property: JsonPropertyName("itemRefs")] List<ReaderAPIReference>? ItemRefs,
    [property: JsonPropertyName("continuation")] string? Continuation);
public sealed record ReaderAPIItemIDs(IReadOnlySet<string> IDs, DateTime? ServerDate);
public sealed record ReaderAPIUsageLimits(int Zone1Usage, int Zone1Limit, DateTime ResetDate);
