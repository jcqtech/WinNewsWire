using Xunit;
using System.Text;
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

    [Fact]
    public void DeeplyNestedFolders()
    {
        var doc = Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="1.0">
              <head><title>Nested</title></head>
              <body>
                <outline text="L1">
                  <outline text="L2">
                    <outline text="L3">
                      <outline text="feed" type="rss" xmlUrl="http://example.com/feed.xml"/>
                    </outline>
                  </outline>
                </outline>
              </body>
            </opml>
            """);
        var l1 = Assert.Single(doc.Children);
        Assert.Equal("L1", l1.Text);
        var l2 = Assert.Single(l1.Children);
        Assert.Equal("L2", l2.Text);
        var l3 = Assert.Single(l2.Children);
        Assert.Equal("L3", l3.Text);
        Assert.Equal("http://example.com/feed.xml", Assert.Single(l3.Children).FeedSpecifier?.FeedUrl);
    }

    [Fact]
    public void XmlUrlAttributeIsCaseInsensitive()
    {
        var doc = Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="1.0">
              <head><title>Mixed-case attributes</title></head>
              <body>
                <outline text="camelCase" type="rss" xmlUrl="http://example.com/a.xml"/>
                <outline text="lowercase" type="rss" xmlurl="http://example.com/b.xml"/>
                <outline text="UPPERCASE" type="rss" XMLURL="http://example.com/c.xml"/>
                <outline text="MixedCase" type="rss" XmlUrl="http://example.com/d.xml"/>
              </body>
            </opml>
            """);
        var urls = doc.Children.Select(c => c.FeedSpecifier?.FeedUrl).ToList();
        Assert.Contains("http://example.com/a.xml", urls);
        Assert.Contains("http://example.com/b.xml", urls);
        Assert.Contains("http://example.com/c.xml", urls);
        Assert.Contains("http://example.com/d.xml", urls);
    }

    [Fact]
    public void NonFeedOutlinesParseAsFolders()
    {
        var doc = Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <opml version="1.0">
              <head><title>Mixed</title></head>
              <body>
                <outline text="Just a note"/>
                <outline text="A folder">
                  <outline text="Real feed" type="rss" xmlUrl="http://example.com/feed.xml"/>
                </outline>
                <outline text="Another note"/>
              </body>
            </opml>
            """);
        Assert.Equal(3, doc.Children.Count);
        Assert.Null(doc.Children[0].FeedSpecifier);
        Assert.Null(doc.Children[2].FeedSpecifier);
        Assert.Null(doc.Children[1].FeedSpecifier);
        Assert.Equal("http://example.com/feed.xml", Assert.Single(doc.Children[1].Children).FeedSpecifier?.FeedUrl);
    }

    [Fact]
    public void UnknownElementDoesNotSkipFollowingOutline()
    {
        var doc = Parse("""
            <opml><body><metadata><value>ignored</value></metadata><outline text="Feed" xmlUrl="https://example.com/feed"/></body></opml>
            """);
        Assert.Equal("https://example.com/feed", Assert.Single(doc.Children).FeedSpecifier?.FeedUrl);
    }

    [Fact]
    public void DocumentTitleDoesNotSkipFollowingBody()
    {
        var doc = Parse("""
            <opml><title>Subscriptions</title><body><outline text="Feed" xmlUrl="https://example.com/feed"/></body></opml>
            """);
        Assert.Equal("Subscriptions", doc.Title);
        Assert.Single(doc.Children);
    }

    [Fact]
    public void OutlineTitleDoesNotOverwriteDocumentTitle()
    {
        var doc = Parse("""
            <opml><head><title>Subscriptions</title></head><body>
              <outline text="Folder"><title>Not the document</title><outline text="Feed" xmlUrl="https://example.com/feed"/></outline>
            </body></opml>
            """);
        Assert.Equal("Subscriptions", doc.Title);
        Assert.Equal("https://example.com/feed", Assert.Single(Assert.Single(doc.Children).Children).FeedSpecifier?.FeedUrl);
    }

    [Fact]
    public void QualifiedAttributesArePreserved()
    {
        var doc = Parse("""<opml xmlns:x="urn:custom"><body><outline text="Feed" xmlUrl="https://example.com/feed" x:value="custom"/></body></opml>""");
        Assert.Equal("custom", Assert.Single(doc.Children).Attributes["x:value"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<opml><body><outline></body></opml>")]
    [InlineData("<opml><body></opml>")]
    [InlineData("<opml/><opml/>")]
    public void InvalidXmlIsReported(string xml)
        => Assert.Throws<FeedParserException>(() => Parse(xml));

    private static OpmlDocument Parse(string xml)
        => OpmlParser.Parse(new ParserData("http://example.com/", Encoding.UTF8.GetBytes(xml)));

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
