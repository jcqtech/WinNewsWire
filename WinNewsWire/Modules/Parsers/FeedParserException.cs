namespace WinNewsWire.Parsers;

/// <summary>Port of <c>FeedParserError</c>.</summary>
public sealed class FeedParserException : Exception
{
    public FeedParserErrorKind Kind { get; }
    public FeedParserException(FeedParserErrorKind kind) : base(kind.ToString()) { Kind = kind; }
    public FeedParserException(FeedParserErrorKind kind, string message) : base(message) { Kind = kind; }
    public FeedParserException(FeedParserErrorKind kind, string message, Exception inner) : base(message, inner) { Kind = kind; }
}

public enum FeedParserErrorKind
{
    RssChannelNotFound,
    RssItemsNotFound,
    JsonFeedVersionNotFound,
    JsonFeedItemsNotFound,
    JsonFeedTitleNotFound,
    InvalidJson,
    InvalidXml,
}
