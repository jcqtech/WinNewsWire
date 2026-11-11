using System.IO;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

public class FeedParserTypeTests
{
    private static ParserData Load(string filename, string ext, string url)
    {
        var path = Path.Combine(TestResources.ResourcesDir, filename + "." + ext);
        return new ParserData(url, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("DaringFireball", "html", "http://daringfireball.net/")]
    [InlineData("furbo", "html", "http://furbo.org/")]
    [InlineData("inessential", "html", "http://inessential.com/")]
    [InlineData("sixcolors", "html", "https://sixcolors.com/")]
    public void HtmlIsNotAFeed(string n, string e, string u)
        => Assert.Equal(FeedType.NotAFeed, FeedTypeDetector.Detect(Load(n, e, u)));

    [Theory]
    [InlineData("EMarley", "rss", "https://medium.com/@emarley")]
    [InlineData("scriptingNews", "rss", "http://scripting.com/")]
    [InlineData("KatieFloyd", "rss", "https://katiefloyd.com/")]
    [InlineData("manton", "rss", "http://manton.org/")]
    [InlineData("dcrainmaker", "xml", "https://www.dcrainmaker.com/")]
    [InlineData("macworld", "rss", "https://www.macworld.com/")]
    [InlineData("natasha", "xml", "https://www.natashatherobot.com/")]
    [InlineData("donthitsave", "xml", "http://donthitsave.com/donthitsavefeed.xml")]
    [InlineData("bio", "rdf", "http://connect.biorxiv.org/")]
    [InlineData("phpxml", "rss", "https://www.fcutrecht.net/")]
    public void DetectsRss(string n, string e, string u)
        => Assert.Equal(FeedType.Rss, FeedTypeDetector.Detect(Load(n, e, u)));

    [Theory]
    [InlineData("DaringFireball", "rss", "http://daringfireball.net/")]
    [InlineData("OneFootTsunami", "atom", "http://onefoottsunami.com/")]
    [InlineData("russcox", "atom", "https://research.swtch.com/")]
    public void DetectsAtom(string n, string e, string u)
        => Assert.Equal(FeedType.Atom, FeedTypeDetector.Detect(Load(n, e, u)));

    [Fact]
    public void DetectsRssInJson()
        => Assert.Equal(FeedType.RssInJson, FeedTypeDetector.Detect(Load("ScriptingNews", "json", "http://scripting.com/")));

    [Theory]
    [InlineData("inessential", "http://inessential.com/")]
    [InlineData("allthis", "http://leancrew.com/allthis/")]
    [InlineData("curt", "http://curtclifton.net/")]
    [InlineData("pxlnv", "http://pxlnv.com/")]
    [InlineData("rose", "https://www.rosemaryorchard.com/")]
    public void DetectsJsonFeed(string n, string u)
        => Assert.Equal(FeedType.JsonFeed, FeedTypeDetector.Detect(Load(n, "json", u)));

    [Fact]
    public void PartialAllThisIsUnknown()
    {
        var d = Load("allthis-partial", "json", "http://leancrew.com/allthis/");
        Assert.Equal(FeedType.Unknown, FeedTypeDetector.Detect(d, isPartialData: true));
    }

    [Theory]
    [InlineData("<channel><pubDate>today</pubDate></channel>", FeedType.Rss)]
    [InlineData("<feed><title>Old Atom</title></feed>", FeedType.Atom)]
    [InlineData("{\"title\":\"jsonfeed.org is not a version\"}", FeedType.NotAFeed)]
    [InlineData("{\"rss\":{\"channel\":{}}}", FeedType.NotAFeed)]
    [InlineData("{\"version\":\"https:\\/\\/jsonfeed.org\\/version\\/1\"}", FeedType.JsonFeed)]
    public void NativeDetectionFallbacks(string content, FeedType expected)
    {
        var data = new ParserData("https://example.com/", System.Text.Encoding.UTF8.GetBytes(content.PadRight(128)));
        Assert.Equal(expected, FeedTypeDetector.Detect(data));
    }

    [Fact]
    public async Task AsyncParsingUsesTheSameOutputAndSurfacesErrors()
    {
        var data = Load("qemu", "atom", "https://www.qemu.org/feed.xml");
        var actual = await FeedParser.ParseAsync(data);
        Assert.NotNull(actual);
        Assert.Equal(FeedParser.Parse(data)!.Items, actual.Items);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FeedParser.ParseAsync(data, cancellation.Token));
        Assert.True(FeedParser.MightBeAbleToParseBasedOnPartialData(new ParserData(data.Url, [1, 2, 3])));
    }
}
