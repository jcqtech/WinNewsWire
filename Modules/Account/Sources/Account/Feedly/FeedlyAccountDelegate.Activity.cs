namespace WinNewsWire.Feedly;

public sealed partial class FeedlyAccountDelegate
{
    internal readonly record struct StatusRefreshCounts(int UnreadAdded = 0, int UnreadRemoved = 0, int StarredAdded = 0, int StarredRemoved = 0)
    {
        public int TotalChanged => UnreadAdded + UnreadRemoved + StarredAdded + StarredRemoved;
    }

    internal readonly record struct FeedListChanges(int FoldersAdded = 0, int FoldersRemoved = 0,
        int FeedsAdded = 0, int FeedsRemoved = 0, int FeedsRenamed = 0)
    {
        public int TotalChanged => FoldersAdded + FoldersRemoved + FeedsAdded + FeedsRemoved + FeedsRenamed;
    }

    internal sealed record RefreshAllSummary
    {
        public int StatusesSent { get; init; }
        public FeedListChanges FeedListChanges { get; init; }
        public StatusRefreshCounts StatusRefreshCounts { get; init; }
        public int ArticlesDownloaded { get; init; }
        public int NewArticlesFromFeedRefresh { get; init; }
    }

    internal static string SendStatusMessage(int count) => count == 0 ? "No statuses to send" : PluralCount(count, "status", "statuses") + " sent";
    internal static string RefreshStatusMessage(StatusRefreshCounts counts) => JoinedOrNoChanges(StatusParts(counts));
    internal static string FeedListMessage(FeedListChanges changes) => JoinedOrNoChanges(FeedParts(changes));
    internal static string RefreshAllMessage(RefreshAllSummary summary)
    {
        var parts = new List<string>();
        if (summary.ArticlesDownloaded > 0) parts.Add(PluralCount(summary.ArticlesDownloaded, "article") + " downloaded");
        if (summary.NewArticlesFromFeedRefresh > 0) parts.Add($"{summary.NewArticlesFromFeedRefresh} new from feed refresh");
        parts.AddRange(StatusParts(summary.StatusRefreshCounts));
        if (summary.StatusesSent > 0) parts.Add(PluralCount(summary.StatusesSent, "status", "statuses") + " sent");
        parts.AddRange(FeedParts(summary.FeedListChanges));
        return JoinedOrNoChanges(parts);
    }

    private static IEnumerable<string> StatusParts(StatusRefreshCounts counts)
    {
        if (counts.UnreadAdded > 0) yield return $"{counts.UnreadAdded} marked unread";
        if (counts.UnreadRemoved > 0) yield return $"{counts.UnreadRemoved} marked read";
        if (counts.StarredAdded > 0) yield return $"{counts.StarredAdded} starred";
        if (counts.StarredRemoved > 0) yield return $"{counts.StarredRemoved} unstarred";
    }

    private static IEnumerable<string> FeedParts(FeedListChanges changes)
    {
        if (changes.FoldersAdded > 0) yield return PluralCount(changes.FoldersAdded, "folder") + " added";
        if (changes.FoldersRemoved > 0) yield return PluralCount(changes.FoldersRemoved, "folder") + " removed";
        if (changes.FeedsAdded > 0) yield return PluralCount(changes.FeedsAdded, "feed") + " added";
        if (changes.FeedsRemoved > 0) yield return PluralCount(changes.FeedsRemoved, "feed") + " removed";
        if (changes.FeedsRenamed > 0) yield return PluralCount(changes.FeedsRenamed, "feed") + " renamed";
    }

    private static string PluralCount(int count, string singular, string? plural = null) => $"{count} {(count == 1 ? singular : plural ?? singular + "s")}";
    private static string JoinedOrNoChanges(IEnumerable<string> parts)
    {
        var message = string.Join(", ", parts);
        return message.Length > 0 ? message : "No changes";
    }
}
