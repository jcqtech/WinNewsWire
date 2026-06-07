namespace WinNewsWire.Parsers;

/// <summary>Port of <c>FeedType</c> from <c>FeedType.swift</c>.</summary>
public enum FeedType
{
    Rss,
    Atom,
    JsonFeed,
    RssInJson,
    Unknown,
    NotAFeed,
}
