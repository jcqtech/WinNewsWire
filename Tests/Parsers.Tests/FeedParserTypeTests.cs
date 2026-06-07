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
    [InlineData("EMarley", "rss")]
    [InlineData("scriptingNews", "rss")]
    [InlineData("KatieFloyd", "rss")]
    [InlineData("manton", "rss")]
    [InlineData("dcrainmaker", "xml")]
    [InlineData("macworld", "rss")]
    [InlineData("natasha", "xml")]
    [InlineData("donthitsave", "xml")]
    [InlineData("bio", "rdf")]
    [InlineData("phpxml", "rss")]
    public void DetectsRss(string n, string e)
        => Assert.Equal(FeedType.Rss, FeedTypeDetector.Detect(Load(n, e, "https://example.com/")));

    [Theory]
    [InlineData("DaringFireball", "rss")]
    [InlineData("OneFootTsunami", "atom")]
    [InlineData("russcox", "atom")]
    public void DetectsAtom(string n, string e)
        => Assert.Equal(FeedType.Atom, FeedTypeDetector.Detect(Load(n, e, "https://example.com/")));

    [Fact]
    public void DetectsRssInJson()
        => Assert.Equal(FeedType.RssInJson, FeedTypeDetector.Detect(Load("ScriptingNews", "json", "http://scripting.com/")));

    [Theory]
    [InlineData("inessential")]
    [InlineData("allthis")]
    [InlineData("curt")]
    [InlineData("pxlnv")]
    [InlineData("rose")]
    public void DetectsJsonFeed(string n)
        => Assert.Equal(FeedType.JsonFeed, FeedTypeDetector.Detect(Load(n, "json", "https://example.com/")));

    [Fact]
    public void PartialAllThisIsUnknown()
    {
        var d = Load("allthis-partial", "json", "http://leancrew.com/allthis/");
        Assert.Equal(FeedType.Unknown, FeedTypeDetector.Detect(d, isPartialData: true));
    }
}
