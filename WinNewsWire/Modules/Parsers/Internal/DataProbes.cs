using System.Text;

namespace WinNewsWire.Parsers.Internal;

/// <summary>
/// Port of the byte-sniffing helpers in <c>NSData+RSParser.m</c>. We use
/// <see cref="ReadOnlySpan{T}"/> of bytes and avoid allocating a string for the check.
/// </summary>
internal static class DataProbes
{
    private const int ProbeWindow = 4096;

    public static bool IsProbablyJson(ReadOnlySpan<byte> d)
    {
        foreach (var b in SkipWhitespaceAndBom(d))
        {
            return b == (byte)'{' || b == (byte)'[';
        }
        return false;
    }

    public static bool IsProbablyJsonFeed(ReadOnlySpan<byte> d)
    {
        if (!IsProbablyJson(d)) return false;
        // The version URL may be encoded as "https://jsonfeed.org/version/1" or
        // with escaped slashes "https:\/\/jsonfeed.org\/version\/1". Check for the
        // shared substring.
        return ContainsAsciiCaseInsensitive(d, "jsonfeed.org");
    }

    public static bool IsProbablyRssInJson(ReadOnlySpan<byte> d)
    {
        if (!IsProbablyJson(d)) return false;
        return ContainsAsciiCaseInsensitive(d, "\"rss\"") &&
               ContainsAsciiCaseInsensitive(d, "\"channel\"");
    }

    public static bool IsProbablyRss(ReadOnlySpan<byte> d)
    {
        var head = d[..Math.Min(ProbeWindow, d.Length)];
        if (ContainsAsciiCaseInsensitive(head, "<rss")) return true;
        if (ContainsAsciiCaseInsensitive(head, "<rdf:rdf") &&
            ContainsAsciiCaseInsensitive(head, "purl.org/rss/")) return true;
        return false;
    }

    public static bool IsProbablyAtom(ReadOnlySpan<byte> d)
    {
        var head = d[..Math.Min(ProbeWindow, d.Length)];
        if (!ContainsAsciiCaseInsensitive(head, "<feed")) return false;
        return ContainsAsciiCaseInsensitive(head, "www.w3.org/2005/atom");
    }

    private static ReadOnlySpan<byte> SkipWhitespaceAndBom(ReadOnlySpan<byte> d)
    {
        int i = 0;
        // UTF-8 BOM
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) i = 3;
        while (i < d.Length)
        {
            var b = d[i];
            if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n') { i++; continue; }
            break;
        }
        return d[i..];
    }

    private static bool ContainsAsciiCaseInsensitive(ReadOnlySpan<byte> haystack, string needle)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;
        Span<byte> lower = stackalloc byte[needle.Length];
        for (int i = 0; i < needle.Length; i++) lower[i] = (byte)char.ToLowerInvariant(needle[i]);
        int max = haystack.Length - needle.Length;
        for (int i = 0; i <= max; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
            {
                var c = haystack[i + j];
                if (c >= (byte)'A' && c <= (byte)'Z') c = (byte)(c + 32);
                if (c != lower[j]) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }
}
