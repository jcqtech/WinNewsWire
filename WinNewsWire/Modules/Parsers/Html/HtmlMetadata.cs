namespace WinNewsWire.Parsers;

/// <summary>Port of <c>RSHTMLMetadataFeedLink</c>.</summary>
public sealed record HtmlMetadataFeedLink(string? Title, string? Type, string? Url);

/// <summary>Port of <c>RSHTMLMetadataAppleTouchIcon</c>.</summary>
public sealed record HtmlMetadataAppleTouchIcon(string? Rel, string? Sizes, string? Url);

/// <summary>
/// Port of <c>RSHTMLMetadata</c> — the strongly-typed surface consumed by
/// <c>HTMLMetadataDownloader</c> and the feed finder.
/// </summary>
public sealed class HtmlMetadata
{
    public string? FaviconLink { get; init; }
    public string? OpenGraphImageUrl { get; init; }
    public string? TwitterImageUrl { get; init; }
    public IReadOnlyList<HtmlMetadataFeedLink> FeedLinks { get; init; } = Array.Empty<HtmlMetadataFeedLink>();
    public IReadOnlyList<HtmlMetadataAppleTouchIcon> AppleTouchIcons { get; init; } = Array.Empty<HtmlMetadataAppleTouchIcon>();
}
