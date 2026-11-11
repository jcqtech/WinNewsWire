using Xunit;
using System.Text;
using WinNewsWire.Parsers.Utilities;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>RSSInJSONParserTests.swift</c>.</summary>
public class RssInJsonParserTests
{
    [Fact]
    public void ScriptingNewsLanguage()
    {
        var f = FeedParser.Parse(TestResources.Load("ScriptingNews.json", "http://scripting.com/"))!;
        Assert.Equal("en-us", f.Language);
    }

    [Fact]
    public void CalculatedIdUsesTheNativeDoubleTimestampString()
    {
        var feed = RssInJsonParser.Parse(new("https://example.com/feed", Encoding.UTF8.GetBytes("""
            {"rss":{"channel":{"item":[{"title":"Post","pubDate":"1970-01-01T00:00:01Z","description":"body"}]}}}
            """)))!;
        Assert.Equal(Md5Hash.Hex("1.0Post"), Assert.Single(feed.Items).UniqueId);
    }

    [Theory]
    [InlineData("123 bytes", 123)]
    [InlineData("invalid", 0)]
    public void EnclosureLengthUsesTheNativeIntegerPrefix(string length, long expected)
    {
        var feed = RssInJsonParser.Parse(new("https://example.com/feed", Encoding.UTF8.GetBytes($$$$"""
            {"rss":{"channel":{"item":[{"guid":"1","title":"Post","enclosure":{"url":"https://example.com/audio","length":"{{{{length}}}}"}}]}}}
            """)))!;
        Assert.Equal(expected, Assert.Single(Assert.Single(feed.Items).Attachments!).SizeInBytes);
    }
}
