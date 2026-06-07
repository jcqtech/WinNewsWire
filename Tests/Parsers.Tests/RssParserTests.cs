using System.Linq;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>RSSParserTests.swift</c>. Performance tests are omitted.</summary>
public class RssParserTests
{
    private static ParserData Load(string f, string ext, string url) => TestResources.Load($"{f}.{ext}", url);

    [Fact]
    public void NatashaTheRobotItemCount()
    {
        var feed = FeedParser.Parse(Load("natasha", "xml", "https://www.natashatherobot.com/"))!;
        Assert.Equal(10, feed.Items.Count);
    }

    [Fact]
    public void TheOmniShowAttachments()
    {
        var feed = FeedParser.Parse(Load("theomnishow", "rss", "https://theomnishow.omnigroup.com/"))!;
        foreach (var a in feed.Items)
        {
            Assert.NotNull(a.Attachments);
            Assert.Single(a.Attachments!);
            var att = a.Attachments!.First();
            Assert.NotNull(att.MimeType);
            Assert.NotNull(att.SizeInBytes);
            Assert.Contains("cloudfront", att.Url);
            Assert.True(att.SizeInBytes >= 22275279);
            Assert.Equal("audio/mpeg", att.MimeType);
        }
    }

    [Fact]
    public void TheOmniShowUniqueIds()
    {
        var feed = FeedParser.Parse(Load("theomnishow", "rss", "https://theomnishow.omnigroup.com/"))!;
        foreach (var a in feed.Items)
            Assert.StartsWith("https://theomnishow.omnigroup.com/episode/", a.UniqueId);
    }

    [Fact]
    public void MacworldUniqueIdsAreMd5()
    {
        var feed = FeedParser.Parse(Load("macworld", "rss", "https://www.macworld.com/"))!;
        foreach (var a in feed.Items)
            Assert.Equal(32, a.UniqueId.Length);
    }

    [Fact]
    public void MacworldAuthorsHaveNamesOnly()
    {
        var feed = FeedParser.Parse(Load("macworld", "rss", "https://www.macworld.com/"))!;
        foreach (var a in feed.Items)
        {
            var author = a.Authors!.First();
            Assert.Null(author.EmailAddress);
            Assert.Null(author.Url);
            Assert.NotNull(author.Name);
        }
    }

    [Fact]
    public void EmptyContentEncodedIsIgnored()
    {
        var feed = FeedParser.Parse(Load("atp", "rss", "http://atp.fm/"))!;
        foreach (var a in feed.Items)
            Assert.NotNull(a.ContentHtml);
    }

    [Fact]
    public void LivemintGuidsNotPermalinks()
    {
        var feed = FeedParser.Parse(Load("livemint", "xml", "https://www.livemint.com/rss/news"))!;
        foreach (var a in feed.Items)
            Assert.Null(a.Url);
    }

    [Fact]
    public void AktualityTitlesPresent()
    {
        var feed = FeedParser.Parse(Load("aktuality", "rss", "https://www.aktuality.sk/"))!;
        foreach (var a in feed.Items) Assert.NotNull(a.Title);
    }

    [Fact]
    public void MantonFeedLanguage()
    {
        var feed = FeedParser.Parse(Load("manton", "rss", "http://manton.org/"))!;
        Assert.Equal("en-US", feed.Language);
    }

    [Fact]
    public void KatieFloydIconUrl()
    {
        var feed = FeedParser.Parse(Load("KatieFloyd", "rss", "http://katiefloyd.com/"))!;
        Assert.Equal("https://static.feedpress.it/logo/katiefloyd.png", feed.IconUrl);
    }

    [Fact]
    public void AktualityIconNotSetFromItemImages()
    {
        var feed = FeedParser.Parse(Load("aktuality", "rss", "https://www.aktuality.sk/"))!;
        Assert.Null(feed.IconUrl);
    }

    [Fact]
    public void MedscapeExternalUrls()
    {
        var feed = FeedParser.Parse(Load("medscape", "rss", "https://www.medscape.com/cx/rssfeeds/2674.xml"))!;
        foreach (var a in feed.Items) Assert.NotNull(a.ExternalUrl);
    }

    [Fact]
    public void CloudblogAuthorTitleNotUsedAsItemTitle()
    {
        var feed = FeedParser.Parse(Load("cloudblog", "rss", "https://cloudblog.withgoogle.com/"))!;
        foreach (var a in feed.Items)
        {
            Assert.NotEqual("Product Manager, Office of the CTO", a.Title);
            Assert.NotEqual("Developer Programs Engineer", a.Title);
            Assert.NotEqual("Product Director", a.Title);
        }
    }

    [Fact]
    public void MonkeydomGuidsThatArentPermalinks()
    {
        var feed = FeedParser.Parse(Load("monkeydom", "rss", "https://coding.monkeydom.de/"))!;
        foreach (var a in feed.Items)
        {
            Assert.Null(a.Url);
            Assert.NotNull(a.UniqueId);
        }
    }

    // Port of Swift `testMarkdown1`. RSS feeds carrying source:markdown should
    // populate ParsedItem.Markdown for every item.
    [Fact]
    public void Markdown1()
    {
        var feed = FeedParser.Parse(Load("markdown1", "rss",
            "https://wordland.social/api/feed/2025/04/14/RklIWmJTdjJzMGl2RVlpajdEZS9SQT09.rss"))!;
        Assert.NotEmpty(feed.Items);
        foreach (var a in feed.Items)
            Assert.NotNull(a.Markdown);
    }

    // Port of Swift `testMarkdown2`. Same as Markdown1 with a different feed.
    [Fact]
    public void Markdown2()
    {
        var feed = FeedParser.Parse(Load("markdown2", "rss",
            "https://wordland.social/api/feed/2025/04/14/M0NlNTVISFhCNUgwYldKNlpsR2Y1Zz09.rss"))!;
        Assert.NotEmpty(feed.Items);
        foreach (var a in feed.Items)
            Assert.NotNull(a.Markdown);
    }
}
