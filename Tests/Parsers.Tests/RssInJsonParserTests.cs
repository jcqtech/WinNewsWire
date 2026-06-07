using Xunit;
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
}
