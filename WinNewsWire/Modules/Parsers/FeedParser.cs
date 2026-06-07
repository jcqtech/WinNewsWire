namespace WinNewsWire.Parsers;

/// <summary>
/// Top-level orchestrator — port of <c>FeedParser</c>. Detects the feed type and
/// dispatches to the appropriate concrete parser.
/// </summary>
public static class FeedParser
{
    public static bool CanParse(ParserData parserData)
    {
        var t = FeedTypeDetector.Detect(parserData);
        return t is not FeedType.NotAFeed and not FeedType.Unknown;
    }

    public static ParsedFeed? Parse(ParserData parserData)
    {
        var type = FeedTypeDetector.Detect(parserData);
        return type switch
        {
            FeedType.JsonFeed => JsonFeedParser.Parse(parserData),
            FeedType.RssInJson => RssInJsonParser.Parse(parserData),
            FeedType.Rss => RssParser.Parse(parserData),
            FeedType.Atom => AtomParser.Parse(parserData),
            _ => null,
        };
    }
}
