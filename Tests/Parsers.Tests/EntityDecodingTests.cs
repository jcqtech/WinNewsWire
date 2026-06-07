using System.Net;
using Xunit;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.Parsers.Tests;

/// <summary>Port of RSParser's <c>EntityDecodingTests.swift</c>.
/// Tests HTML entity decoding via <see cref="StringExtensions.DecodeHtmlEntities"/>.</summary>
public class EntityDecodingTests
{
    // --- Direct ports from NNW EntityDecodingTests.swift ---

    [Fact]
    public void Test39Decoding()
    {
        // Bug found by Manton Reece — &#39; was not decoded in JSON Feeds from micro.blog.
        var s = "These are the times that try men&#39;s souls.";
        Assert.Equal("These are the times that try men's souls.", s.DecodeHtmlEntities());
    }

    [Fact]
    public void TestDecimalEllipsis()
    {
        Assert.Equal("\u2026", "&#8230;".DecodeHtmlEntities());
    }

    [Fact]
    public void TestHexEllipsis()
    {
        Assert.Equal("\u2026", "&#x2026;".DecodeHtmlEntities());
    }

    [Fact]
    public void TestDecimalApostrophe()
    {
        Assert.Equal("'", "&#039;".DecodeHtmlEntities());
    }

    [Fact]
    public void TestSectionSign()
    {
        Assert.Equal("\u00A7", "&#167;".DecodeHtmlEntities());
    }

    [Fact]
    public void TestHexPoundSign()
    {
        // NNW tests &#XA3; (uppercase X) — should still decode.
        Assert.Equal("\u00A3", "&#XA3;".DecodeHtmlEntities());
    }

    // --- Additional entity tests (basic named entities) ---

    [Theory]
    [InlineData("&amp;", "&")]
    [InlineData("&lt;", "<")]
    [InlineData("&gt;", ">")]
    [InlineData("&quot;", "\"")]
    [InlineData("&apos;", "'")]
    public void BasicNamedEntities(string input, string expected)
    {
        Assert.Equal(expected, input.DecodeHtmlEntities());
    }

    // --- Extended named entities ---

    [Theory]
    [InlineData("&mdash;", "\u2014")]
    [InlineData("&ndash;", "\u2013")]
    [InlineData("&hellip;", "\u2026")]
    [InlineData("&trade;", "\u2122")]
    [InlineData("&copy;", "\u00A9")]
    [InlineData("&reg;", "\u00AE")]
    [InlineData("&lsquo;", "\u2018")]
    [InlineData("&rsquo;", "\u2019")]
    [InlineData("&ldquo;", "\u201C")]
    [InlineData("&rdquo;", "\u201D")]
    [InlineData("&nbsp;", "\u00A0")]
    public void ExtendedNamedEntities(string input, string expected)
    {
        Assert.Equal(expected, input.DecodeHtmlEntities());
    }

    // --- Numeric entities (decimal and hex) ---

    [Theory]
    [InlineData("&#38;", "&")]
    [InlineData("&#x26;", "&")]
    [InlineData("&#60;", "<")]
    [InlineData("&#x3C;", "<")]
    [InlineData("&#62;", ">")]
    [InlineData("&#x3E;", ">")]
    public void NumericEntities(string input, string expected)
    {
        Assert.Equal(expected, input.DecodeHtmlEntities());
    }

    // --- Edge cases ---

    [Fact]
    public void PlainTextPassesThrough()
    {
        var s = "Nothing to decode here.";
        Assert.Equal(s, s.DecodeHtmlEntities());
    }

    [Fact]
    public void MultipleEntitiesInOneString()
    {
        var s = "A &amp; B &lt; C &gt; D";
        Assert.Equal("A & B < C > D", s.DecodeHtmlEntities());
    }

    [Fact]
    public void MalformedEntityLeftAlone()
    {
        // Bare ampersand without a valid entity should survive.
        var s = "A & B";
        var decoded = s.DecodeHtmlEntities();
        Assert.Equal("A & B", decoded);
    }

    [Fact]
    public void EntityInFeedTitle()
    {
        var title = "Daring Fireball &mdash; John Gruber&#8217;s Blog";
        Assert.Equal("Daring Fireball \u2014 John Gruber\u2019s Blog", title.DecodeHtmlEntities());
    }

    [Fact]
    public void EntityInFeedContent()
    {
        var content = "He said &ldquo;hello&rdquo; &amp; goodbye&#8230;";
        Assert.Equal("He said \u201Chello\u201D & goodbye\u2026", content.DecodeHtmlEntities());
    }
}
