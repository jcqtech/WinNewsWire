using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.Timeline;

public interface ISortableArticle
{
    string SortableName { get; }
    DateTime SortableDate { get; }
    string SortableArticleID { get; }
    string SortableFeedID { get; }
}

/// <summary>Port of <c>ArticleSorter</c>.</summary>
public static class ArticleSorter
{
    public static List<T> SortedByDate<T>(IEnumerable<T> articles, bool descending = true, bool groupByFeed = false) where T : ISortableArticle
        => groupByFeed ? SortByFeedName(articles, descending) : SortByDate(articles, descending);

    private static List<T> SortByDate<T>(IEnumerable<T> articles, bool desc) where T : ISortableArticle
    {
        var list = articles.ToList();
        list.Sort((a, b) =>
        {
            int c = a.SortableDate.CompareTo(b.SortableDate);
            if (c == 0) return string.CompareOrdinal(a.SortableArticleID, b.SortableArticleID);
            return desc ? -c : c;
        });
        return list;
    }

    private static List<T> SortByFeedName<T>(IEnumerable<T> articles, bool desc) where T : ISortableArticle
    {
        var groups = articles.GroupBy(a => $"{a.SortableName.ToLowerInvariant()}-{a.SortableFeedID}")
                             .OrderBy(g => g.Key, StringComparer.Ordinal);
        return groups.SelectMany(g => SortByDate(g, desc)).ToList();
    }
}

public sealed record SortableArticle(Article Article, string SortableName) : ISortableArticle
{
    // Use the same logical date the Mac sort uses: published → modified → arrived.
    public DateTime SortableDate
        => Article.DatePublished ?? Article.DateModified ?? Article.Status.DateArrived;
    public string SortableArticleID => Article.ArticleID;
    public string SortableFeedID => Article.FeedID;
}
