using System.Text.Json.Serialization;
using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using WinNewsWire.Parsers;

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
    [property: JsonPropertyName("created_at")] DateTime? DateArrived,
    [property: JsonPropertyName("images")] FeedbinEntryImages? Images = null,
    [property: JsonPropertyName("enclosure")] FeedbinEntryEnclosure? Enclosure = null)
{
    public string? ImageUrl => Images?.OriginalUrl ?? Enclosure?.ITunesImageUrl;
    public string? ThumbnailUrl => Images?.Thumbnail?.CdnUrl;
}

public sealed record FeedbinEntryImages(
    [property: JsonPropertyName("original_url"), JsonConverter(typeof(FeedbinOptionalStringConverter))] string? OriginalUrl,
    [property: JsonPropertyName("size_1"), JsonConverter(typeof(FeedbinThumbnailConverter))] FeedbinEntryThumbnail? Thumbnail);

public sealed record FeedbinEntryThumbnail(
    [property: JsonPropertyName("cdn_url"), JsonConverter(typeof(FeedbinOptionalStringConverter))] string? CdnUrl);

public sealed record FeedbinEntryEnclosure(
    [property: JsonPropertyName("enclosure_url"), JsonConverter(typeof(FeedbinOptionalStringConverter))] string? Url,
    [property: JsonPropertyName("enclosure_type"), JsonConverter(typeof(FeedbinOptionalStringConverter))] string? MimeType,
    [property: JsonPropertyName("enclosure_length"), JsonConverter(typeof(FeedbinOptionalStringConverter))] string? Length,
    [property: JsonPropertyName("itunes_duration"), JsonConverter(typeof(FeedbinOptionalStringConverter))] string? ITunesDuration,
    [property: JsonPropertyName("itunes_image"), JsonConverter(typeof(FeedbinOptionalStringConverter))] string? ITunesImageUrl)
{
    public ParsedAttachment? ParsedAttachment => Parsers.ParsedAttachment.Create(Url, MimeType, null,
        long.TryParse(Length, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size > 0 ? size : null,
        Parsers.ParsedAttachment.ParseDurationInSeconds(ITunesDuration));
}

internal sealed class FeedbinOptionalStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString();
        using var ignored = JsonDocument.ParseValue(ref reader);
        Trace.TraceWarning("Ignored a non-string optional Feedbin media field.");
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

internal sealed class FeedbinThumbnailConverter : JsonConverter<FeedbinEntryThumbnail>
{
    public override FeedbinEntryThumbnail? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartObject) return JsonSerializer.Deserialize<FeedbinEntryThumbnail>(ref reader, options);
        using var ignored = JsonDocument.ParseValue(ref reader);
        Trace.TraceWarning("Ignored a non-object optional Feedbin thumbnail.");
        return null;
    }

    public override void Write(Utf8JsonWriter writer, FeedbinEntryThumbnail value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}
