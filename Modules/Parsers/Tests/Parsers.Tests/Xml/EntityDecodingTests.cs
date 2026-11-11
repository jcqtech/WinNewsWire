using System.Text;
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

    [Fact]
    public void PredefinedXmlEntitiesInContext()
    {
        Assert.Equal("Tom & Jerry", "Tom &amp; Jerry".DecodeHtmlEntities());
        Assert.Equal("<script>alert(1)</script>", "&lt;script&gt;alert(1)&lt;/script&gt;".DecodeHtmlEntities());
        Assert.Equal("He said \"hi\"", "He said &quot;hi&quot;".DecodeHtmlEntities());
        Assert.Equal("it's", "it&apos;s".DecodeHtmlEntities());
    }

    [Theory]
    [InlineData("&zwj;", "\u200D")]
    [InlineData("&zwnj;", "\u200C")]
    public void ZeroWidthEntity(string input, string expected) => Assert.Equal(expected, input.DecodeHtmlEntities());

    [Fact] public void ZeroWidthJoinerInContext() => Assert.Equal("a\u200Db", "a&zwj;b".DecodeHtmlEntities());
    [Fact] public void UnknownNamedEntityPassesThrough()
    {
        Assert.Equal("&foo;", "&foo;".DecodeHtmlEntities());
        Assert.Equal("hello &bogus; world", "hello &bogus; world".DecodeHtmlEntities());
    }
    [Fact] public void EntityWithoutSemicolonPassesThrough()
    {
        Assert.Equal("&amp", "&amp".DecodeHtmlEntities());
        Assert.Equal("a&amp b", "a&amp b".DecodeHtmlEntities());
    }
    [Fact] public void EmptyEntityReferencePassesThrough() => Assert.Equal("&;", "&;".DecodeHtmlEntities());
    [Fact] public void LonelyAmpersandPassesThrough()
    {
        Assert.Equal("&", "&".DecodeHtmlEntities());
        Assert.Equal("a & b", "a & b".DecodeHtmlEntities());
    }
    [Fact] public void MalformedNumericEntitiesPassThrough()
    {
        foreach (var input in new[] { "&#;", "&#x;", "&#abc;", "&#xZZ;" }) Assert.Equal(input, input.DecodeHtmlEntities());
    }
    [Fact] public void NumericEntityZeroPassesThrough()
    {
        Assert.Equal("&#0;", "&#0;".DecodeHtmlEntities());
        Assert.Equal("&#x0;", "&#x0;".DecodeHtmlEntities());
    }
    [Fact] public void NumericEntityOutOfRangePassesThrough()
    {
        Assert.Equal("&#x110000;", "&#x110000;".DecodeHtmlEntities());
        Assert.Equal("&#99999999;", "&#99999999;".DecodeHtmlEntities());
    }
    [Theory]
    [InlineData("&#128;", "\u20AC")]
    [InlineData("&#130;", "\u201A")]
    [InlineData("&#133;", "\u2026")]
    [InlineData("&#134;", "\u2020")]
    [InlineData("&#145;", "\u2018")]
    [InlineData("&#146;", "\u2019")]
    [InlineData("&#147;", "\u201C")]
    [InlineData("&#148;", "\u201D")]
    [InlineData("&#150;", "\u2013")]
    [InlineData("&#151;", "\u2014")]
    [InlineData("&#153;", "\u2122")]
    [InlineData("&#x80;", "\u20AC")]
    [InlineData("&#x96;", "\u2013")]
    public void WindowsLatin1Remap(string input, string expected) => Assert.Equal(expected, input.DecodeHtmlEntities());

    [Fact] public void MultipleConsecutiveEntities()
    {
        Assert.Equal("&<>", "&amp;&lt;&gt;".DecodeHtmlEntities());
        Assert.Equal("\u201C\u201D", "&#8220;&#8221;".DecodeHtmlEntities());
    }
    [Fact] public void EntitiesMixedWithPlainText()
        => Assert.Equal("One & two: \u2018three\u2019 \u2014 \u2014 \u2014.", "One &amp; two: &#8216;three&#8217; \u2014 &#x2014; &mdash;.".DecodeHtmlEntities());
    [Fact] public void StringWithNoEntitiesUnchanged()
    {
        const string input = "Just some text with no entities at all.";
        Assert.Same(input, input.DecodeHtmlEntities());
    }
    [Fact] public void EmptyStringUnchanged() => Assert.Equal("", "".DecodeHtmlEntities());
    [Fact] public void HexEntityIsCaseInsensitiveForDigits()
    {
        var lower = "&#xab;".DecodeHtmlEntities();
        Assert.Equal(lower, "&#xAB;".DecodeHtmlEntities());
        Assert.Equal("\u00AB", lower);
        Assert.Equal("\u00AB", "&#xAb;".DecodeHtmlEntities());
        Assert.Equal("\u00AB", "&#xaB;".DecodeHtmlEntities());
        Assert.Equal("\u2026", "&#X2026;".DecodeHtmlEntities());
    }
    [Fact] public void EntityAtEndOfInputBoundary()
    {
        Assert.Equal("foo&", "foo&amp;".DecodeHtmlEntities());
        foreach (var input in new[] { "foo&amp", "foo&", "foo&#", "foo&#x" }) Assert.Equal(input, input.DecodeHtmlEntities());
    }
    [Fact] public void FourByteUtf8EmojiDecodes()
    {
        Assert.Equal("\U0001F600", "&#x1F600;".DecodeHtmlEntities());
        Assert.Equal("\U0001F600", "&#128512;".DecodeHtmlEntities());
        Assert.Equal("\U0001F44B", "&#x1F44B;".DecodeHtmlEntities());
        Assert.Equal(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, Encoding.UTF8.GetBytes("&#x1F600;".DecodeHtmlEntities()));
    }
}
