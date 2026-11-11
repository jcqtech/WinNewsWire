using System.Text;

namespace WinNewsWire.Parsers.Xml;

internal static class XmlEntities
{
    internal enum Mode { Html, XmlStrict }
    internal readonly record struct DecodedEntity(ReadOnlyMemory<byte> Bytes, int NextIndex);

    private static readonly byte[] AmpersandBytes = [(byte)'&'];
    private static readonly Dictionary<string, byte[]> PredefinedEntities = new(StringComparer.Ordinal)
    {
        ["amp"] = AmpersandBytes, ["lt"] = [(byte)'<'], ["gt"] = [(byte)'>'],
        ["quot"] = [(byte)'"'], ["apos"] = [(byte)'\'']
    };

    internal static ReadOnlySpan<uint> WindowsLatin1Extension =>
    [
        0x20AC, 0x0081, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021,
        0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0x008D, 0x017D, 0x008F,
        0x0090, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014,
        0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0x009D, 0x017E, 0x0178
    ];

    private static readonly Dictionary<string, byte[]> HtmlNamedEntities = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AElig"] = "\u00C6", ["Aacute"] = "\u00C1", ["Acirc"] = "\u00C2", ["Agrave"] = "\u00C0",
        ["Aring"] = "\u00C5", ["Atilde"] = "\u00C3", ["Auml"] = "\u00C4", ["Ccedil"] = "\u00C7",
        ["Dstrok"] = "\u00D0", ["ETH"] = "\u00D0", ["Eacute"] = "\u00C9", ["Ecirc"] = "\u00CA",
        ["Egrave"] = "\u00C8", ["Euml"] = "\u00CB", ["Iacute"] = "\u00CD", ["Icirc"] = "\u00CE",
        ["Igrave"] = "\u00CC", ["Iuml"] = "\u00CF", ["Ntilde"] = "\u00D1", ["Oacute"] = "\u00D3",
        ["Ocirc"] = "\u00D4", ["Ograve"] = "\u00D2", ["Oslash"] = "\u00D8", ["Otilde"] = "\u00D5",
        ["Ouml"] = "\u00D6", ["Pi"] = "\u03A0", ["THORN"] = "\u00DE", ["Uacute"] = "\u00DA",
        ["Ucirc"] = "\u00DB", ["Ugrave"] = "\u00D9", ["Uuml"] = "\u00DC", ["Yacute"] = "Y",
        ["aacute"] = "\u00E1", ["acirc"] = "\u00E2", ["acute"] = "\u00B4", ["aelig"] = "\u00E6",
        ["agrave"] = "\u00E0", ["aring"] = "\u00E5", ["atilde"] = "\u00E3", ["auml"] = "\u00E4",
        ["brkbar"] = "\u00A6", ["brvbar"] = "\u00A6", ["ccedil"] = "\u00E7", ["cedil"] = "\u00B8",
        ["cent"] = "\u00A2", ["copy"] = "\u00A9", ["CounterClockwiseContourIntegral"] = "\u2233",
        ["curren"] = "\u00A4", ["deg"] = "\u00B0", ["die"] = "\u00A8", ["divide"] = "\u00F7",
        ["eacute"] = "\u00E9", ["ecirc"] = "\u00EA", ["egrave"] = "\u00E8", ["eth"] = "\u00F0",
        ["euml"] = "\u00EB", ["euro"] = "\u20AC", ["frac12"] = "\u00BD", ["frac14"] = "\u00BC",
        ["frac34"] = "\u00BE", ["hearts"] = "\u2665", ["hellip"] = "\u2026", ["iacute"] = "\u00ED",
        ["icirc"] = "\u00EE", ["iexcl"] = "\u00A1", ["igrave"] = "\u00EC", ["iquest"] = "\u00BF",
        ["iuml"] = "\u00EF", ["laquo"] = "\u00AB", ["ldquo"] = "\u201C", ["lsquo"] = "\u2018",
        ["macr"] = "\u00AF", ["mdash"] = "\u2014", ["micro"] = "\u00B5", ["middot"] = "\u00B7",
        ["ndash"] = "\u2013", ["not"] = "\u00AC", ["ntilde"] = "\u00F1", ["oacute"] = "\u00F3",
        ["ocirc"] = "\u00F4", ["ograve"] = "\u00F2", ["ordf"] = "\u00AA", ["ordm"] = "\u00BA",
        ["oslash"] = "\u00F8", ["otilde"] = "\u00F5", ["ouml"] = "\u00F6", ["para"] = "\u00B6",
        ["pi"] = "\u03C0", ["plusmn"] = "\u00B1", ["pound"] = "\u00A3", ["raquo"] = "\u00BB",
        ["rdquo"] = "\u201D", ["reg"] = "\u00AE", ["rsquo"] = "\u2019", ["sect"] = "\u00A7",
        ["smallcircle"] = "\u25E6", ["shy"] = "\u00AD", ["sup1"] = "\u00B9", ["sup2"] = "\u00B2",
        ["sup3"] = "\u00B3", ["szlig"] = "\u00DF", ["thorn"] = "\u00FE", ["times"] = "\u00D7",
        ["trade"] = "\u2122", ["uacute"] = "\u00FA", ["ucirc"] = "\u00FB", ["ugrave"] = "\u00F9",
        ["uml"] = "\u00A8", ["uuml"] = "\u00FC", ["yacute"] = "y", ["yen"] = "\u00A5",
        ["yuml"] = "\u00FF", ["infin"] = "\u221E", ["nbsp"] = "\u00A0", ["zwj"] = "\u200D", ["zwnj"] = "\u200C"
    }.ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value), StringComparer.Ordinal);

    private static readonly int MaxEntityLength = HtmlNamedEntities.Keys.Max(name => name.Length) + 1;

    public static DecodedEntity Decode(ReadOnlySpan<byte> bytes, int at, Mode mode)
    {
        if ((uint)at >= bytes.Length || bytes[at] != XmlAscii.Ampersand)
            throw new ArgumentException("Entity decoding must start at an ampersand.", nameof(at));

        var searchEnd = Math.Min(bytes.Length, at + 1 + MaxEntityLength);
        var semicolon = -1;
        for (var i = at + 1; i < searchEnd; i++)
        {
            var b = bytes[i];
            if (b == XmlAscii.Semicolon) { semicolon = i; break; }
            if (b.IsAsciiWhitespace() || b is XmlAscii.Ampersand or XmlAscii.LessThan) break;
        }

        if (semicolon <= at + 1) return LiteralAmpersand(at);
        var name = bytes[(at + 1)..semicolon];
        if (name[0] == XmlAscii.Hash)
        {
            var numeric = DecodeNumeric(name[1..]);
            return numeric is null ? LiteralAmpersand(at) : new(numeric, semicolon + 1);
        }

        var key = Encoding.UTF8.GetString(name);
        if (PredefinedEntities.TryGetValue(key, out var predefined)) return new(predefined, semicolon + 1);
        if (mode == Mode.Html && HtmlNamedEntities.TryGetValue(key, out var html)) return new(html, semicolon + 1);
        return LiteralAmpersand(at);
    }

    private static DecodedEntity LiteralAmpersand(int at) => new(AmpersandBytes, at + 1);

    private static byte[]? DecodeNumeric(ReadOnlySpan<byte> digits)
    {
        if (digits.IsEmpty) return null;
        var hex = digits[0] is XmlAscii.LowerX or XmlAscii.UpperX;
        if (hex) digits = digits[1..];
        uint codepoint = 0;
        foreach (var b in digits)
        {
            var value = hex ? b.AsciiHexValue() : b.IsAsciiDigit() ? (uint?)(b - XmlAscii.Zero) : null;
            if (value is null) return null;
            codepoint = codepoint * (hex ? 16u : 10u) + value.Value;
            if (codepoint > 0x10FFFF) return null;
        }
        if (codepoint is >= 0x80 and <= 0x9F)
            codepoint = WindowsLatin1Extension[(int)codepoint - 0x80];
        if (codepoint == 0 || codepoint is >= 0xD800 and <= 0xDFFF) return null;
        Span<byte> encoded = stackalloc byte[4];
        var length = new Rune(codepoint).EncodeToUtf8(encoded);
        return encoded[..length].ToArray();
    }
}
