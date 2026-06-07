using WinNewsWire.AppShared.Timeline;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

/// <summary>
/// Full port of <c>Tests/NetNewsWireTests/ArticleSorterTests.swift</c>. Uses a purely-in-memory
/// <see cref="TestSortable"/> so these tests don't need the Articles database.
/// </summary>
public class ArticleSorterTests
{
    private sealed record TestSortable(string SortableName, DateTime SortableDate,
        string SortableArticleID, string SortableFeedID) : ISortableArticle;

    private static TestSortable A(string name, DateTime date, string id, string feedId)
        => new(name, date, id, feedId);

    [Fact]
    public void SortByDateAscending()
    {
        var now = DateTime.UtcNow;
        var a1 = A("Susie's Feed", now.AddSeconds(-60), "1", "4");
        var a2 = A("Phil's Feed",  now.AddSeconds(60),  "2", "6");
        var a3 = A("Phil's Feed",  now.AddSeconds(120), "3", "6");
        var a4 = A("Susie's Feed", now.AddSeconds(-120),"4", "5");

        var sorted = ArticleSorter.SortedByDate(new[] { a1, a2, a3, a4 }, descending: false, groupByFeed: false);
        Assert.Equal(new[] { a4, a1, a2, a3 }, sorted);
    }

    [Fact]
    public void SortByDateAscendingWithSameDate()
    {
        var now = DateTime.UtcNow;
        var a1 = A("Phil's Feed",  now, "1", "1");
        var a2 = A("Matt's Feed",  now, "2", "2");
        var a3 = A("Sally's Feed", now, "3", "3");
        var a4 = A("Susie's Feed", now.AddSeconds(-60),  "4", "4");
        var a5 = A("Paul's Feed",  now.AddSeconds(-120), "5", "5");

        var sorted = ArticleSorter.SortedByDate(new[] { a1, a2, a3, a4, a5 }, descending: false, groupByFeed: false);
        Assert.Equal(new[] { a5, a4, a1, a2, a3 }, sorted);
    }

    [Fact]
    public void SortByDateDescending()
    {
        var now = DateTime.UtcNow;
        var a1 = A("Susie's Feed", now.AddSeconds(-60), "1", "4");
        var a2 = A("Phil's Feed",  now.AddSeconds(60),  "2", "6");
        var a3 = A("Phil's Feed",  now.AddSeconds(120), "3", "6");
        var a4 = A("Susie's Feed", now.AddSeconds(-120),"4", "5");

        var sorted = ArticleSorter.SortedByDate(new[] { a1, a2, a3, a4 }, descending: true, groupByFeed: false);
        Assert.Equal(new[] { a3, a2, a1, a4 }, sorted);
    }

    [Fact]
    public void SortByDateDescendingWithSameDate()
    {
        var now = DateTime.UtcNow;
        var a1 = A("Phil's Feed",  now, "1", "1");
        var a2 = A("Matt's Feed",  now, "2", "2");
        var a3 = A("Sally's Feed", now, "3", "3");
        var a4 = A("Susie's Feed", now.AddSeconds(-60),  "4", "4");
        var a5 = A("Paul's Feed",  now.AddSeconds(-120), "5", "5");

        var sorted = ArticleSorter.SortedByDate(new[] { a1, a2, a3, a4, a5 }, descending: true, groupByFeed: false);
        Assert.Equal(new[] { a1, a2, a3, a4, a5 }, sorted);
    }

    [Fact]
    public void GroupByFeedWithSameFeedNamesSortsByFeedId()
    {
        var now = DateTime.UtcNow;
        var a1 = A("Phil's Feed", now, "1", "2");
        var a2 = A("Phil's Feed", now, "2", "2");
        var a3 = A("Phil's Feed", now, "3", "1");
        var a4 = A("Phil's Feed", now, "4", "2");
        var a5 = A("Phil's Feed", now, "5", "1");

        var sorted = ArticleSorter.SortedByDate(new[] { a1, a2, a3, a4, a5 }, descending: false, groupByFeed: true);
        Assert.Equal(new[] { a3, a5, a1, a2, a4 }, sorted);
    }

