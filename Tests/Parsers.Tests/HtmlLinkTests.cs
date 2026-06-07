using System.Linq;
using Xunit;
using WinNewsWire.Parsers;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>HTMLLinkTests.swift</c>.</summary>
public class HtmlLinkTests
{
    [Fact]
    public void SixColorsHasExpectedLink()
    {
        var links = HtmlLinkParser.ParseLinks(TestResources.Load("sixcolors.html", "http://sixcolors.com/"));
        Assert.Contains(links,
            l => l.Href == "https://www.theincomparable.com/theincomparable/290/index.php"
              && l.Text == "this week\u2019s episode of The Incomparable");
        // Parity with Swift `testSixColorsLink` — assert the total link count
        // to catch regressions in the anchor-extraction loop.
        Assert.Equal(131, links.Count);
    }
}
