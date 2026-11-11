namespace WinNewsWire.Feedly;

/// <summary>Port of <c>FeedlyFeedResourceId</c> and friends from <c>FeedlyModel.swift</c>.
/// Feedly exposes various resource kinds (feed, category, tag) that are each encoded
/// as opaque URIs; these helpers build and unwrap them.</summary>
public static class FeedlyResourceIds
{
    private const string FeedPrefix = "feed/";

    public static string FeedIdForUrl(string url) => FeedPrefix + url;

    public static string UrlFromFeedId(string feedId)
        => feedId.StartsWith(FeedPrefix, StringComparison.Ordinal)
            ? feedId[FeedPrefix.Length..]
            : feedId;

    public static string GlobalAllFor(string userId) => $"user/{userId}/category/global.all";
    public static string GlobalUncategorizedFor(string userId) => $"user/{userId}/category/global.uncategorized";
    public static string GlobalMustReadFor(string userId) => $"user/{userId}/category/global.must";
    public static string GlobalSavedFor(string userId) => $"user/{userId}/tag/global.saved";
    public static string GlobalReadFor(string userId) => $"user/{userId}/tag/global.read";
}
