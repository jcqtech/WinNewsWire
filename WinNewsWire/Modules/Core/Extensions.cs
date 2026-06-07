using System.Security.Cryptography;
using System.Text;

namespace WinNewsWire.Core;

public static class HashExtensions
{
    public static string Md5Hex(this string input) => ToHex(MD5.HashData(Encoding.UTF8.GetBytes(input)));
    public static string Sha256Hex(this string input) => ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    public static string Md5Hex(this byte[] bytes) => ToHex(MD5.HashData(bytes));

    private static string ToHex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }
}

public static class StringExtensions
{
    public static string? NilIfEmpty(this string? s) => string.IsNullOrEmpty(s) ? null : s;
    public static string? NilIfEmptyOrWhitespace(this string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    public static bool ContainsIgnoreCase(this string s, string other)
        => s.Contains(other, StringComparison.OrdinalIgnoreCase);

    public static string CollapsingWhitespace(this string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastWs = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { if (!lastWs) sb.Append(' '); lastWs = true; }
            else { sb.Append(c); lastWs = false; }
        }
        return sb.ToString().Trim();
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
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
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
        var input = s;
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
        else
        {
            return s;
        }

        // Strip optional leading // after feed:/feeds:.
        if (input.StartsWith("//", StringComparison.Ordinal)) input = input[2..];

        // If the remainder already has its own scheme, return that scheme directly.
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return input;

        // Add trailing slash if missing (matches Swift's normalization).
        if (!input.Contains('/')) input += "/";
        return (secure ? "https://" : "http://") + input;
    }

    public static string StripHtml(this string s) => StripHtml(s, maxCharacters: null);

    /// <summary>
    /// Removes HTML tags from <paramref name="s"/>, dropping any content inside
    /// <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> blocks, collapsing runs of
    /// whitespace, and optionally truncating to <paramref name="maxCharacters"/>
    /// code units. Port of NetNewsWire's <c>String.strippingHTML</c>.
    /// </summary>
    public static string StripHtml(this string s, int? maxCharacters)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        if (s.IndexOf('<') < 0)
        {
            // Fast path: no tags. Honor the caller's maxCharacters cap.
            return maxCharacters is int max && s.Length > max ? s[..max] : s;
        }

        // Match the macOS preflight: block-level tags become whitespace so
        // adjacent words don't run together, and <script>/<style> blocks lose
        // their interior content along with the wrapper tags.
        var preflight = s;
        preflight = BlockTagSpaceRegex.Replace(preflight, " ");
        preflight = BlockTagNewlineRegex.Replace(preflight, "\n");
        preflight = ScriptBlockRegex.Replace(preflight, string.Empty);
        preflight = StyleBlockRegex.Replace(preflight, string.Empty);

        var max2 = maxCharacters ?? preflight.Length;
        var sb = new StringBuilder(Math.Min(max2, preflight.Length));
        bool lastWasSpace = false;
        int level = 0;
        int added = 0;
        bool sawNonWhitespace = false;

        foreach (var c in preflight)
        {
            if (c == '<') { level++; continue; }
            if (c == '>') { level--; continue; }
            if (level != 0) continue;

            if (c == ' ' || c == '\r' || c == '\t' || c == '\n')
            {
                // Skip leading whitespace entirely so it doesn't burn through the
                // maxCharacters budget. After we've seen at least one real
                // character, collapse runs of whitespace into a single space.
                if (!sawNonWhitespace) continue;
                if (lastWasSpace) continue;
                lastWasSpace = true;
                sb.Append(' ');
            }
            else
            {
                lastWasSpace = false;
                sawNonWhitespace = true;
                sb.Append(c);
            }

            added++;
            if (added >= max2) break;
        }

        var result = sb.ToString().Trim();
        // Maintain post-trim cap so callers always see <= maxCharacters out.
        if (maxCharacters is int cap && result.Length > cap) result = result[..cap];
        return result;
    }

    // `</?(?:blockquote|p|div)>` — block-level open/close tags collapse to a space.
    private static readonly System.Text.RegularExpressions.Regex BlockTagSpaceRegex =
        new(@"</?(?:blockquote|p|div)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // `<p>|</?div>|<br(?: ?/)?>|</li>` — line-break inducing tags. The <p> and
    // </div> alternatives are already stripped by BlockTagSpace; <br>/</li>
    // survive that pass.
    private static readonly System.Text.RegularExpressions.Regex BlockTagNewlineRegex =
        new(@"<p>|</?div>|<br(?: ?/)?>|</li>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // <script>…</script> — non-greedy across newlines.
    private static readonly System.Text.RegularExpressions.Regex ScriptBlockRegex =
        new(@"<script\b[^>]*>[\s\S]*?</script>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // <style>…</style> — same shape as script.
    private static readonly System.Text.RegularExpressions.Regex StyleBlockRegex =
        new(@"<style\b[^>]*>[\s\S]*?</style>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
}

public static class UriExtensions
{
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
