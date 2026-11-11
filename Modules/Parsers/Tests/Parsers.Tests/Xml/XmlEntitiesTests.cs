using System.Text;
using WinNewsWire.Parsers.Xml;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public sealed class XmlEntitiesTests
{
    private static (string Text, int NextIndex) DecodeOne(string input, XmlEntities.Mode mode = XmlEntities.Mode.Html)
    {
        var result = XmlEntities.Decode(Encoding.UTF8.GetBytes(input), 0, mode);
        return (Encoding.UTF8.GetString(result.Bytes.Span), result.NextIndex);
    }
    private static void AssertDecoded(string input, string expected, XmlEntities.Mode mode = XmlEntities.Mode.Html)
        => Assert.Equal((expected, Encoding.UTF8.GetByteCount(input)), DecodeOne(input, mode));
    private static void AssertRejected(string input, XmlEntities.Mode mode = XmlEntities.Mode.Html)
        => Assert.Equal(("&", 1), DecodeOne(input, mode));
    private static readonly (string Input, string Expected)[] Predefined =
        [("&amp;", "&"), ("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""), ("&apos;", "'")];

    [Fact] public void PredefinedEntitiesNormalMode()
    {
        foreach (var (input, expected) in Predefined) AssertDecoded(input, expected);
    }
    [Fact] public void PredefinedEntitiesXmlStrictMode()
    {
        foreach (var (input, expected) in Predefined) AssertDecoded(input, expected, XmlEntities.Mode.XmlStrict);
    }
    [Fact] public void HtmlNamedEntitiesLiteralInXmlStrict()
    {
        foreach (var input in new[] { "&mdash;", "&copy;", "&nbsp;" }) AssertRejected(input, XmlEntities.Mode.XmlStrict);
    }
    [Fact] public void NumericEntitiesExpandInXmlStrict() => Assert.Equal("\u2019", DecodeOne("&#8217;", XmlEntities.Mode.XmlStrict).Text);
    [Fact] public void DecimalNumericEntities()
    {
        foreach (var (input, expected) in new[] { ("&#65;", "A"), ("&#8482;", "\u2122"), ("&#8220;", "\u201C"), ("&#9731;", "\u2603") })
            AssertDecoded(input, expected);
    }
    [Fact] public void DecimalZeroIsRejected() => AssertRejected("&#0;");
    [Fact] public void DecimalWithNonDigitIsRejected() => AssertRejected("&#12a;");
    [Fact] public void DecimalEmptyIsRejected() => AssertRejected("&#;");
    [Fact] public void HexNumericEntities()
    {
        foreach (var (input, expected) in new[] { ("&#x41;", "A"), ("&#X41;", "A"), ("&#x2014;", "\u2014"), ("&#xAD;", "\u00AD"), ("&#x1F600;", "\U0001F600") })
            AssertDecoded(input, expected);
    }
    [Fact] public void HexZeroIsRejected() => AssertRejected("&#x0;");
    [Fact] public void HexEmptyIsRejected() => AssertRejected("&#x;");
    [Fact] public void HexWithNonHexIsRejected() => AssertRejected("&#xZZ;");
    [Fact] public void SurrogatesRejected()
    {
        foreach (var input in new[] { "&#xD800;", "&#xDC00;", "&#xDFFF;", "&#55296;" }) AssertRejected(input);
    }
    [Fact] public void OutOfRangeCodepointRejected() => AssertRejected("&#x110000;");
    [Fact] public void Windows1252Remap()
    {
        foreach (var (input, expected) in new[]
        {
            ("&#128;", "\u20AC"), ("&#133;", "\u2026"), ("&#145;", "\u2018"),
            ("&#146;", "\u2019"), ("&#147;", "\u201C"), ("&#148;", "\u201D"),
            ("&#150;", "\u2013"), ("&#151;", "\u2014"), ("&#153;", "\u2122")
        }) Assert.Equal(expected, DecodeOne(input).Text);
    }
    [Fact] public void Windows1252BoundariesNotRemapped()
    {
        Assert.Equal("\u007F", DecodeOne("&#x7F;").Text);
        Assert.Equal("\u00A0", DecodeOne("&#xA0;").Text);
    }
    [Fact] public void HtmlNamedEntities()
    {
        foreach (var (input, expected) in new[]
        {
            ("&copy;", "\u00A9"), ("&mdash;", "\u2014"), ("&ndash;", "\u2013"),
            ("&ldquo;", "\u201C"), ("&rdquo;", "\u201D"), ("&hellip;", "\u2026"),
            ("&trade;", "\u2122"), ("&eacute;", "\u00E9"), ("&nbsp;", "\u00A0"), ("&shy;", "\u00AD")
        }) AssertDecoded(input, expected);
    }
    [Fact] public void LongestHtmlNamedEntity() => Assert.Equal("\u2233", DecodeOne("&CounterClockwiseContourIntegral;").Text);
    [Fact] public void HtmlNamedEntityCaseSensitive() => AssertRejected("&COPY;");
    [Fact] public void HtmlNamedEntityUppercase() => Assert.Equal("\u00C6", DecodeOne("&AElig;").Text);
    [Fact] public void UnknownHtmlNamedEntityIsRejected() => AssertRejected("&notARealEntity;");
    [Fact] public void NoSemicolonReturnsLiteral() => AssertRejected("&amp");
    [Fact] public void EmptyEntityReturnsLiteral() => AssertRejected("&;");
    [Fact] public void WhitespaceTerminatesScan()
    {
        foreach (var input in new[] { "&amp ", "&amp\t", "&amp\n" }) AssertRejected(input);
    }
    [Fact] public void NestedAmpersandTerminatesScan() => AssertRejected("&amp&lt;");
    [Fact] public void LessThanTerminatesScan() => AssertRejected("&amp<");
    [Fact] public void TooLongEntityIsRejected() => AssertRejected("&" + new string('x', 100) + ";");
    [Fact] public void DecodesAtNonZeroIndex()
    {
        var result = XmlEntities.Decode("Hello &amp; world"u8, 6, XmlEntities.Mode.Html);
        Assert.Equal("&", Encoding.UTF8.GetString(result.Bytes.Span));
        Assert.Equal(11, result.NextIndex);
    }
    [Fact] public void XmlStrictModeStillExpandsNumericEntities() => Assert.Equal("A", DecodeOne("&#65;", XmlEntities.Mode.XmlStrict).Text);
    [Fact] public void HtmlModeExpandsNamedEntities() => Assert.Equal("\u00A9", DecodeOne("&copy;", XmlEntities.Mode.Html).Text);
    [Fact] public void WindowsLatin1ExtensionTableSize() => Assert.Equal(32, XmlEntities.WindowsLatin1Extension.Length);
    [Fact] public void WindowsLatin1ExtensionKnownEntries()
    {
        Assert.Equal(0x20ACu, XmlEntities.WindowsLatin1Extension[0]);
        Assert.Equal(0x2122u, XmlEntities.WindowsLatin1Extension[0x99 - 0x80]);
    }
}
