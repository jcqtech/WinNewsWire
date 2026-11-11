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
    [property: JsonPropertyName("secure_image_urls")] Dictionary<string, string>? ImageUrls = null,
    [property: JsonPropertyName("story_tags")] List<string>? Tags = null)
{
    public DateTime? DatePublished => double.TryParse(PublishedTimestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && double.IsFinite(seconds) ? DateTime.UnixEpoch.AddSeconds(seconds) : null;
    public string? ImageUrl => ImageUrls?.Values.FirstOrDefault();
}

public sealed record NewsBlurStoriesResponse([property: JsonPropertyName("stories")] List<NewsBlurStory> Stories);
