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

        foreach (var c in preflight)
        {
            if (c == '<') { level++; continue; }
            if (c == '>') { level--; continue; }
            if (level != 0) continue;

            if (c == ' ' || c == '\r' || c == '\t' || c == '\n')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
                sb.Append(' ');
            }
            else
            {
                lastWasSpace = false;
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
