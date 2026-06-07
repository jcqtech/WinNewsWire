using System.Text.Json.Serialization;

namespace WinNewsWire.NewsBlur;

public sealed record NewsBlurLoginResponse(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("authenticated")] bool Authenticated);

public sealed record NewsBlurFeed(
    [property: JsonPropertyName("id")] long FeedID,
    [property: JsonPropertyName("feed_title")] string? Name,
    [property: JsonPropertyName("feed_address")] string? FeedUrl,
    [property: JsonPropertyName("feed_link")] string? HomePageUrl);

/// <summary>Port of <c>NewsBlurStoriesResponse.Story</c>.</summary>
public sealed record NewsBlurStory(
    [property: JsonPropertyName("story_hash")] string StoryID,
    [property: JsonPropertyName("story_feed_id")] long FeedID,
    [property: JsonPropertyName("story_title")] string? Title,
    [property: JsonPropertyName("story_permalink")] string? Url,
    [property: JsonPropertyName("story_authors")] string? AuthorName,
    [property: JsonPropertyName("story_content")] string? ContentHtml,
    [property: JsonPropertyName("story_timestamp")] string? PublishedTimestamp)
{
    public DateTime? DatePublished
    {
        get
        {
            if (double.TryParse(PublishedTimestamp, out var secs))
                return DateTimeOffset.FromUnixTimeSeconds((long)secs).UtcDateTime;
            return null;
        }
    }
}

public sealed record NewsBlurStoriesResponse(
    [property: JsonPropertyName("stories")] List<NewsBlurStory> Stories);

public sealed record NewsBlurStoryHashesResponse(
    [property: JsonPropertyName("unread_feed_story_hashes")] Dictionary<string, List<List<object>>>? UnreadByFeed);
