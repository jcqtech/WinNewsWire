using System.Linq;
using System.Text;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>HTMLMetadataTests.swift</c>.</summary>
public class HtmlMetadataTests
{
    [Fact]
    public void RepeatedHtmlAttributeLookupsDoNotAllocateUtf8Arrays()
    {
        var attributes = new HtmlAttributes([("src"u8.ToArray(), "photo.jpg")]);
        for (int index = 0; index < 32; index++) _ = attributes["SrC"];
        long before = GC.GetAllocatedBytesForCurrentThread();
        string? value = null;
        for (int index = 0; index < 1000; index++) value = attributes["SrC"];
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("photo.jpg", value);
        Assert.InRange(allocated, 0, 128);
    }

    [Fact]
    public void LongUtf8AttributeNamesKeepCaseInsensitiveAsciiLookup()
    {
        string name = "DATA-" + new string('x', 300) + "\u00e9";
        var attributes = new HtmlAttributes([(Encoding.UTF8.GetBytes(name), "value")]);
        Assert.Equal("value", attributes[name.ToLowerInvariant()]);
        Assert.Null(attributes["missing"]);
    }

    private static HtmlMetadata Parse(string f, string url) =>
        HtmlMetadataParser.Parse(TestResources.Load($"{f}.html", url));

    [Fact]
    public void DaringFireball()
    {
        var m = Parse("DaringFireball", "http://daringfireball.net/");
        Assert.Equal("http://daringfireball.net/graphics/favicon.ico?v=005", m.FaviconLink);
        Assert.Single(m.FeedLinks);
        var link = m.FeedLinks[0];
        Assert.Null(link.Title);
        Assert.Equal("application/atom+xml", link.Type);
        Assert.Equal("http://daringfireball.net/feeds/main", link.Url);
    }

    [Fact]
    public void Furbo()
    {
        var m = Parse("furbo", "http://furbo.org/");
        Assert.Equal("http://furbo.org/favicon.ico", m.FaviconLink);
        Assert.Single(m.FeedLinks);
        var link = m.FeedLinks[0];
        Assert.Equal("Iconfactory News Feed", link.Title);
        Assert.Equal("application/rss+xml", link.Type);
    }

    [Fact]
    public void Inessential()
    {
        var m = Parse("inessential", "http://inessential.com/");
        Assert.Null(m.FaviconLink);
        Assert.Single(m.FeedLinks);
        var link = m.FeedLinks[0];
        Assert.Equal("RSS", link.Title);
        Assert.Equal("application/rss+xml", link.Type);
        Assert.Equal("http://inessential.com/xml/rss.xml", link.Url);
        Assert.Empty(m.AppleTouchIcons);
    }

    [Fact]
    public void SixColors()
    {
        var m = Parse("sixcolors", "http://sixcolors.com/");
        Assert.Equal("https://sixcolors.com/images/favicon.ico", m.FaviconLink);
        Assert.Single(m.FeedLinks);
        var link = m.FeedLinks[0];
        Assert.Equal("RSS", link.Title);
        Assert.Equal("application/rss+xml", link.Type);
        Assert.Equal("http://feedpress.me/sixcolors", link.Url);
        Assert.Equal(6, m.AppleTouchIcons.Count);
        var icon = m.AppleTouchIcons[3];
        Assert.Equal("apple-touch-icon", icon.Rel);
        Assert.Equal("120x120", icon.Sizes);
        Assert.Equal("https://sixcolors.com/apple-touch-icon-120.png", icon.Url);
    }

    [Fact]
    public void CocoOpenGraphImage()
    {
        var m = Parse("coco", "https://www.theatlantic.com/entertainment/archive/2017/11/coco-is-among-pixars-best-movies-in-years/546695/");
        Assert.Equal("https://cdn.theatlantic.com/assets/media/img/mt/2017/11/1033101_first_full_length_trailer_arrives_pixars_coco/facebook.jpg?1511382177",
            m.OpenGraphImageUrl);
    }

    [Fact]
    public void CocoTwitterImage()
    {
        var m = Parse("coco", "https://www.theatlantic.com/entertainment/archive/2017/11/coco-is-among-pixars-best-movies-in-years/546695/");
        Assert.Equal("https://cdn.theatlantic.com/assets/media/img/mt/2017/11/1033101_first_full_length_trailer_arrives_pixars_coco/facebook.jpg?1511382177",
            m.TwitterImageUrl);
    }

    [Fact]
    public void YouTubeFeedLinkInBody()
    {
        var m = Parse("YouTubeTheVolvoRocks", "https://www.youtube.com/user/TheVolvorocks");
        Assert.Single(m.FeedLinks);
        var link = m.FeedLinks[0];
        Assert.Equal("RSS", link.Title);
        Assert.Equal("application/rss+xml", link.Type);
        Assert.Equal("https://www.youtube.com/feeds/videos.xml?channel_id=UCct7QF2jcWRY6dhXWMSq9LQ", link.Url);
    }

