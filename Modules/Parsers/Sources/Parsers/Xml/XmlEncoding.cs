using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace WinNewsWire.Parsers.Xml;

internal static class XmlEncoding
{
    private static readonly Dictionary<string, int> CodePages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iso-8859-2"] = 28592, ["latin2"] = 28592, ["iso-8859-5"] = 28595,
        ["iso-8859-9"] = 28599, ["iso-8859-15"] = 28605,
        ["windows-1251"] = 1251, ["cp1251"] = 1251, ["koi8-r"] = 20866,
        ["shift_jis"] = 932, ["shift-jis"] = 932, ["sjis"] = 932,
        ["euc-jp"] = 51932, ["iso-2022-jp"] = 50220,
        ["gb2312"] = 936, ["gbk"] = 936, ["gb18030"] = 54936, ["big5"] = 950, ["euc-kr"] = 51949,
        ["windows-1250"] = 1250, ["cp1250"] = 1250,
        ["windows-1253"] = 1253, ["cp1253"] = 1253,
        ["windows-1254"] = 1254, ["cp1254"] = 1254
    };

    public static byte[] ToUtf8(byte[] input)
    {
        if (input.AsSpan().StartsWith<byte>([0xEF, 0xBB, 0xBF])) return input[3..];
        if (input.AsSpan().StartsWith<byte>([0xFF, 0xFE]))
            return Transcode(input.AsSpan(2), new UnicodeEncoding(false, false, true)) ?? input;
        if (input.AsSpan().StartsWith<byte>([0xFE, 0xFF]))
            return Transcode(input.AsSpan(2), new UnicodeEncoding(true, false, true)) ?? input;

        var name = DetectEncodingFromDeclaration(input);
        if (name is null) return input;
        switch (name.ToLowerInvariant())
        {
            case "utf-8": case "utf8": case "us-ascii": case "ascii": return input;
            case "iso-8859-1": case "latin1": case "latin-1":
                return Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(input));
            case "windows-1252": case "cp1252": return TranscodeWindows1252(input);
            case "utf-16": case "utf16": case "utf-16be":
                return Transcode(input, new UnicodeEncoding(true, false, true)) ?? input;
            case "utf-16le":
                return Transcode(input, new UnicodeEncoding(false, false, true)) ?? input;
        }
        if (CodePages.TryGetValue(name, out var codePage))
        {
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(
                codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                ?? throw new NotSupportedException($"The runtime cannot decode XML code page {codePage}.");
            return Transcode(input, encoding) ?? input;
        }
        Trace.TraceWarning($"Unrecognized XML encoding '{name}'; preserving the original bytes.");
        return input;
    }

    private static string? DetectEncodingFromDeclaration(ReadOnlySpan<byte> input)
    {
        var declaration = input[..Math.Min(input.Length, 200)];
        var i = 0;
        while (i < declaration.Length && declaration[i].IsAsciiWhitespace()) i++;
        declaration = declaration[i..];
        if (!declaration.StartsWith("<?xml"u8)) return null;
        var end = declaration.IndexOf("?>"u8);
        if (end >= 0) declaration = declaration[..end];
        var encodingStart = -1;
        for (i = 0; i <= declaration.Length - 8; i++)
        {
            if (!XmlAscii.EqualsCaseInsensitive(declaration.Slice(i, 8), "encoding"u8)) continue;
            encodingStart = i;
            break;
        }
        if (encodingStart < 0) return null;
        i = encodingStart + 8;
        while (i < declaration.Length && declaration[i].IsAsciiWhitespace()) i++;
        if (i >= declaration.Length || declaration[i++] != XmlAscii.EqualsSign) return null;
        while (i < declaration.Length && declaration[i].IsAsciiWhitespace()) i++;
        if (i >= declaration.Length || declaration[i] is not (XmlAscii.DoubleQuote or XmlAscii.SingleQuote)) return null;
        var quote = declaration[i++];
        var start = i;
        while (i < declaration.Length && declaration[i] != quote) i++;
        return i < declaration.Length ? Encoding.UTF8.GetString(declaration[start..i]) : null;
    }

    private static byte[]? Transcode(ReadOnlySpan<byte> input, Encoding encoding)
    {
        try { return Encoding.UTF8.GetBytes(encoding.GetString(input)); }
        catch (DecoderFallbackException error)
        {
            Trace.TraceWarning($"Invalid {encoding.WebName} XML bytes; preserving the original bytes: {error.Message}");
            return null;
        }
    }

    private static byte[] TranscodeWindows1252(ReadOnlySpan<byte> input)
    {
        var output = new ArrayBufferWriter<byte>(Math.Max(1, input.Length));
        foreach (var b in input)
        {
            var codepoint = b is >= 0x80 and <= 0x9F ? XmlEntities.WindowsLatin1Extension[b - 0x80] : b;
            var length = new Rune(codepoint).EncodeToUtf8(output.GetSpan(4));
            output.Advance(length);
        }
        return output.WrittenSpan.ToArray();
    }
}
