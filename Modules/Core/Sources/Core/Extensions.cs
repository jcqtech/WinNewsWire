using System.Security.Cryptography;
using System.Text;

namespace WinNewsWire.Core;

public static class HashExtensions
{
    public static string Md5Hex(this string input) => ToHex(MD5.HashData(Encoding.UTF8.GetBytes(input)));
    public static string Sha256Hex(this string input) => ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    public static string Md5Hex(this byte[] bytes) => ToHex(MD5.HashData(bytes));
    public static string HmacUsingSha1(this string input, string key)
        => ToHex(HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(input)));

    private static string ToHex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }
}

public static partial class StringExtensions
{
    public static string? NilIfEmpty(this string? s) => string.IsNullOrEmpty(s) ? null : s;
    public static string? NilIfEmptyOrWhitespace(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    public static bool ContainsIgnoreCase(this string s, string other)
        => s.Contains(other, StringComparison.OrdinalIgnoreCase);

    public static string CollapsingWhitespace(this string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastWs = true;
        foreach (var c in s)
        {
            if (c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r')
            {
                if (!lastWs) sb.Append(' ');
                lastWs = true;
            }
            else { sb.Append(c); lastWs = false; }
        }
        return sb.ToString().TrimEnd(' ');
    }

    /// <summary>
    /// Port of <c>String.trimmingWhitespace</c>. Trims leading and trailing
    /// whitespace (matches Swift's <c>.whitespacesAndNewlines</c>) without
    /// touching interior whitespace.
    /// </summary>
    public static string TrimmingWhitespace(this string s) => s.Trim();

    /// <summary>
    /// Port of <c>String.stripping(prefix:caseSensitive:)</c>. Returns
    /// <paramref name="s"/> unchanged when it doesn't start with
    /// <paramref name="prefix"/>; otherwise drops the first occurrence.
    /// </summary>
    public static string StrippingPrefix(this string s, string prefix, bool caseSensitive = false)
    {
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return s.StartsWith(prefix, cmp) ? s[prefix.Length..] : s;
    }

    /// <summary>
    /// Port of <c>String.stripping(suffix:caseSensitive:)</c>.
    /// </summary>
    public static string StrippingSuffix(this string s, string suffix, bool caseSensitive = false)
    {
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return s.EndsWith(suffix, cmp) ? s[..^suffix.Length] : s;
    }

    /// <summary>
    /// Port of <c>String.escapingSpecialXMLCharacters</c>. Escapes the five
    /// XML special characters (<![CDATA[<, >, &, ", ']]>).
    /// </summary>
    public static string EscapingSpecialXmlCharacters(this string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    if (rune.Value is 0x9 or 0xA or 0xD
                        or >= 0x20 and <= 0xD7FF
                        or >= 0xE000 and <= 0xFFFD
                        or >= 0x10000 and <= 0x10FFFF)
                        sb.Append(rune.ToString());
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Port of <c>String.strippingHTTPOrHTTPSScheme</c>. Strips a leading
    /// <c>http://</c> or <c>https://</c>; other schemes pass through.
    /// </summary>
    public static string StrippingHttpOrHttpsScheme(this string s)
    {
        if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return s[8..];
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return s[7..];
        return s;
    }

    /// <summary>
    /// Port of <c>String.normalizedURL</c>. Strips <c>feed:</c>, <c>feeds:</c>,
    /// <c>feed://</c>, <c>feeds://</c> wrappers; defaults to <c>http://</c> for
    /// <c>feed:</c> (insecure default) and <c>https://</c> for <c>feeds:</c>
    /// (secure default) when no scheme follows.
    /// </summary>
    public static string NormalizedUrl(this string s)
    {
        var input = s.Trim();
        bool secure = false;

        // Order matters: check 'feeds' first because 'feed' is a prefix.
        if (input.StartsWith("feeds:", StringComparison.OrdinalIgnoreCase))
        {
            secure = true;
            input = input["feeds:".Length..];
        }
        else if (input.StartsWith("feed:", StringComparison.OrdinalIgnoreCase))
        {
            input = input["feed:".Length..];
        }
        // Strip optional leading // after feed:/feeds:.
        if (input.StartsWith("//", StringComparison.Ordinal)) input = input[2..];

        if (!input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            input = (secure ? "https://" : "http://") + input;
        if (input.Count(character => character == '/') == 2) input += "/";
        return input;
    }

}

public static class UriExtensions
{
    public static Uri? PercentEncodedEmailAddress(this Uri url)
        => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeMailto ? url : null;

    public static bool IsAbsoluteHttp(string? s)
        => !string.IsNullOrWhiteSpace(s)
        && Uri.TryCreate(s, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public static string? ResolveAgainst(string? relativeOrAbsolute, string? baseUrl)
    {
        if (string.IsNullOrEmpty(relativeOrAbsolute)) return null;
        if (IsAbsoluteHttp(relativeOrAbsolute)) return relativeOrAbsolute;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)) return relativeOrAbsolute;
        return Uri.TryCreate(b, relativeOrAbsolute, out var r) ? r.AbsoluteUri : relativeOrAbsolute;
    }

    public static string? NormalizedHost(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var h = u.Host;
        return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
    }
}
