using System.Text;
using WinNewsWire.Parsers.Xml;
using Xunit;

namespace WinNewsWire.Parsers.Tests;

public sealed class XmlAsciiTests
{
    [Fact]
    public void ByteConstantsMatchLiterals()
    {
        Assert.Equal<byte>(0x09, XmlAscii.Tab);
        Assert.Equal<byte>(0x0A, XmlAscii.Newline);
        Assert.Equal<byte>(0x0D, XmlAscii.CarriageReturn);
        Assert.Equal<byte>(0x20, XmlAscii.Space);
        Assert.Equal<byte>(0x21, XmlAscii.Exclamation);
        Assert.Equal<byte>(0x22, XmlAscii.DoubleQuote);
        Assert.Equal<byte>(0x26, XmlAscii.Ampersand);
        Assert.Equal<byte>(0x3C, XmlAscii.LessThan);
        Assert.Equal<byte>(0x3E, XmlAscii.GreaterThan);
        Assert.Equal<byte>(0x30, XmlAscii.Zero);
        Assert.Equal<byte>(0x39, XmlAscii.Nine);
        Assert.Equal<byte>(0x41, XmlAscii.UpperA);
        Assert.Equal<byte>(0x61, XmlAscii.LowerA);
        Assert.Equal<byte>(0x7A, XmlAscii.LowerZ);
    }

    [Fact] public void WhitespaceIsRecognized()
    {
        foreach (var b in " \t\n\r"u8) Assert.True(b.IsAsciiWhitespace());
    }
    [Fact] public void NonWhitespaceIsRejected()
    {
        foreach (byte b in new byte[] { 65, 48, 95, 60, 0, 11, 12, 255 }) Assert.False(b.IsAsciiWhitespace());
    }
    [Fact] public void DigitsRecognized()
    {
        foreach (var b in "0123456789"u8) Assert.True(b.IsAsciiDigit());
    }
    [Fact] public void NonDigitIsRejected()
    {
        foreach (var b in "Aa/: \0\u00FF".Select(c => (byte)c)) Assert.False(b.IsAsciiDigit());
    }
    [Fact] public void LettersRecognized()
    {
        foreach (var b in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8) Assert.True(b.IsAsciiLetter());
    }
    [Fact] public void NonLetterIsRejected()
    {
        foreach (var b in "09@[`{-\0\u00FF".Select(c => (byte)c)) Assert.False(b.IsAsciiLetter());
    }
    [Fact] public void HexValueForDigits()
    {
        for (byte b = (byte)'0'; b <= '9'; b++) Assert.Equal((uint)(b - '0'), b.AsciiHexValue());
    }
    [Fact] public void HexValueForUpperLetters()
    {
        for (byte b = (byte)'A'; b <= 'F'; b++) Assert.Equal((uint)(b - 'A' + 10), b.AsciiHexValue());
    }
    [Fact] public void HexValueForLowerLetters()
    {
        for (byte b = (byte)'a'; b <= 'f'; b++) Assert.Equal((uint)(b - 'a' + 10), b.AsciiHexValue());
    }
    [Fact] public void HexValueForNonHexIsNil()
    {
        foreach (var b in "GgZz@`/: \0\u00FF".Select(c => (byte)c)) Assert.Null(b.AsciiHexValue());
    }
    [Fact] public void HexValueNeverFalsePositiveFromCaseFoldTrick()
    {
        for (var i = 0; i <= 255; i++)
        {
            uint? expected = i switch
            {
                >= '0' and <= '9' => (uint)(i - '0'),
                >= 'A' and <= 'F' => (uint)(i - 'A' + 10),
                >= 'a' and <= 'f' => (uint)(i - 'a' + 10),
                _ => null
            };
            Assert.Equal(expected, ((byte)i).AsciiHexValue());
        }
    }
    [Fact] public void UppercaseFolds()
    {
        for (byte b = (byte)'A'; b <= 'Z'; b++) Assert.Equal((byte)(b + 32), b.AsciiLowercased());
    }
    [Fact] public void LowercaseLettersUnchanged()
    {
        for (byte b = (byte)'a'; b <= 'z'; b++) Assert.Equal(b, b.AsciiLowercased());
    }
    [Fact] public void NonLettersUnchanged()
    {
        foreach (var b in "09@[`{/.-\0\u007F\u00FF".Select(c => (byte)c)) Assert.Equal(b, b.AsciiLowercased());
    }
    [Fact] public void ValidXmlNameStart()
    {
        foreach (var b in "azAZ_:"u8) Assert.True(b.IsXmlNameStart());
    }
    [Fact] public void InvalidXmlNameStart()
    {
        foreach (var b in "09-. </\0"u8) Assert.False(b.IsXmlNameStart());
    }
    [Fact] public void ValidXmlNameChar()
    {
        foreach (var b in "aZ_:09-."u8) Assert.True(b.IsXmlNameChar());
    }
    [Fact] public void InvalidXmlNameChar()
    {
        foreach (var b in " <>=/&\"\0"u8) Assert.False(b.IsXmlNameChar());
    }
    [Fact] public void EqualsMatchingLiteral() => Assert.True(XmlAscii.Equals("xmlns"u8, "xmlns"u8));
    [Fact] public void EqualsRejectsContentMismatch()
    {
        Assert.False(XmlAscii.Equals("xmlns"u8, "XMLNS"u8));
        Assert.False(XmlAscii.Equals("xmlns"u8, "xmlnz"u8));
    }
    [Fact] public void EqualsRejectsLengthMismatch()
    {
        Assert.False(XmlAscii.Equals("xml"u8, "xmlns"u8));
        Assert.False(XmlAscii.Equals("xml"u8, "xm"u8));
    }
    [Fact] public void EqualsEmptySlice()
    {
        Assert.True(XmlAscii.Equals([], ""u8));
        Assert.False(XmlAscii.Equals([], "x"u8));
    }
    [Fact] public void EqualsUsesSliceStartIndex()
    {
        var slice = Encoding.UTF8.GetBytes("..xmlns..").AsSpan(2, 5);
        Assert.True(XmlAscii.Equals(slice, "xmlns"u8));
        Assert.False(XmlAscii.Equals(slice, "..xmlns.."u8));
    }
    [Fact] public void CaseInsensitiveExactMatch() => Assert.True(XmlAscii.EqualsCaseInsensitive("xmlns"u8, "xmlns"u8));
    [Fact] public void CaseInsensitiveUppercaseMatches() => Assert.True(XmlAscii.EqualsCaseInsensitive("XMLNS"u8, "xmlns"u8));
    [Fact] public void CaseInsensitiveMixedCaseMatches() => Assert.True(XmlAscii.EqualsCaseInsensitive("XmLnS"u8, "xmlns"u8));
    [Fact] public void CaseInsensitiveRejectsContentMismatch() => Assert.False(XmlAscii.EqualsCaseInsensitive("xmlnz"u8, "xmlns"u8));
    [Fact] public void CaseInsensitiveRejectsLengthMismatch() => Assert.False(XmlAscii.EqualsCaseInsensitive("xml"u8, "xmlns"u8));
    [Fact] public void CaseInsensitivePreservesNonLetters()
    {
        Assert.True(XmlAscii.EqualsCaseInsensitive("utf-8"u8, "utf-8"u8));
        Assert.False(XmlAscii.EqualsCaseInsensitive("utf-8"u8, "utf_8"u8));
    }
}
