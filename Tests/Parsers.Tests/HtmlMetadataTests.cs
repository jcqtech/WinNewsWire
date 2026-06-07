using System.Linq;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>HTMLMetadataTests.swift</c>.</summary>
public class HtmlMetadataTests
{
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
        var m = Parse("coco", "https://www.theatlantic.com/");
        Assert.Equal("https://cdn.theatlantic.com/assets/media/img/mt/2017/11/1033101_first_full_length_trailer_arrives_pixars_coco/facebook.jpg?1511382177",
            m.OpenGraphImageUrl);
    }

    [Fact]
    public void CocoTwitterImage()
    {
        var m = Parse("coco", "https://www.theatlantic.com/");
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
}
