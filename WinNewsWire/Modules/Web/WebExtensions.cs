using System.Collections.Generic;
using System.Text;

namespace WinNewsWire.Web;

/// <summary>
/// Port of NetNewsWire RSWeb's <c>Dictionary+RSWeb</c> and <c>String+RSWeb</c>
/// helpers used when constructing remote-account API requests.
/// </summary>
public static class WebExtensions
{
    /// <summary>
    /// Port of <c>Dictionary.urlQueryString</c>. Encodes each key and value
    /// with percent-encoding suitable for an HTTP form/query body, then
    /// joins them with <c>&amp;</c>. Multibyte characters become UTF-8 bytes
    /// before percent-encoding.
    /// </summary>
    public static string UrlQueryString(this IReadOnlyDictionary<string, string> dict)
    {
        var parts = new List<string>(dict.Count);
        foreach (var (key, value) in dict)
            parts.Add($"{PercentEncode(key)}={PercentEncode(value)}");
        return string.Join("&", parts);
    }

    /// <summary>
    /// Port of <c>String.escapedHTML</c>. Escapes the five HTML/XML special
    /// characters (<![CDATA[<, >, &, ", ']]>). Mirrors the macOS app's
    /// behaviour: <c>'</c> becomes <c>&amp;apos;</c> (NOT the more common
    /// <c>&amp;#39;</c>), and amp/quot are spelled out.
    /// </summary>
    public static string EscapedHtml(this string s)
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
    /// Percent-encodes <paramref name="s"/> using the same character set as
    /// <c>CharacterSet.urlQueryAllowed</c> with the additional reserved
    /// characters (<c>:#[]@!$&amp;'()*+,;=?/</c>) escaped — what NNW does for
    /// query items inside <c>urlQueryString</c>. Space is encoded as
    /// <c>%20</c>, not <c>+</c>.
    /// </summary>
    private static string PercentEncode(string s)
    {
        var sb = new StringBuilder(s.Length);
        var bytes = Encoding.UTF8.GetBytes(s);
        foreach (var b in bytes)
        {
            if (IsUnreservedQueryChar(b)) sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static bool IsUnreservedQueryChar(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z') ||
        (b >= (byte)'a' && b <= (byte)'z') ||
        (b >= (byte)'0' && b <= (byte)'9') ||
        b == (byte)'-' || b == (byte)'_' || b == (byte)'.' || b == (byte)'~';
}
