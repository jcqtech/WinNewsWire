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
    /// <summary>
    /// Returns <paramref name="html"/> with any per-site sanitization applied.
    /// If <paramref name="baseUrl"/> isn't a recognized special-case host, the
    /// input is returned unchanged.
    /// </summary>
    public static string FilterHtmlIfNeeded(string? baseUrl, string html)
    {
        if (string.IsNullOrEmpty(html)) return html ?? string.Empty;
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
        s = s.Replace("â€", "\u201D");
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
}
