using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>OPMLTests.swift</c>.</summary>
public class OpmlTests
{
    [Fact]
    public void SubsStructure()
    {
        var doc = OpmlParser.Parse(TestResources.Load("Subs.opml", "http://example.org/"));
        Assert.Equal("Subs", doc.Title);
        Assert.Equal("http://example.org/", doc.Url);
        RecursivelyCheck(doc);
    }

    [Fact]
    public void FindingTitlesWithoutTitleAttribute()
    {
        var doc = OpmlParser.Parse(TestResources.Load("SubsNoTitleAttributes.opml", "http://example.org/"));
        RecursivelyCheck(doc);
    }

    // Port of Swift `testNotOPML`. Feeding non-OPML XML (an RSS/Atom file)
    // must surface as an exception rather than silently producing an empty
    // document so callers don't accept garbage subscriptions.
    [Fact]
    public void NotOpml()
    {
        Assert.Throws<FeedParserException>(() =>
            OpmlParser.Parse(TestResources.Load("DaringFireball.rss", "http://daringfireball.net/")));
    }

    private static void RecursivelyCheck(OpmlItem item)
    {
        if (item is not OpmlDocument)
            Assert.NotNull(item.Text);

        bool isFolder = item.Children.Count > 0;
        if (!isFolder && item.Title == "Skip") isFolder = true;

        if (!isFolder)
        {
            Assert.NotNull(item.FeedSpecifier);
            Assert.NotNull(item.FeedSpecifier!.Title);
            Assert.NotNull(item.FeedSpecifier.FeedUrl);
        }
        else
        {
            Assert.Null(item.FeedSpecifier);
        }

        foreach (var c in item.Children) RecursivelyCheck(c);
    }
}
