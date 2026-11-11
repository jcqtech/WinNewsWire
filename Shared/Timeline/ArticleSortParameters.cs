namespace WinNewsWire.AppShared.Timeline;

public enum ArticleSortKey { Date, Feed, Title, Unread, Starred }

public static class ArticleSortKeyExtensions
{
    public static bool SortsAscendingFirst(this ArticleSortKey key) => key is ArticleSortKey.Feed or ArticleSortKey.Title;
}

public sealed record ArticleSortParameters(ArticleSortKey Key, bool Descending = true);
