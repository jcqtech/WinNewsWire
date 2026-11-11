using System.Text;
using WinNewsWire.Parsers.Xml;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public sealed class XmlEncodingTests
{
    private static byte[] Decl(string name, bool singleQuoted = false)
    {
        var quote = singleQuoted ? "'" : "\"";
        return Encoding.UTF8.GetBytes($"<?xml version={quote}1.0{quote} encoding={quote}{name}{quote}?>");
    }
    private static void AssertRoundTrip(string text, int codePage, string declaredAs)
    {
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        Assert.NotNull(encoding);
        byte[] input = [.. Decl(declaredAs), .. "<a>"u8, .. encoding.GetBytes(text), .. "</a>"u8];
        Assert.Contains($"<a>{text}</a>", Encoding.UTF8.GetString(XmlEncoding.ToUtf8(input)));
    }
    private static void AssertLatin1(string name, bool singleQuoted = false, byte[]? prefix = null)
    {
        byte[] input = [.. prefix ?? [], .. Decl(name, singleQuoted), .. "<a>"u8, 0xE9, .. "</a>"u8];
        Assert.Contains("<a>\u00E9</a>", Encoding.UTF8.GetString(XmlEncoding.ToUtf8(input)));
    }

    [Fact] public void PlainUtf8NoDeclarationReturnsInputUnchanged()
    {
        var input = "<root>hello</root>"u8.ToArray();
        Assert.Equal(input, XmlEncoding.ToUtf8(input));
    }
    [Fact] public void EmptyInputReturnsEmpty() => Assert.Empty(XmlEncoding.ToUtf8([]));
    [Fact] public void OneByteInputReturnsUnchanged() => Assert.Equal(new byte[] { 0x58 }, XmlEncoding.ToUtf8([0x58]));
    [Fact] public void TwoByteInputReturnsUnchanged() => Assert.Equal(new byte[] { 0x58, 0x59 }, XmlEncoding.ToUtf8([0x58, 0x59]));
    [Fact] public void Utf8BomStripped() => Assert.Equal("<a/>"u8.ToArray(), XmlEncoding.ToUtf8([0xEF, 0xBB, 0xBF, .. "<a/>"u8]));
    [Fact] public void Utf16LeBomDecoded()
        => Assert.Equal("<a>\u00E9</a>", Encoding.UTF8.GetString(XmlEncoding.ToUtf8([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<a>\u00E9</a>")])));
    [Fact] public void Utf16BeBomDecoded()
        => Assert.Equal("<a>\u00E9</a>", Encoding.UTF8.GetString(XmlEncoding.ToUtf8([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("<a>\u00E9</a>")])));
    [Fact] public void EncodingNameIsCaseInsensitive()
    {
        foreach (var name in new[] { "ISO-8859-1", "iso-8859-1", "Iso-8859-1", "iSO-8859-1" }) AssertLatin1(name);
    }
    [Fact] public void Utf8Alias()
    {
        foreach (var name in new[] { "UTF-8", "utf-8", "utf8", "UTF8", "us-ascii", "ASCII" })
        {
            byte[] input = [.. Decl(name), .. "<a>hello</a>"u8];
            Assert.Equal(input, XmlEncoding.ToUtf8(input));
        }
    }
    [Fact] public void Latin1Alias()
    {
        foreach (var name in new[] { "ISO-8859-1", "latin1", "latin-1", "LATIN-1" }) AssertLatin1(name);
    }
    [Fact] public void Windows1252Alias()
    {
        foreach (var name in new[] { "windows-1252", "Windows-1252", "cp1252", "CP1252" })
        {
            byte[] input = [.. Decl(name), .. "<a>"u8, 0x93, .. "Hi"u8, 0x94, .. "</a>"u8];
            Assert.Contains("<a>\u201CHi\u201D</a>", Encoding.UTF8.GetString(XmlEncoding.ToUtf8(input)));
        }
    }
    [Fact] public void UnknownEncodingFallsThroughUnchanged()
    {
        var input = "<?xml version=\"1.0\" encoding=\"xyz-9000\"?><root>hello</root>"u8.ToArray();
        Assert.Equal(input, XmlEncoding.ToUtf8(input));
    }
    [Fact] public void SingleQuotedEncodingAttribute() => AssertLatin1("ISO-8859-1", true);
    [Fact] public void LeadingWhitespaceBeforeDeclaration() => AssertLatin1("ISO-8859-1", prefix: "  \n\t"u8.ToArray());
    [Fact] public void DeclarationWithoutEncodingAttribute()
    {
        var input = "<?xml version=\"1.0\"?><root>hello</root>"u8.ToArray();
        Assert.Equal(input, XmlEncoding.ToUtf8(input));
    }
    [Fact] public void ShiftJis() => AssertRoundTrip("\u65E5\u672C\u8A9E", 932, "shift_jis");
    [Fact] public void ShiftJisAlternateName() => AssertRoundTrip("\u65E5\u672C\u8A9E", 932, "Shift-JIS");
    [Fact] public void EucJp() => AssertRoundTrip("\u65E5\u672C\u8A9E", 51932, "euc-jp");
    [Fact] public void Windows1251Cyrillic() => AssertRoundTrip("\u0420\u0443\u0441\u0441\u043A\u0438\u0439", 1251, "windows-1251");
    [Fact] public void IsoLatin2CentralEuropean() => AssertRoundTrip("\u010Cesk\u00FD", 28592, "iso-8859-2");
    [Fact] public void IsoLatin9Euro() => AssertRoundTrip("\u20AC100", 28605, "iso-8859-15");
    [Fact] public void Big5TraditionalChinese() => AssertRoundTrip("\u7E41\u9AD4\u4E2D\u6587", 950, "big5");
    [Fact] public void EucKrKorean() => AssertRoundTrip("\uD55C\uAD6D\uC5B4", 51949, "euc-kr");
    [Fact] public void Koi8RCyrillic() => AssertRoundTrip("\u0420\u0443\u0441\u0441\u043A\u0438\u0439", 20866, "koi8-r");
    [Fact] public void GbkSimplifiedChinese() => AssertRoundTrip("\u7B80\u4F53\u4E2D\u6587", 936, "gbk");

    [Fact] public void MalformedUtf16PreservesOriginalBytes()
    {
        byte[] input = [0xFF, 0xFE, 0x00, 0xD8];
        Assert.Equal(input, XmlEncoding.ToUtf8(input));
    }
}