    [Fact]
    public void BothRssAndAtomFeedLinksReported()
    {
        var metadata = ParseInline("""
            <html><head>
            <link rel="alternate" type="application/rss+xml" title="RSS" href="/feed.rss">
            <link rel="alternate" type="application/atom+xml" title="Atom" href="/feed.atom">
            </head><body></body></html>
            """);
        Assert.Equal(2, metadata.FeedLinks.Count);
        Assert.True(new HashSet<string?> { "application/rss+xml", "application/atom+xml" }.SetEquals(metadata.FeedLinks.Select(link => link.Type)));
    }

    [Fact]
    public void PageWithNoHeadReturnsEmptyMetadata()
    {
        var metadata = ParseInline("<html><body>Hello world</body></html>");
        Assert.Empty(metadata.FeedLinks);
        Assert.Empty(metadata.Favicons);
        Assert.Empty(metadata.AppleTouchIcons);
    }

    [Fact]
    public void RelativeFaviconResolvedAgainstPageUrl()
    {
        var metadata = ParseInline("""
            <html><head>
            <link rel="icon" href="/favicon.ico">
            <link rel="apple-touch-icon" sizes="60x60" href="/touch-60.png">
            <link rel="apple-touch-icon" sizes="120x120" href="/touch-120.png">
            </head><body></body></html>
            """, "http://example.com/some/page.html");
        Assert.Equal("http://example.com/favicon.ico", metadata.FaviconLink);
        Assert.Equal(2, metadata.AppleTouchIcons.Count);
        Assert.True(new HashSet<string?> { "60x60", "120x120" }.SetEquals(metadata.AppleTouchIcons.Select(icon => icon.Sizes)));
        Assert.True(new HashSet<string?> { "http://example.com/touch-60.png", "http://example.com/touch-120.png" }.SetEquals(metadata.AppleTouchIcons.Select(icon => icon.Url)));
    }

    [Fact]
    public void TypelessFeedLinkAccepted()
    {
        var metadata = ParseInline("""
            <html><head>
            <link rel="alternate" href="/feed.xml">
            </head><body></body></html>
            """);
        var link = Assert.Single(metadata.FeedLinks);
        Assert.Equal("http://example.com/feed.xml", link.Url);
        Assert.Null(link.Type);
    }

    [Fact]
    public void NonFeedTypedAlternateRejected()
        => Assert.Empty(ParseInline("""
            <html><head>
            <link rel="alternate" hreflang="fr" type="text/html" href="/fr/page.html">
            </head><body></body></html>
            """).FeedLinks);

    [Fact]
    public void UnquotedAttributeValues()
    {
        var metadata = ParseInline("""
            <html><head>
            <link rel=alternate type=application/rss+xml title="ranchero.com RSS feed" href=https://ranchero.com/xml/rss.xml>
            <link rel=alternate type=application/json title="ranchero.com JSON feed" href=https://ranchero.com/feed.json>
            </head><body></body></html>
            """, "https://ranchero.com/");
        Assert.Equal(2, metadata.FeedLinks.Count);
        var rss = Assert.Single(metadata.FeedLinks, link => link.Type == "application/rss+xml");
        Assert.Equal("https://ranchero.com/xml/rss.xml", rss.Url);
        Assert.Equal("ranchero.com RSS feed", rss.Title);
    }

    [Fact]
    public void NonYoutubePagesStopAtBodyAndIgnoreEmbeddedMarkup()
    {
        var metadata = ParseInline("""
            <head><script>var tag = '<link rel="alternate" href="/not-a-feed">';</script></head>
            <body><link rel="alternate" href="/body-feed"></body>
            """);
        Assert.Empty(metadata.FeedLinks);
    }

    [Fact]
    public void OpenGraphImagesRetainTheirAssociatedProperties()
    {
        var metadata = ParseInline("""
            <head>
            <meta property="og:image:secure_url" content="https://example.com/one">
            <meta property="og:image" content="http://example.com/one">
            <meta property="og:image:type" content="image/jpeg">
            <meta property="og:image:width" content="640">
            <meta property="og:image:height" content="480">
            <meta property="og:image:alt" content="One &amp; two">
            <meta property="og:image" content="http://example.com/two">
            </head>
            """);
        Assert.Equal(2, metadata.OpenGraphProperties.Images.Count);
        Assert.Equal(new HtmlOpenGraphImage("http://example.com/one", "https://example.com/one", "image/jpeg", 640, 480, "One & two"),
            metadata.OpenGraphProperties.Images[0]);
        Assert.Equal("http://example.com/two", metadata.OpenGraphProperties.Images[1].Url);
    }

    [Fact]
    public void FaviconsAreDeduplicatedAndTouchIconSizesAreParsed()
    {
        var metadata = ParseInline("""
            <head><LINK REL="shortcut ICON" HREF="/icon"><link rel="icon" href="/icon">
            <link rel="apple-touch-icon" sizes="120x120" href="/touch"></head>
            """);
        Assert.Equal("http://example.com/icon", Assert.Single(metadata.Favicons).Url);
        Assert.Equal(new HtmlImageSize(120, 120), Assert.Single(metadata.AppleTouchIcons).Size);
    }

    private static HtmlMetadata ParseInline(string html, string url = "http://example.com/")
        => HtmlMetadataParser.Parse(new(url, Encoding.UTF8.GetBytes(html)));
}
