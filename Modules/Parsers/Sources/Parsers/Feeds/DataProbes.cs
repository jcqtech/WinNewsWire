using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

internal static class DataProbes
{
    public static bool IsProbablyXml(ReadOnlySpan<byte> bytes) => StartsWithAscii(bytes, "<?xml"u8);
    public static bool IsProbablyJson(ReadOnlySpan<byte> bytes) => StartsWithAscii(bytes, "{"u8);
    public static bool IsProbablyJsonFeed(ReadOnlySpan<byte> bytes)
        => IsProbablyJson(bytes) && (bytes.IndexOf("://jsonfeed.org/version/"u8) >= 0 || bytes.IndexOf(":\\/\\/jsonfeed.org\\/version\\/"u8) >= 0);
    public static bool IsProbablyRssInJson(ReadOnlySpan<byte> bytes)
        => IsProbablyJson(bytes) && bytes.IndexOf("rss"u8) >= 0 && bytes.IndexOf("channel"u8) >= 0 && bytes.IndexOf("item"u8) >= 0;
    public static bool IsProbablyRss(ReadOnlySpan<byte> bytes)
        => bytes.IndexOf("<rss"u8) >= 0 || bytes.IndexOf("<rdf:RDF"u8) >= 0
            || bytes.IndexOf("<channel>"u8) >= 0 && bytes.IndexOf("<pubDate>"u8) >= 0;
    public static bool IsProbablyAtom(ReadOnlySpan<byte> bytes) => bytes.IndexOf("<feed"u8) >= 0;

    private static bool StartsWithAscii(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty) return true;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i].IsAsciiWhitespace()) continue;
            if (bytes[i] == needle[0]) return bytes[i..].StartsWith(needle);
            if (i >= 4) return false;
        }
        return false;
    }
}
