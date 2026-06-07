using WinNewsWire.AppShared.ArticleRendering;
using WinNewsWire.AppShared.Commands;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.AppShared.Favicons;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.AppShared.Timer;
using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public class SharedSmokeTests
{
    private static Article MakeArticle(string feed, string unique, DateTime? date, bool read = false, bool starred = false, string? title = null, string? summary = null)
    {
        var status = new ArticleStatus(Articles.Article.CalculatedArticleID(feed, unique), read, starred, DateTime.UtcNow);
        return new Article(
            "acct", null, feed, unique,
            title, null, null, null,
            null, null, summary, null,
            date, null, null, null, status);
    }

    [Fact]
    public void ArticleSorter_SortsByDate_Descending()
    {
        var older = new SortableArticle(MakeArticle("f", "1", DateTime.UtcNow.AddHours(-1)), "A");
        var newer = new SortableArticle(MakeArticle("f", "2", DateTime.UtcNow), "A");
        var sorted = ArticleSorter.SortedByDate(new[] { older, newer });
        Assert.Equal(newer.Article.ArticleID, sorted[0].Article.ArticleID);
    }

    [Fact]
    public void ArticleSorter_GroupByFeed_GroupsThenSortsByDate()
    {
        var a1 = new SortableArticle(MakeArticle("f1", "1", DateTime.UtcNow.AddHours(-1)), "Beta");
        var a2 = new SortableArticle(MakeArticle("f1", "2", DateTime.UtcNow), "Beta");
        var b1 = new SortableArticle(MakeArticle("f2", "1", DateTime.UtcNow), "Alpha");
        var sorted = ArticleSorter.SortedByDate(new[] { a1, a2, b1 }, groupByFeed: true);
        Assert.Equal("Alpha", sorted[0].SortableName);
    }

    [Fact]
    public void ColorHash_Deterministic()
    {
        var a = ColorHash.ColorForString("example.com");
        var b = ColorHash.ColorForString("example.com");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ArticleStringFormatter_TruncatesSummary()
    {
        var long_ = new string('x', 500);
        var article = MakeArticle("f", "u-truncate", DateTime.UtcNow, summary: long_);
        var s = ArticleStringFormatter.TruncatedSummary(article);
        Assert.True(s.Length <= 301);
        Assert.EndsWith("…", s);
    }

    [Fact]
    public void ArticleStringFormatter_StripsHtmlFromTitle()
    {
        var a = MakeArticle("f", "u-title", DateTime.UtcNow, title: "<b>hi</b> &amp; bye");
        Assert.Equal("hi & bye", ArticleStringFormatter.TruncatedTitle(a));
    }

    [Fact]
    public void RefreshInterval_IntervalValues()
    {
        Assert.Null(RefreshInterval.Manually.Interval());
        Assert.Equal(TimeSpan.FromHours(1), RefreshInterval.EveryHour.Interval());
        Assert.Equal(TimeSpan.FromHours(8), RefreshInterval.Every8Hours.Interval());
    }

    [Fact]
    public void MarkStatusCommand_UndoRedo()
    {
        var a = MakeArticle("f", "u", DateTime.UtcNow, read: false);
        var applied = new List<(ArticleStatus.Key, bool)>();
        Task Apply(IEnumerable<string> _, ArticleStatus.Key k, bool v) { applied.Add((k, v)); return Task.CompletedTask; }
        var cmd = MarkStatusCommand.Create(new[] { a }, ArticleStatus.Key.Read, true, Apply);
        Assert.NotNull(cmd);
        cmd!.Perform();
        Assert.True(a.Status.Read);
        cmd.Undo();
        Assert.False(a.Status.Read);
        cmd.Redo();
        Assert.True(a.Status.Read);
        Assert.Equal(3, applied.Count);
    }

    [Fact]
    public void ArticleRenderer_ProducesHtml()
    {
        var a = MakeArticle("f", "u-render", DateTime.UtcNow, title: "Hello");
        var render = ArticleRenderer.Render(a);
        Assert.False(string.IsNullOrEmpty(render.Html));
    }
}
