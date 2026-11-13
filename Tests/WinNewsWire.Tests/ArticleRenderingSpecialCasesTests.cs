using WinNewsWire.AppShared.ArticleRendering;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

/// <summary>
/// Smoke coverage for <see cref="ArticleRenderingSpecialCases"/>. Mirrors the
/// behaviour of NetNewsWire's <c>filterHTMLIfNeeded</c> step: only Verge URLs
/// get rewritten, and rewriting replaces the well-known mojibake sequences
/// with the intended Unicode characters.
/// </summary>
public class ArticleRenderingSpecialCasesTests
{
    [Theory]
    [InlineData("https://www.theverge.com/2024/1/2/foo", true)]
    [InlineData("https://theverge.com/", true)]
    [InlineData("http://blog.theverge.com/", true)]
    [InlineData("https://daringfireball.net/", false)]
    [InlineData("https://example.com/", false)]
    public void IsVergeHostMatches(string url, bool expected)
    {
        Assert.Equal(expected, ArticleRenderingSpecialCases.IsVergeSpecialCase(new System.Uri(url)));
    }

    [Fact]
    public void FilterHtmlIfNeededPassesThroughOnNonSpecialHost()
    {
        var html = "<p>â€™ stays â€™</p>";
        Assert.Equal(html,
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/", html));
    }

    [Fact]
    public void FilterHtmlIfNeededPassesThroughOnInvalidUrl()
    {
        var html = "<p>â€™ stays â€™</p>";
        Assert.Equal(html, ArticleRenderingSpecialCases.FilterHtmlIfNeeded(null, html));
        Assert.Equal(html, ArticleRenderingSpecialCases.FilterHtmlIfNeeded(string.Empty, html));
        Assert.Equal(html, ArticleRenderingSpecialCases.FilterHtmlIfNeeded("not a url", html));
    }

    [Fact]
    public void VergeRightSingleQuoteIsFixed()
    {
        Assert.Equal("Apple\u2019s",
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded(
                "https://www.theverge.com/foo", "Appleâ€™s"));
    }

    [Fact]
    public void VergeEntityEncodedQuotesAreFixed()
    {
        Assert.Equal("Apple\u2019s",
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded(
                "https://www.theverge.com/foo", "Apple&acirc;&#128;&#153;s"));
    }

    [Fact]
    public void VergeDoubleQuotesAndEmDashAreFixed()
    {
        var input = "He said â€œhelloâ€\u00a0â€\" goodbye";
        var output = ArticleRenderingSpecialCases.FilterVergeHtml(input);
        Assert.Contains("\u201Chello", output);
        Assert.DoesNotContain("â€œ", output);
    }

    [Fact]
    public void VergeDoubleEncodedEllipsisIsFixed()
    {
        Assert.Equal("And then\u2026",
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded(
                "https://www.theverge.com/foo", "And then &amp;hellip;"));
    }

    [Fact]
    public void StrayAcircCharsAreDropped()
    {
        Assert.Equal("hello world",
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded(
                "https://www.theverge.com/foo", "helloÂ world"));
    }

    [Fact]
    public void RedirectAssignmentIsRemoved()
    {
        var filtered = ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/post/",
            "<p>Before</p><script>location.href='https://example.com/full/';</script><p>After</p>");
        Assert.DoesNotContain("location.href", filtered);
        Assert.Contains("<p>Before</p>", filtered);
        Assert.Contains("<p>After</p>", filtered);
    }

