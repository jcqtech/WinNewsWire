using WinNewsWire.Parsers.Internal;

namespace WinNewsWire.Parsers;

/// <summary>
/// Port of <c>feedType(ParserData)</c>. Returns <see cref="FeedType.Unknown"/> if there
/// aren't enough bytes yet (caller can retry once more data has been downloaded), and
/// <see cref="FeedType.NotAFeed"/> once the bytes are clearly not any known feed type.
/// </summary>
public static class FeedTypeDetector
{
    private const int MinBytes = 128;

    public static FeedType Detect(ParserData data, bool isPartialData = false)
    {
        if (data.Data.Length < MinBytes) return FeedType.Unknown;
        var bytes = data.Data.AsSpan();

        if (DataProbes.IsProbablyJsonFeed(bytes)) return FeedType.JsonFeed;
        if (DataProbes.IsProbablyRssInJson(bytes)) return FeedType.RssInJson;
        if (DataProbes.IsProbablyRss(bytes)) return FeedType.Rss;
        if (DataProbes.IsProbablyAtom(bytes)) return FeedType.Atom;

        if (isPartialData && DataProbes.IsProbablyJson(bytes))
        {
            // See Dr. Drang's JSON Feed story: version marker may come at the very end
            // of the file. Defer classification until the full payload is available.
            return FeedType.Unknown;
        }

        return FeedType.NotAFeed;
    }
}
