using System.Text;
using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.FeedFinder.Tests;

public class FeedFinderTests
{
    [Fact]
    public void Example() => Assert.NotNull(new FeedFinder());

    [Fact]
    public void KnownFeedSpecifierForRelayFMBlog()
        => Assert.Equal("https://www.relay.fm/blog/feed",
            FeedSpecifier.KnownFeedSpecifier(new Uri("https://www.relay.fm/blog"))?.UrlString);

    [Fact]
    public void KnownFeedSpecifierIgnoresRelayFMRoot()
        => Assert.Null(FeedSpecifier.KnownFeedSpecifier(new Uri("https://www.relay.fm")));

    [Fact]
    public void BodyLinksWithNonFeedExtensionsAreIgnored()
    {
        var urls = new[]
        {
            "https://example.com/feed.xml", "https://example.com/feed.PDF",
            "https://example.com/feed.pdf?download=1", "https://example.com/rss.jpg#top"
        };
        var html = "<html><body>" + string.Concat(urls.Select(url => $"<a href=\"{url}\">Link</a>")) + "</body></html>";
        var found = HtmlFeedFinder.FindIn(new ParserData("https://example.com/", Encoding.UTF8.GetBytes(html)));
        Assert.Equal(new HashSet<string> { "https://example.com/feed.xml" }, found.Select(f => f.UrlString).ToHashSet());
    }
}
