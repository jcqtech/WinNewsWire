using WinNewsWire.AppShared.ArticleRendering;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

/// <summary>
/// Smoke coverage for <see cref="ArticleRenderingSpecialCases"/>. Mirrors the
/// behaviour of NetNewsWire's <c>filterHTMLIfNeeded</c> step: only Verge URLs
/// get rewritten, and rewriting replaces the well-known mojibake sequences
/// with the intended Unicode characters.
/// </summary>
public class ArticleRenderingSpecialCasesTests
{
    [Theory]
    [InlineData("https://www.theverge.com/2024/1/2/foo", true)]
    [InlineData("https://theverge.com/", true)]
    [InlineData("http://blog.theverge.com/", true)]
    [InlineData("https://daringfireball.net/", false)]
    [InlineData("https://example.com/", false)]
    public void IsVergeHostMatches(string url, bool expected)
    {
        Assert.Equal(expected, ArticleRenderingSpecialCases.IsVergeSpecialCase(new System.Uri(url)));
    }

    [Fact]
    public void FilterHtmlIfNeededPassesThroughOnNonSpecialHost()
    {
        var html = "<p>â€™ stays â€™</p>";
        Assert.Equal(html,
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded("https://example.com/", html));
    }

    [Fact]
    public void FilterHtmlIfNeededPassesThroughOnInvalidUrl()
    {
        var html = "<p>â€™ stays â€™</p>";
        Assert.Equal(html, ArticleRenderingSpecialCases.FilterHtmlIfNeeded(null, html));
        Assert.Equal(html, ArticleRenderingSpecialCases.FilterHtmlIfNeeded(string.Empty, html));
        Assert.Equal(html, ArticleRenderingSpecialCases.FilterHtmlIfNeeded("not a url", html));
    }

    [Fact]
    public void VergeRightSingleQuoteIsFixed()
    {
        Assert.Equal("Apple\u2019s",
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded(
                "https://www.theverge.com/foo", "Appleâ€™s"));
    }

    [Fact]
    public void VergeEntityEncodedQuotesAreFixed()
    {
        Assert.Equal("Apple\u2019s",
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded(
                "https://www.theverge.com/foo", "Apple&acirc;&#128;&#153;s"));
    }

    [Fact]
    public void VergeDoubleQuotesAndEmDashAreFixed()
    {
        var input = "He said â€œhelloâ€\u00a0â€\" goodbye";
        var output = ArticleRenderingSpecialCases.FilterVergeHtml(input);
        Assert.Contains("\u201Chello", output);
        Assert.DoesNotContain("â€œ", output);
    }

    [Fact]
    public void VergeDoubleEncodedEllipsisIsFixed()
    {
        Assert.Equal("And then\u2026",
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded(
                "https://www.theverge.com/foo", "And then &amp;hellip;"));
    }

    [Fact]
    public void StrayAcircCharsAreDropped()
    {
        Assert.Equal("hello world",
            ArticleRenderingSpecialCases.FilterHtmlIfNeeded(
                "https://www.theverge.com/foo", "helloÂ world"));
    }
}
