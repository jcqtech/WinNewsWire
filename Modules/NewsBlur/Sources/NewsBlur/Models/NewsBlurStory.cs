using System.Globalization;
using System.Text.Json.Serialization;

namespace WinNewsWire.NewsBlur;

public sealed record NewsBlurStory(
    [property: JsonPropertyName("story_hash")] string StoryID,
    [property: JsonPropertyName("story_feed_id")] long FeedID,
    [property: JsonPropertyName("story_title")] string? Title,
    [property: JsonPropertyName("story_permalink")] string? Url,
    [property: JsonPropertyName("story_authors")] string? AuthorName,
    [property: JsonPropertyName("story_content")] string? ContentHtml,
    [property: JsonPropertyName("story_timestamp")] string? PublishedTimestamp,
    [property: JsonPropertyName("secure_image_urls")] Dictionary<string, string>? SecureImageUrls = null,
    [property: JsonPropertyName("story_tags")] List<string>? Tags = null,
    [property: JsonPropertyName("image_urls")] List<string>? ImageUrls = null,
    [property: JsonPropertyName("secure_image_thumbnails")] Dictionary<string, string>? SecureImageThumbnails = null)
{
    public DateTime? DatePublished => double.TryParse(PublishedTimestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && double.IsFinite(seconds) ? DateTime.UnixEpoch.AddSeconds(seconds) : null;
    public string? ImageUrl => ImageUrls?.FirstOrDefault() is { } first ? SecureImageUrls?.GetValueOrDefault(first) ?? first : null;
    public string? ThumbnailUrl => ImageUrls?.FirstOrDefault() is { } first ? SecureImageThumbnails?.GetValueOrDefault(first) : null;
}

public sealed record NewsBlurStoriesResponse([property: JsonPropertyName("stories")] List<NewsBlurStory> Stories);
