using System.Globalization;
using System.Runtime.CompilerServices;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

/// <summary>Portable cases from Tests/NetNewsWireTests/ArticleStringFormatterTests.swift.</summary>
public class ArticleStringFormatterTests
{
    [Fact]
    public void TruncatedTitlePlain()
        => Assert.Equal("Hello World", new ArticleStringFormatter().TruncatedTitle(MakeArticle(title: "Hello World")));

    [Fact]
    public void TruncatedTitleReplacesControlCharactersWithSpaces()
        => Assert.Equal("Hello World with breaks", new ArticleStringFormatter().TruncatedTitle(
            MakeArticle(title: "\rHello\nWorld\twith\r\t\rbreaks\n\t\r\r")));

    [Fact]
    public void TruncatedTitleDecodesEntitiesWhenNotForHtml()
        => Assert.Equal("Tom & Jerry", new ArticleStringFormatter().TruncatedTitle(MakeArticle(title: "Tom &amp; Jerry"), forHtml: false));

    [Fact]
    public void TruncatedTitleKeepsEntitiesWhenForHtml()
        => Assert.Equal("Tom &amp; Jerry", new ArticleStringFormatter().TruncatedTitle(MakeArticle(title: "Tom &amp; Jerry"), forHtml: true));

    [Fact]
    public void TruncatedTitleEmptyForNilTitle()
        => Assert.Equal("", new ArticleStringFormatter().TruncatedTitle(MakeArticle(title: null)));

    [Fact]
    public void TruncatedTitleCachedResultIsStable()
    {
        var formatter = new ArticleStringFormatter();
        var article = MakeArticle(title: "Repeated Title");
        Assert.Equal(formatter.TruncatedTitle(article), formatter.TruncatedTitle(article));
    }

    [Fact]
    public void ForHtmlCollisionRegression()
    {
        var formatter = new ArticleStringFormatter();
        var article = MakeArticle(title: "Tom &amp; Jerry");
        Assert.Equal("Tom & Jerry", formatter.TruncatedTitle(article, forHtml: false));
        Assert.Equal("Tom &amp; Jerry", formatter.TruncatedTitle(article, forHtml: true));
    }

    [Fact]
    public void ForHtmlCollisionRegressionReverseOrder()
    {
        var formatter = new ArticleStringFormatter();
        var article = MakeArticle(title: "Tom &amp; Jerry");
        Assert.Equal("Tom &amp; Jerry", formatter.TruncatedTitle(article, forHtml: true));
        Assert.Equal("Tom & Jerry", formatter.TruncatedTitle(article, forHtml: false));
    }

    [Fact]
    public void AttributedTruncatedTitleNonEmpty()
        => Assert.Equal("Some Title", new ArticleStringFormatter().AttributedTruncatedTitle(MakeArticle(title: "Some Title")).Text);

    [Fact]
    public void AttributedTruncatedTitleEmptyForNilTitle()
        => Assert.Equal("", new ArticleStringFormatter().AttributedTruncatedTitle(MakeArticle(title: null)).Text);

    [Fact]
    public void AttributedTruncatedTitleReturnsCachedInstance()
    {
        var article = MakeArticle(title: "Cached");
        var formatter = new ArticleStringFormatter();
        Assert.Same(formatter.AttributedTruncatedTitle(article), formatter.AttributedTruncatedTitle(article));
    }

    [Fact]
    public void TruncatedSummaryFromContentHtml()
        => Assert.Equal("Hello World", new ArticleStringFormatter().TruncatedSummary(MakeArticle(contentHtml: "<p>Hello <b>World</b></p>")));

    [Fact]
    public void TruncatedSummaryEmptyForNoBody()
        => Assert.Equal("", new ArticleStringFormatter().TruncatedSummary(MakeArticle(contentHtml: null)));

    [Fact]
    public void TruncatedSummaryHackerNewsCommentsBecomesEmpty()
        => Assert.Equal("", new ArticleStringFormatter().TruncatedSummary(MakeArticle(contentHtml: "Comments")));

    [Fact]
    public void SummarySemanticsForEntityEncodedLiteralTags()
        => Assert.Equal("Use <i> for italics.", new ArticleStringFormatter().TruncatedSummary(MakeArticle(contentHtml: "<p>Use &lt;i&gt; for italics.</p>")));

    [Fact]
    public void SummarySemanticsForTypicalHtml()
        => Assert.Equal("Tom & Jerry", new ArticleStringFormatter().TruncatedSummary(MakeArticle(contentHtml: "<p>Tom &amp; Jerry</p>")));

    [Fact]
    public void DateStringTodayReturnsTimeString()
    {
        var now = DateTime.Now;
        var result = new ArticleStringFormatter().DateString(now);
        Assert.NotEmpty(result);
        Assert.DoesNotContain(now.Year.ToString(CultureInfo.CurrentCulture), result);
    }

    [Fact]
    public void DateStringPastReturnsNonEmpty()
        => Assert.NotEmpty(new ArticleStringFormatter().DateString(DateTime.UnixEpoch));

    [Fact]
    public void TruncatedFeedNameShortPassesThrough()
        => Assert.Equal("Daring Fireball", new ArticleStringFormatter().TruncatedFeedName("Daring Fireball"));

    [Fact]
    public void TruncatedFeedNameLongIsTruncated()
        => Assert.Equal(100, new ArticleStringFormatter().TruncatedFeedName(new string('x', 200)).Length);

    [Fact]
    public void SummaryCacheDoesNotMixAccountsOrKeepReplacedContent()
    {
        var formatter = new ArticleStringFormatter();
        Assert.Equal("First", formatter.TruncatedSummary(MakeArticle(contentHtml: "First")));
        Assert.Equal("Other", formatter.TruncatedSummary(MakeArticle(accountID: "other", contentHtml: "Other")));
        Assert.Equal("Updated", formatter.TruncatedSummary(MakeArticle(contentHtml: "Updated")));
    }

    [Fact]
    public void SummaryCacheDoesNotRetainAnOtherwiseUnreferencedArticleBody()
    {
        var formatter = new ArticleStringFormatter();
        var body = CacheLargeSummary(formatter);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(body.TryGetTarget(out _));
        GC.KeepAlive(formatter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<string> CacheLargeSummary(ArticleStringFormatter formatter)
    {
        var body = "<p>Summary prefix</p>" + new string('x', 1_000_000);
        Assert.StartsWith("Summary prefix", formatter.TruncatedSummary(MakeArticle(contentHtml: body)));
        return new(body);
    }

    [Fact]
    public void TruncationPreservesWholeGraphemes()
    {
        var text = string.Concat(Enumerable.Repeat("e\u0301\U0001f30d", 600));
        var formatter = new ArticleStringFormatter();
        Assert.Equal(1000, new StringInfo(formatter.TruncatedTitle(MakeArticle(title: text))).LengthInTextElements);
        Assert.Equal(100, new StringInfo(formatter.TruncatedFeedName(text)).LengthInTextElements);
    }

    private static Article MakeArticle(string? title = "Untitled", string? contentHtml = null, string accountID = "account1")
        => new(accountID, "article1", "feed1", "unique-article1", title, contentHtml, null, null, null, null, null, null,
            DateTime.UtcNow, null, null, null, new ArticleStatus("article1", false, false, DateTime.UtcNow));
}
