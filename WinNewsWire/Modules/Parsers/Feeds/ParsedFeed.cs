namespace WinNewsWire.Parsers;

/// <summary>Port of <c>ParsedFeed</c>.</summary>
public sealed record ParsedFeed(
    FeedType Type,
    string? Title,
    string? HomePageUrl,
    string? FeedUrl,
    string? Language,
    string? FeedDescription,
    string? NextUrl,
    string? IconUrl,
    string? FaviconUrl,
    IReadOnlySet<ParsedAuthor>? Authors,
    bool Expired,
    IReadOnlySet<ParsedHub>? Hubs,
    IReadOnlySet<ParsedItem> Items);
