using WinNewsWire.Articles;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Core;

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
    public static List<Article> Sorted(IEnumerable<Article> articles, ArticleSortParameters parameters,
        Func<Article, string>? feedNameFor = null)
    {
        feedNameFor ??= _ => "";
        if (parameters.Key == ArticleSortKey.Date)
            return SortedByDate(articles.Select(a => new SortableArticle(a, feedNameFor(a))), parameters.Descending)
                .Select(a => a.Article).ToList();
        if (parameters.Key == ArticleSortKey.Feed)
            return SortByFeedName(articles.Select(a => new SortableArticle(a, feedNameFor(a))),
                desc: true, feedDescending: parameters.Descending).Select(a => a.Article).ToList();

        var list = articles.ToList();
        if (parameters.Key == ArticleSortKey.Title)
        {
            var titled = list.Select(a => (Article: a, Title: SortableTitle(a))).ToList();
            titled.Sort((a, b) =>
            {
                int comparison = LocalizedStandardComparer.Shared.Compare(a.Title, b.Title);
                return comparison == 0 ? CompareByDate(a.Article, b.Article)
                    : parameters.Descending ? -Math.Sign(comparison) : comparison;
            });
            return titled.Select(a => a.Article).ToList();
        }

        list.Sort((a, b) =>
        {
            bool flagA = parameters.Key == ArticleSortKey.Unread ? !a.Status.Read : a.Status.Starred;
            bool flagB = parameters.Key == ArticleSortKey.Unread ? !b.Status.Read : b.Status.Starred;
            int comparison = flagA.CompareTo(flagB);
            return comparison == 0 ? CompareByDate(a, b) : parameters.Descending ? -comparison : comparison;
        });
        return list;
    }

    private static string SortableTitle(Article article)
    {
        var title = ArticleStringFormatter.Shared.TruncatedTitle(article);
        return title.Length > 0 ? title : ArticleStringFormatter.Shared.TruncatedSummary(article);
    }

    private static int CompareByDate(Article a, Article b)
    {
        int comparison = b.LogicalDatePublished().CompareTo(a.LogicalDatePublished());
        return comparison != 0 ? comparison : string.CompareOrdinal(a.ArticleID, b.ArticleID);
    }

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

    private static List<T> SortByFeedName<T>(IEnumerable<T> articles, bool desc, bool feedDescending = false) where T : ISortableArticle
    {
        var groups = articles.GroupBy(a => a.SortableFeedID).ToList();
        groups.Sort((a, b) =>
        {
            int comparison = LocalizedStandardComparer.Shared.Compare(a.First().SortableName, b.First().SortableName);
            return comparison == 0 ? string.CompareOrdinal(a.Key, b.Key)
                : feedDescending ? -Math.Sign(comparison) : comparison;
        });
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
