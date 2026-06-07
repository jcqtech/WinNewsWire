using System.Net;
using System.Reflection;
using System.Text;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.ArticleRendering;

public enum ArticleTextSize { Small, Medium, Large, XLarge, XXLarge }

public sealed record ExtractedArticle(string? Title, string? Author, string? DatePublished, string? Content, string? Url);

public sealed record ArticleRendering(string Style, string Html, string Title, string BaseUrl);

/// <summary>Port of <c>ArticleRenderer</c>. Pulls template + stylesheet from the currently
/// selected <see cref="ArticleTheme"/> so users can drop in a <c>.nnwtheme</c> bundle and have
/// articles restyled.</summary>
public static class ArticleRenderer
{
    public static ArticleRendering Render(Article? article, ExtractedArticle? extracted = null, ArticleTextSize size = ArticleTextSize.Medium)
    {
        var theme = ArticleThemesManager.Shared.CurrentTheme;
        var title = article?.Title is null ? "" : WebUtility.HtmlEncode(article.Title);
        var author = article?.Authors?.FirstOrDefault()?.Name ?? "";
        var date = ArticleStringFormatter.DateString(article?.DatePublished);
        var rawBody = extracted?.Content ?? article?.ContentHtml ?? article?.ContentText ?? "";
        var baseUrl = article?.RawLink ?? "";
        // Per-site mojibake cleanup before the body lands in the WebView. Mirrors
        // NetNewsWire's `DetailWebViewController.filterHTMLIfNeeded` step.
        var body = ArticleRenderingSpecialCases.FilterHtmlIfNeeded(baseUrl, rawBody);

        var sb = new StringBuilder(theme.TemplateHtml);
        sb.Replace("[[[title]]]", title);
        sb.Replace("[[[body]]]", body);
        sb.Replace("[[[author]]]", WebUtility.HtmlEncode(author));
        sb.Replace("[[[datePublished]]]", WebUtility.HtmlEncode(date));
        sb.Replace("[[[externalURL]]]", WebUtility.HtmlEncode(article?.RawExternalLink ?? ""));
        sb.Replace("[[[baseURL]]]", WebUtility.HtmlEncode(baseUrl));
        sb.Replace("[[[textSizeClass]]]", TextSizeClass(size));
        sb.Replace("[[[styleSheet]]]", theme.StylesheetCss);
        return new ArticleRendering(theme.StylesheetCss, sb.ToString(), title, baseUrl);
    }

    public static string BlankPage() => "<!doctype html><html><body></body></html>";

    private static string TextSizeClass(ArticleTextSize s) => s switch
    {
        ArticleTextSize.Small => "articleTextSmall",
        ArticleTextSize.Medium => "articleTextMedium",
        ArticleTextSize.Large => "articleTextLarge",
        ArticleTextSize.XLarge => "articleTextXLarge",
        ArticleTextSize.XXLarge => "articleTextXXLarge",
        _ => "articleTextMedium",
    };

    private static string? LoadResource(string name)
    {
        var asm = typeof(ArticleRenderer).Assembly;
        var full = asm.GetName().Name + ".Resources." + name;
        using var s = asm.GetManifestResourceStream(full);
        if (s is null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
