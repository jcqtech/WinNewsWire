using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNewsWire.NewsBlur;

public sealed record NewsBlurFeed(
    [property: JsonPropertyName("id")] long FeedID,
    [property: JsonPropertyName("feed_title")] string? Name,
    [property: JsonPropertyName("feed_address")] string? FeedUrl,
    [property: JsonPropertyName("feed_link")] string? HomePageUrl,
    [property: JsonPropertyName("favicon_url")] string? FaviconUrl = null);

public sealed record NewsBlurFolder(string Name, IReadOnlyList<long> FeedIDs);

[JsonConverter(typeof(NewsBlurFeedsResponseConverter))]
public sealed record NewsBlurFeedsResponse(IReadOnlyList<NewsBlurFeed> Feeds, IReadOnlyList<NewsBlurFolder> Folders);

public sealed class NewsBlurFeedsResponseConverter : JsonConverter<NewsBlurFeedsResponse>
{
    public override NewsBlurFeedsResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (!root.TryGetProperty("feeds", out var feeds) || feeds.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("flat_folders", out var folders) || folders.ValueKind != JsonValueKind.Object)
            throw new JsonException("NewsBlur did not return feed and folder dictionaries.");
        var folderList = folders.EnumerateObject().Select(folder => new NewsBlurFolder(folder.Name,
            folder.Value.EnumerateArray().Select(id => id.GetInt64()).ToArray())).ToList();
        var visible = folderList.SelectMany(folder => folder.FeedIDs).ToHashSet();
        var feedList = feeds.EnumerateObject().Select(item => item.Value.Deserialize<NewsBlurFeed>(options)
            ?? throw new JsonException("NewsBlur returned an empty feed.")).Where(feed => visible.Contains(feed.FeedID)).ToList();
        if (feedList.Any(feed => string.IsNullOrEmpty(feed.FeedUrl))) throw new JsonException("NewsBlur returned a feed without its address.");
        return new(feedList, folderList);
    }

    public override void Write(Utf8JsonWriter writer, NewsBlurFeedsResponse value, JsonSerializerOptions options)
        => throw new NotSupportedException("NewsBlur feed responses are read-only.");
}
