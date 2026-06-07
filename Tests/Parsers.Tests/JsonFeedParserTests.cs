using System.Linq;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>JSONFeedParserTests.swift</c>.</summary>
public class JsonFeedParserTests
{
    private static ParserData Load(string f, string url) => TestResources.Load($"{f}.json", url);

    [Fact]
    public void DaringFireballIconUrls()
    {
        var f = FeedParser.Parse(Load("DaringFireball", "http://daringfireball.net/"))!;
        Assert.Equal("https://daringfireball.net/graphics/favicon-64.png", f.FaviconUrl);
        Assert.Equal("https://daringfireball.net/graphics/apple-touch-icon.png", f.IconUrl);
    }

    [Fact]
    public void AllThisItemCount()
    {
        var f = FeedParser.Parse(Load("allthis", "http://leancrew.com/allthis/"))!;
        Assert.Equal(12, f.Items.Count);
    }

    [Fact]
    public void CurtContainsTwitterQuitter()
    {
        var f = FeedParser.Parse(Load("curt", "http://curtclifton.net/"))!;
        Assert.Equal(26, f.Items.Count);
        Assert.Contains(f.Items,
            i => i.Title == "Twitter Quitter"
              && i.ContentHtml!.StartsWith("<p>I&#8217;ve decided to close my Twitter account. William Van Hecke <a href=\"https://tinyletter.com/fet/letters/microcosmographia-xlxi-reasons-to-stay-on-twitter\">makes a convincing case</a>"));
    }

    [Fact]
    public void PxlnvItemCount() =>
        Assert.Equal(20, FeedParser.Parse(Load("pxlnv", "http://pxlnv.com/"))!.Items.Count);

    [Fact]
    public void RoseItemCount() =>
        Assert.Equal(84, FeedParser.Parse(Load("rose", "http://www.rosemaryorchard.com/"))!.Items.Count);

    [Fact]
    public void ThreeSixtyLanguage()
    {
        var f = FeedParser.Parse(Load("3960", "http://journal.3960.org/"))!;
        Assert.Equal(20, f.Items.Count);
        Assert.Equal("de-DE", f.Language);
        foreach (var i in f.Items) Assert.Equal("de-DE", i.Language);
    }

    [Fact]
    public void AuthorsResolution()
    {
        var f = FeedParser.Parse(Load("authors", "https://example.com/"))!;
        Assert.Equal(4, f.Items.Count);

        Assert.NotNull(f.Authors);
        Assert.Equal(2, f.Authors!.Count);
        var rootNames = f.Authors.Select(a => a.Name).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "Root Author 1", "Root Author 2" }, rootNames);

        var noAuthors = f.Items.Single(i => i.UniqueId == "Item without authors");
        Assert.Null(noAuthors.Authors);

        var legacy = f.Items.Single(i => i.UniqueId == "Item with legacy author");
        Assert.Equal(new[] { "Legacy Item Author" },
            legacy.Authors!.Select(a => a.Name).ToArray());

        string[] ItemNames(ParsedItem i) =>
            i.Authors!.Select(a => a.Name!).OrderBy(s => s).ToArray();

        var modern = f.Items.Single(i => i.UniqueId == "Item with modern authors");
        Assert.Equal(new[] { "Item Author 1", "Item Author 2" }, ItemNames(modern));

        var both = f.Items.Single(i => i.UniqueId == "Item with both");
        Assert.Equal(new[] { "Item Author 1", "Item Author 2" }, ItemNames(both));
    }
}