    [Fact]
    public void SpacedAssignmentAndWindowLocationRemoved()
    {
        var filtered = ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/a/",
            "<script> window.location.href = 'https://example.com/x/' ;</script>KEEP");
        Assert.DoesNotContain("location.href", filtered);
        Assert.Contains("KEEP", filtered);
    }

    [Fact]
    public void ReadsAndComparisonsAreKept()
    {
        const string read = "<script>if (location.href.includes('x')) { doThing(); }</script>";
        const string compare = "<script>if (location.href == 'x') { doThing(); }</script>";
        Assert.Equal(read, ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/a/", read));
        Assert.Equal(compare, ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/a/", compare));
    }

    [Fact]
    public void ContentWithoutLocationHrefIsUnchanged()
    {
        const string html = "<p>Just an article.</p><script>console.log('hi');</script>";
        Assert.Equal(html, ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/a/", html));
    }

    [Fact]
    public void SlashdotSubdomainLinkDisablesJavaScript()
        => Assert.True(ArticleRenderingSpecialCases.ShouldDisableJavaScript([
            "https://yro.slashdot.org/story/26/09/18/0023251/will-california-gut-its-net-neutrality-law?utm_source=rss1.0mainlinkanon&utm_medium=feed", null, null]));

    [Fact]
    public void SlashdotRootLinkDisablesJavaScript()
        => Assert.True(ArticleRenderingSpecialCases.ShouldDisableJavaScript(["https://slashdot.org/", null, null]));

    [Fact]
    public void SlashdotFeedUrlDisablesJavaScriptWhenLinkIsNil()
        => Assert.True(ArticleRenderingSpecialCases.ShouldDisableJavaScript([null, "https://rss.slashdot.org/Slashdot/slashdotMain", null]));

    [Fact]
    public void OtherDomainKeepsJavaScript()
        => Assert.False(ArticleRenderingSpecialCases.ShouldDisableJavaScript([
            "https://example.com/post/", "https://example.com/feed.xml", "https://example.com/"]));

    [Fact]
    public void SimilarDomainKeepsJavaScript()
        => Assert.False(ArticleRenderingSpecialCases.ShouldDisableJavaScript(["https://notslashdot.org/story/", null, null]));

    [Fact]
    public void AllNilUrlsKeepJavaScript()
        => Assert.False(ArticleRenderingSpecialCases.ShouldDisableJavaScript([null, null, null]));

    [Fact]
    public void SlashdotArticleBodyGetsParagraphTags()
    {
        const string paragraph1 = "An anonymous reader quotes a report from Reuters: A growing number of people think social media manipulates and divides people and harms democracy, Pew Research Center said on Thursday, echoing rising concern and tighter scrutiny across the world.";
        const string paragraph2 = "Australia has proposed rules to let users opt out of algorithmically recommended feeds, after becoming the first country to ban social media for children under 16.";
        const string paragraph3 = "People in wealthier countries, including the US, Canada and UK, are more inclined to believe that social media is harming democracy, versus their counterparts in economies with lower gross domestic product per capita -- such as Ghana, Kenya and Nigeria.";
        const string shareLinks = "<p><div class=\"share_submission\" style=\"position:relative;\">\n"
            + "<a class=\"slashpop\" href=\"http://twitter.com/home?status=Social+Media+Harms+Democracy+By+Spreading+Rumors%2C+Survey+Shows%3A+https%3A%2F%2Ftech.slashdot.org%2Fstory%2F26%2F10%2F02%2F0632243%2F%3Futm_source%3Dtwitter%26utm_medium%3Dtwitter\"><img src=\"https://a.fsdn.com/sd/twitter_icon_large.png\"></a>\n"
            + "<a class=\"slashpop\" href=\"http://www.facebook.com/sharer.php?u=https%3A%2F%2Ftech.slashdot.org%2Fstory%2F26%2F10%2F02%2F0632243%2Fsocial-media-harms-democracy-by-spreading-rumors-survey-shows%3Futm_source%3Dslashdot%26utm_medium%3Dfacebook\"><img src=\"https://a.fsdn.com/sd/facebook_icon_large.png\"></a>";
        const string readMore = "</div></p><p><a href=\"https://tech.slashdot.org/story/26/10/02/0632243/social-media-harms-democracy-by-spreading-rumors-survey-shows?utm_source=rss1.0moreanon&amp;utm_medium=feed\">Read more of this story</a> at Slashdot.</p><iframe src=\"https://slashdot.org/slashdot-it.pl?op=discuss&amp;id=24112882&amp;smallembed=1\" style=\"height: 300px; width: 100%; border: none;\"></iframe>";
        var html = paragraph1 + "\n \n" + paragraph2 + "\n \n" + paragraph3 + shareLinks + "\n\n\n\n" + readMore;
        var expected = paragraph1 + "<p>" + paragraph2 + "<p>" + paragraph3 + shareLinks + "<p>" + readMore;
        Assert.Equal(expected, ArticleRenderingSpecialCases.InsertParagraphTagsIfNeeded(html, "https://rss.slashdot.org/Slashdot/slashdotMain", null));
    }

    [Fact]
    public void SlashdotHomePageUrlGetsParagraphTagsForProxiedFeed()
        => Assert.Equal("First paragraph.<p>Second paragraph.", ArticleRenderingSpecialCases.InsertParagraphTagsIfNeeded(
            "First paragraph.\n\nSecond paragraph.", "https://feeds.feedburner.com/Slashdot/slashdot", "https://slashdot.org/"));

    [Fact]
    public void OtherFeedsDontGetParagraphTags()
    {
        const string html = "First paragraph.\n\nSecond paragraph.";
        Assert.Equal(html, ArticleRenderingSpecialCases.InsertParagraphTagsIfNeeded(html, "https://example.com/feed.xml", "https://example.com/"));
    }

    [Fact]
    public void YoutubeArticleGetsNetNewsWireBaseUrl()
    {
        Assert.Equal("https://netnewswire.com/", ArticleRenderingSpecialCases.BaseUrlForRendering(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ")).AbsoluteUri);
        var hackaday = new Uri("https://hackaday.com/2025/12/01/necroprinting-isnt-as-bad-as-it-sounds/");
        Assert.Equal(hackaday, ArticleRenderingSpecialCases.BaseUrlForRendering(hackaday));
    }

    [Fact]
    public void MacRumorsListsLoseStrayBreaksButKeepSurroundingContent()
    {
        const string html = "Intro<br/><ul>\n<br/>\n<li>Item</li>\n<br/>\n</ul>After.";
        Assert.Equal("Intro<br/><ul>\n<li>Item</li>\n</ul>After.",
            ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(html, "https://feeds.macrumors.com/MacRumors-All", null));
    }

    [Fact]
    public void MacRumorsTablesKeepBreaksInsideCells()
    {
        const string html = "<table class=\"specs\">\n<br/>\n<tr>\n<br/>\n<th>Height</th>\n<br/>\n<td>closed<br/>open</td>\n<br/>\n</tr>\n<br/>\n</table><br/>After.";
        const string expected = "<table class=\"specs\">\n<tr>\n<th>Height</th>\n<td>closed<br/>open</td>\n</tr>\n</table><br/>After.";
        Assert.Equal(expected, ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(html, null, "https://www.macrumors.com/"));
    }

    [Theory]
    [InlineData("https://example.com/feed.xml")]
    [InlineData("https://notmacrumors.com/feed.xml")]
    public void OtherFeedsKeepStrayBreaks(string feedUrl)
    {
        const string html = "<ul><br/><li>Item</li><br/></ul>";
        Assert.Equal(html, ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(html, feedUrl, null));
    }

    [Fact]
    public void MacRumorsKeepsBreaksAtStartOfListItemsAndCells()
    {
        const string html = "<ul><li><br/>Item</li></ul><table><tr><td><br/>Cell</td></tr></table>";
        Assert.Equal(html, ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(html, "https://macrumors.com/", null));
        Assert.Equal("<UL><LI>Item</LI></UL>", ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(
            "<UL><BR /><br><LI>Item</LI><Br/></UL>", "https://macrumors.com/", null));
    }
}
