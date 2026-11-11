namespace WinNewsWire.Parsers.Xml;

internal static class XmlAscii
{
    public const byte Tab = 0x09, Newline = 0x0A, CarriageReturn = 0x0D, Space = 0x20;
    public const byte Exclamation = 0x21, DoubleQuote = 0x22, Hash = 0x23, Ampersand = 0x26;
    public const byte SingleQuote = 0x27, Plus = 0x2B, Comma = 0x2C, Hyphen = 0x2D, Dot = 0x2E;
    public const byte Slash = 0x2F, Colon = 0x3A, Semicolon = 0x3B, LessThan = 0x3C;
    public const byte EqualsSign = 0x3D, GreaterThan = 0x3E, Question = 0x3F;
    public const byte LeftBracket = 0x5B, RightBracket = 0x5D, Underscore = 0x5F;
    public const byte Zero = 0x30, Nine = 0x39, UpperA = 0x41, UpperX = 0x58, UpperZ = 0x5A;
    public const byte LowerA = 0x61, LowerF = 0x66, LowerX = 0x78, LowerZ = 0x7A;

    public static bool IsAsciiWhitespace(this byte value) => value is Space or Tab or Newline or CarriageReturn;
    public static bool IsAsciiDigit(this byte value) => value is >= Zero and <= Nine;
    public static bool IsAsciiLetter(this byte value) => value is >= UpperA and <= UpperZ or >= LowerA and <= LowerZ;
    public static byte AsciiLowercased(this byte value) => value.IsAsciiLetter() ? (byte)(value | 0x20) : value;
    public static bool IsXmlNameStart(this byte value) => value.IsAsciiLetter() || value is Underscore or Colon;
    public static bool IsXmlNameChar(this byte value) => value.IsXmlNameStart() || value.IsAsciiDigit() || value is Hyphen or Dot;

    public static uint? AsciiHexValue(this byte value)
    {
        if (value.IsAsciiDigit()) return (uint)(value - Zero);
        var lower = value | 0x20;
        return lower is >= LowerA and <= LowerF ? (uint)(lower - LowerA + 10) : null;
    }

    public static bool Equals(ReadOnlySpan<byte> value, ReadOnlySpan<byte> literal) => value.SequenceEqual(literal);

    public static bool EqualsCaseInsensitive(ReadOnlySpan<byte> value, ReadOnlySpan<byte> other)
    {
        if (value.Length != other.Length) return false;
        for (var i = 0; i < value.Length; i++)
            if (value[i].AsciiLowercased() != other[i].AsciiLowercased()) return false;
        return true;
    }
}
