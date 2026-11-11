using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNewsWire.NewsBlur;

public sealed record NewsBlurStoryHash(string Hash, DateTime Timestamp);

[JsonConverter(typeof(NewsBlurStoryHashesResponseConverter))]
public sealed record NewsBlurStoryHashesResponse(IReadOnlyList<NewsBlurStoryHash>? Unread, IReadOnlyList<NewsBlurStoryHash>? Starred);

public sealed class NewsBlurStoryHashesResponseConverter : JsonConverter<NewsBlurStoryHashesResponse>
{
    public override NewsBlurStoryHashesResponse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        IReadOnlyList<NewsBlurStoryHash>? unread = root.TryGetProperty("unread_feed_story_hashes", out var unreadValue)
            ? unreadValue.EnumerateObject().SelectMany(feed => ParseHashes(feed.Value)).ToList() : null;
        IReadOnlyList<NewsBlurStoryHash>? starred = root.TryGetProperty("starred_story_hashes", out var starredValue)
            ? ParseHashes(starredValue).ToList() : null;
        return new(unread, starred);
    }

    private static IEnumerable<NewsBlurStoryHash> ParseHashes(JsonElement array)
    {
        foreach (var pair in array.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 || pair[0].ValueKind != JsonValueKind.String)
                throw new JsonException("NewsBlur returned an invalid story hash.");
            double timestamp = pair[1].ValueKind == JsonValueKind.Number ? pair[1].GetDouble()
                : double.TryParse(pair[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds
                : throw new JsonException("NewsBlur returned an invalid story timestamp.");
            yield return new(pair[0].GetString()!, DateTime.UnixEpoch.AddSeconds(timestamp));
        }
    }

    public override void Write(Utf8JsonWriter writer, NewsBlurStoryHashesResponse value, JsonSerializerOptions options)
        => throw new NotSupportedException("NewsBlur story hash responses are read-only.");
}