    [Fact]
    public void GroupByFeedWithCaseInsensitiveFeedNames()
    {
        var now = DateTime.UtcNow;
        var a1 = A("phil's feed",  now, "1", "1");
        var a2 = A("PhIl's FEed",  now, "2", "1");
        var a3 = A("APPLE's feed", now, "3", "2");
        var a4 = A("PHIL'S FEED",  now, "4", "1");
        var a5 = A("apple's feed", now, "5", "2");

        var sorted = ArticleSorter.SortedByDate(new[] { a1, a2, a3, a4, a5 }, descending: false, groupByFeed: true);
        // Apple's articles first (alphabetically), then Phil's; within each, sorted by articleID.
        Assert.Equal(new[] { a3, a5, a1, a2, a4 }, sorted);
    }

    // Port of Swift `testSortByDateAscendingWithGroupByFeed`. Nine articles
    // across four feeds, ascending sort grouped by feed name; verifies both
    // alphabetical group ordering and within-group chronological ordering.
    [Fact]
    public void SortByDateAscendingWithGroupByFeed()
    {
        var now = DateTime.UtcNow;
        var a1 = A("Phil's Feed",  now.AddSeconds(-100),  "1", "1");
        var a2 = A("Jenny's Feed", now,                   "1", "2");
        var a3 = A("Jenny's Feed", now.AddSeconds(-10),   "2", "2");
        var a4 = A("Gordy's Blog", now.AddSeconds(-1000), "1", "3");
        var a5 = A("Gordy's Blog", now.AddSeconds(-10),   "2", "3");
        var a6 = A("Jenny's Feed", now.AddSeconds(10),    "3", "2");
        var a7 = A("Phil's Feed",  now,                   "2", "1");
        var a8 = A("Zippy's Feed", now,                   "1", "0");
        var a9 = A("Zippy's Feed", now,                   "2", "0");

        var sorted = ArticleSorter.SortedByDate(
            new[] { a1, a2, a3, a4, a5, a6, a7, a8, a9 },
            descending: false, groupByFeed: true);

        // Gordy's, then Jenny's, then Phil's, then Zippy's (alphabetical).
        Assert.Equal(new[] { a4, a5, a3, a2, a6, a1, a7, a8, a9 }, sorted);
    }

    // Port of Swift `testSortByDateDescendingWithGroupByFeed`. Same dataset,
    // descending sort grouped by feed; group ordering stays alphabetical and
    // within-group ordering reverses.
    [Fact]
    public void SortByDateDescendingWithGroupByFeed()
    {
        var now = DateTime.UtcNow;
        var a1 = A("Phil's Feed",  now.AddSeconds(-100),  "1", "1");
        var a2 = A("Jenny's Feed", now,                   "1", "2");
        var a3 = A("Jenny's Feed", now.AddSeconds(-10),   "2", "2");
        var a4 = A("Gordy's Blog", now.AddSeconds(-1000), "1", "3");
        var a5 = A("Gordy's Blog", now.AddSeconds(-10),   "2", "3");
        var a6 = A("Jenny's Feed", now.AddSeconds(10),    "3", "2");
        var a7 = A("Phil's Feed",  now,                   "2", "1");
        var a8 = A("Zippy's Feed", now,                   "1", "0");
        var a9 = A("Zippy's Feed", now,                   "2", "0");

        var sorted = ArticleSorter.SortedByDate(
            new[] { a1, a2, a3, a4, a5, a6, a7, a8, a9 },
            descending: true, groupByFeed: true);

        // Gordy's, then Jenny's, then Phil's, then Zippy's (alphabetical groups);
        // within each group dates run newest → oldest.
        Assert.Equal(new[] { a5, a4, a6, a2, a3, a7, a1, a8, a9 }, sorted);
    }
}
