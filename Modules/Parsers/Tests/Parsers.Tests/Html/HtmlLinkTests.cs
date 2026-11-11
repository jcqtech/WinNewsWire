using System.Linq;
using System.Text;
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

    [Fact]
    public void LinksInsideScriptAreIgnored()
    {
        var urls = Urls("""
            <html><body>
            <a href="http://real.example.com/">real</a>
            <script>
              var markup = '<a href="http://not-a-link.example.com/">nope</a>';
              var also = "<a href='http://also-not.example.com/'>also nope</a>";
            </script>
            <a href="http://real2.example.com/">real 2</a>
            </body></html>
            """);
        Assert.Contains("http://real.example.com/", urls);
        Assert.Contains("http://real2.example.com/", urls);
        Assert.DoesNotContain("http://not-a-link.example.com/", urls);
        Assert.DoesNotContain("http://also-not.example.com/", urls);
    }

    [Fact]
    public void LinksInsideStyleAreIgnored()
    {
        var urls = Urls("""
            <html><body>
            <style>
              .banner { background: url('http://css-url.example.com/bg.png'); }
              /* <a href="http://commented-out.example.com/">x</a> */
            </style>
            <a href="http://real.example.com/">real</a>
            </body></html>
            """);
        Assert.Equal(new[] { "http://real.example.com/" }, urls);
    }

    [Fact]
    public void RelativeUrlsResolvedAgainstPageUrl()
    {
        var urls = Urls("""
            <html><body>
              <a href="/foo">absolute path</a>
              <a href="bar.html">relative path</a>
              <a href="http://other.example.com/x">already absolute</a>
            </body></html>
            """, "http://example.com/page/");
        Assert.Contains("http://example.com/foo", urls);
        Assert.Contains("http://example.com/page/bar.html", urls);
        Assert.Contains("http://other.example.com/x", urls);
    }

    [Fact]
    public void UnclosedTagsDoNotCrashAndStillFindLinks()
    {
        var urls = Urls("""
            <html><body>
            <p>Orphan <b>bold <i>italic
            <a href="http://example.com/first">first</a>
            <div>Unclosed div
            <a href="http://example.com/second">second</a>
            </body>
            """);
        Assert.Contains("http://example.com/first", urls);
        Assert.Contains("http://example.com/second", urls);
    }

    [Fact]
    public void AnchorWithoutHrefIgnored()
    {
        var urls = Urls("""
            <html><body>
            <a name="top"></a>
            <a href="http://example.com/">real</a>
            <a>no href at all</a>
            </body></html>
            """);
        Assert.Equal(new[] { "http://example.com/" }, urls);
    }

    [Fact]
    public void EntitiesInHrefDecoded()
    {
        var urls = Urls("""
            <html><body>
            <a href="http://example.com/search?q=foo&amp;page=2">two-param</a>
            <a href="http://example.com/a?x=1&amp;y=2&amp;z=3">three-param</a>
            </body></html>
            """);
        Assert.Contains("http://example.com/search?q=foo&page=2", urls);
        Assert.Contains("http://example.com/a?x=1&y=2&z=3", urls);
        Assert.All(urls, url => Assert.DoesNotContain("&amp;", url));
    }

    [Fact]
    public void NestedStartResetsAnchorTextAsInTheNativeScanner()
    {
        var links = HtmlLinkParser.ParseLinks(new("http://example.com/", "<a href='/'>before <b>after</b></a>"u8.ToArray()));
        Assert.Equal("after", Assert.Single(links).Text);
    }

    [Fact]
    public void ScannerWithoutDelegateStillConsumesTheDocument()
        => new HtmlScanner(new NoopDelegate()) { Delegate = null }.Parse("plain <a href='/'>text</a>"u8.ToArray());

    private static string[] Urls(string html, string url = "http://example.com/")
        => HtmlLinkParser.ParseLinks(new(url, Encoding.UTF8.GetBytes(html))).Select(link => link.Href).OfType<string>().ToArray();
    private sealed class NoopDelegate : IHtmlScannerDelegate { }
}
