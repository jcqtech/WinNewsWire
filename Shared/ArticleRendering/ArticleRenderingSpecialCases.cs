using System.Text.RegularExpressions;
using WinNewsWire.Web;

namespace WinNewsWire.AppShared.ArticleRendering;

/// <summary>
/// Port of NetNewsWire's <c>ArticleRenderingSpecialCases</c>. Some publisher
/// feeds — notably <c>theverge.com</c> — emit article HTML that contains
/// mis-encoded curly quotes, em dashes, and ellipses. Browsers display the
/// garbled mojibake; this filter rewrites the known sequences back to the
/// intended Unicode characters before the renderer hands the HTML to the
/// WebView.
/// </summary>
/// <remarks>
/// The string replacements are 1:1 with the Swift source so that test cases
/// derived from real Verge articles keep working when re-rendered on Windows.
/// </remarks>
public static class ArticleRenderingSpecialCases
{
    private static readonly Regex RedirectScript = new(@"<script[^>]*>[^<]*location\.href\s*=(?!=)[^<]*</script>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConsecutiveReturns = new(@"(?:\r?\n[ \t]*){2,}", RegexOptions.Compiled);
    private static readonly Regex StrayLineBreaks = new(
        @"(<(?:ul|ol|table|thead|tbody|tfoot|tr)\b[^>]*>|</(?:li|tr|td|th|thead|tbody|tfoot)>)(?:\s*<br\s*/?>)+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SanitizedFreshRssClass = new(@"(<[^>]*?)data-sanitized-class([^<]*?>)", RegexOptions.Compiled);
    private static readonly Regex SanitizedFreshRssId = new(@"(<[^>]*?)data-sanitized-id([^<]*?>)", RegexOptions.Compiled);

    public static bool ShouldDisableJavaScript(IEnumerable<string?> urls)
        => urls.Any(url => url is not null && SpecialCases.UrlStringMatchesDomain(url, ["slashdot.org"]));

    public static string InsertParagraphTagsIfNeeded(string html, string? feedUrl, string? homePageUrl)
        => ShouldDisableJavaScript([feedUrl, homePageUrl]) && html.Contains('\n')
            ? ConsecutiveReturns.Replace(html, "<p>") : html;

    public static Uri BaseUrlForRendering(Uri url)
        => url.IsYouTubeUrl() ? new Uri("https://netnewswire.com/") : url;

    public static string RemoveStrayLineBreaksIfNeeded(string html, string? feedUrl, string? homePageUrl)
        => new[] { feedUrl, homePageUrl }.Any(url => url is not null
            && SpecialCases.UrlStringMatchesDomain(url, ["macrumors.com"]))
            ? StrayLineBreaks.Replace(html, "$1") : html;

    public static string DesanitizeFreshRssContent(string html)
        => SanitizedFreshRssId.Replace(SanitizedFreshRssClass.Replace(html, "$1class$2"), "$1id$2");

    public static string RemoveLocationHrefRedirectScripts(string html)
        => html.Contains("location.href", StringComparison.Ordinal) ? RedirectScript.Replace(html, "") : html;

    /// <summary>
    /// Returns <paramref name="html"/> with any per-site sanitization applied.
    /// If <paramref name="baseUrl"/> isn't a recognized special-case host, the
    /// input is returned unchanged.
    /// </summary>
    public static string FilterHtmlIfNeeded(string? baseUrl, string html)
    {
        if (string.IsNullOrEmpty(html)) return html ?? string.Empty;
        html = RemoveLocationHrefRedirectScripts(html);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var url)) return html;
        if (IsVergeSpecialCase(url)) return FilterVergeHtml(html);
        return html;
    }

    /// <summary>True when the URL's host contains <c>theverge.com</c>.</summary>
    public static bool IsVergeSpecialCase(Uri baseUrl)
    {
        var host = baseUrl.Host;
        return !string.IsNullOrEmpty(host)
            && host.Contains("theverge.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces the common Verge mojibake sequences. The replacements are
    /// ordered carefully: longer ASCII fragments (e.g. <c>â€™</c>) are
    /// handled before shorter ones (e.g. <c>â€</c>) so the more specific
    /// pattern wins.
    /// </summary>
    public static string FilterVergeHtml(string html)
    {
        var s = html;

        // Right curly single quote (’)
        s = s.Replace("â€™", "\u2019");
        s = s.Replace("&acirc;&#128;&#153;", "\u2019");

        // Left curly double quote (“)
        s = s.Replace("â€œ", "\u201C");
        s = s.Replace("â&#128;&#156;", "\u201C");
        s = s.Replace("&acirc;&#128;&#156;", "\u201C");

        // Right curly double quote (”) — must follow the more specific
        // â€™ and â€œ patterns above since â€ is a prefix of both.
        s = s.Replace("\u00e2\u20ac\u009d\u009d\u009d\u009d", "\u201D");
        s = s.Replace("â&#128;&#157;", "\u201D");
        s = s.Replace("&acirc;&#128;&#157;", "\u201D");

        // Em dash (—)
        s = s.Replace("â€”", "\u2014");
        s = s.Replace("&acirc;&#128;&#148;", "\u2014");

        // Stray Â and &Acirc;&nbsp; sequences.
        s = s.Replace("Â", string.Empty);
        s = s.Replace("&Acirc;&nbsp;", string.Empty);

        // Double-encoded horizontal ellipsis (…)
        s = s.Replace(" &amp;hellip;", "\u2026");
        s = s.Replace("&amp;hellip;", "\u2026");

        return s;
    }

    public static string ExtractBodyFragmentIfNeeded(string html)
    {
        if (!html.Contains("<body", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("<html", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("<!doctype", StringComparison.OrdinalIgnoreCase))
            return html;
        int body = FindTag(html, "<body", 0);
        if (body >= 0)
        {
            int close = html.IndexOf('>', body + 5);
            if (close >= 0)
            {
                int end = FindTag(html, "</body", close + 1);
                return html[(close + 1)..(end >= 0 ? end : html.Length)];
            }
        }
        var result = html;
        int doctype = result.IndexOf("<!doctype", StringComparison.OrdinalIgnoreCase);
        if (doctype >= 0) result = RemoveTag(result, doctype);
        int head = FindTag(result, "<head", 0);
        if (head >= 0)
        {
            int end = FindTag(result, "</head", head + 5);
            if (end >= 0)
            {
                int close = result.IndexOf('>', end);
                if (close >= 0) result = result.Remove(head, close - head + 1);
            }
        }
        int openHtml = FindTag(result, "<html", 0);
        if (openHtml >= 0) result = RemoveTag(result, openHtml);
        int closeHtml = FindTag(result, "</html", 0);
        if (closeHtml >= 0) result = RemoveTag(result, closeHtml);
        return result;
    }

    private static string RemoveTag(string html, int start)
    {
        int end = html.IndexOf('>', start);
        return end >= 0 ? html.Remove(start, end - start + 1) : html;
    }

    private static int FindTag(string html, string tag, int start)
    {
        while (start < html.Length)
        {
            int found = html.IndexOf(tag, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            int end = found + tag.Length;
            if (end == html.Length || html[end] is '>' or '/' || char.IsWhiteSpace(html[end]))
                return found;
            start = end;
        }
        return -1;
    }
}
