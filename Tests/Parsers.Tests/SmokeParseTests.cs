using System.IO;
using System.Linq;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

public class SmokeParseTests
{
    private static ParserData Load(string filename, string ext, string url)
    {
        var path = Path.Combine(TestResources.ResourcesDir, filename + "." + ext);
        return new ParserData(url, File.ReadAllBytes(path));
    }

    [Fact]
    public void ParsesDaringFireballAtom()
    {
        var feed = FeedParser.Parse(Load("DaringFireball", "rss", "http://daringfireball.net/"));
        Assert.NotNull(feed);
        Assert.Equal(FeedType.Atom, feed!.Type);
        Assert.NotEmpty(feed.Items);
    }

    [Fact]
    public void ParsesScriptingNewsRss()
    {
        var feed = FeedParser.Parse(Load("scriptingNews", "rss", "http://scripting.com/"));
        Assert.NotNull(feed);
        Assert.Equal(FeedType.Rss, feed!.Type);
        Assert.NotEmpty(feed.Items);
    }

    [Fact]
    public void ParsesInessentialJsonFeed()
    {
        var feed = FeedParser.Parse(Load("inessential", "json", "http://inessential.com/"));
        Assert.NotNull(feed);
        Assert.Equal(FeedType.JsonFeed, feed!.Type);
        Assert.NotEmpty(feed.Items);
    }

    [Fact]
    public void ParsesScriptingNewsRssInJson()
    {
        var feed = FeedParser.Parse(Load("ScriptingNews", "json", "http://scripting.com/"));
        Assert.NotNull(feed);
        Assert.Equal(FeedType.RssInJson, feed!.Type);
        Assert.NotEmpty(feed.Items);
    }

    [Fact]
    public void DetectsRssItemFieldsOnScriptingNews()
    {
        var feed = FeedParser.Parse(Load("scriptingNews", "rss", "http://scripting.com/"))!;
        Assert.All(feed.Items, i => Assert.False(string.IsNullOrEmpty(i.UniqueId)));
    }
}
