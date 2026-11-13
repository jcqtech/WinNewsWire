using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Parsers;

namespace WinNewsWire.AppShared.ArticleRendering;

public enum ArticleTextSize { Small, Medium, Large, XLarge, XXLarge }
public sealed record ExtractedArticle(string? Title, string? Author, string? DatePublished, string? Content, string? Url);
public sealed record ArticleRendering(string Style, string Html, string Title, string BaseUrl, string? ThemeFolder = null);

public static class ArticleRenderer
{
    private static readonly Regex LegacyMacro = new(@"\[\[\[([A-Za-z_]+)\]\]\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AppleFontFamily = new(@"(font-family\s*:\s*)-apple-system(?=\s*[,;])", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Lazy<string> Scripts = new(() => ArticleTheme.ReadResource("main_windows.js") + "\n;\n"
        + ArticleTheme.ReadResource("main.js") + "\n;\n" + ArticleTheme.ReadResource("newsfoot.js"));

    public static string BrowserScripts => Scripts.Value;

    public static ArticleRendering Render(Article? article, ExtractedArticle? extracted = null,
        ArticleTextSize size = ArticleTextSize.Medium, Account.Feed? feed = null, ArticleTheme? theme = null, string? avatarUrl = null,
        Account.AccountType? accountType = null)
    {
        theme ??= ArticleThemesManager.Shared.CurrentTheme;
        if (article is null) return NoSelection(theme, size);
        var title = ArticleStringFormatter.SanitizedTitle(article.Title, forHtml: true) ?? "";
        var baseUrl = BaseUrl(article, feed, extracted?.Url);
        var rawBody = extracted?.Content ?? article.ContentHtml
            ?? (article.ContentText is { } text ? "<pre>" + Html(text) + "</pre>" : article.Summary ?? "");
        var body = ArticleRenderingSpecialCases.ExtractBodyFragmentIfNeeded(rawBody);
        if (extracted?.Content is null)
        {
            body = ArticleRenderingSpecialCases.InsertParagraphTagsIfNeeded(body, feed?.Url, feed?.HomePageUrl);
            body = ArticleRenderingSpecialCases.RemoveStrayLineBreaksIfNeeded(body, feed?.Url, feed?.HomePageUrl);
            if (accountType == Account.AccountType.FreshRSS)
                body = ArticleRenderingSpecialCases.DesanitizeFreshRssContent(body);
            if (!string.IsNullOrEmpty(article.RawImageLink) && !body.Contains("<img", StringComparison.OrdinalIgnoreCase))
                body = "<p><img src=\"" + Html(article.RawImageLink) + "\" alt=\"\"></p>" + body;
        }
        body = ArticleRenderingSpecialCases.FilterHtmlIfNeeded(baseUrl, body);
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) body = HtmlRelativeUrlResolver.Resolve(body, baseUri);

        var preferredLink = article.PreferredLink() ?? "";
        var externalLink = article.RawExternalLink;
        var hasExternalLink = !string.IsNullOrEmpty(externalLink) && externalLink != preferredLink;
        var byline = Byline(article.Authors ?? feed?.Authors, feed?.NameForDisplay);
        var style = Style(theme, size);
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["preferred_link"] = Html(preferredLink),
            ["external_link_label"] = hasExternalLink ? "Link:" : "",
            ["external_link_stripped"] = hasExternalLink ? Html(Regex.Replace(externalLink!, "^https?://", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) : "",
            ["external_link"] = hasExternalLink ? Html(externalLink) : "",
            ["body"] = body,
            ["text_size_class"] = TextSizeClass(size),
            ["avatar_src"] = Html(avatarUrl ?? feed?.FaviconUrl ?? feed?.IconUrl ?? ""),
            ["dateline_style"] = title.Length == 0 ? "articleDatelineTitle" : "articleDateline",
            ["feed_link_title"] = Html(feed?.NameForDisplay),
            ["feed_link"] = Html(feed?.HomePageUrl),
            ["byline"] = byline,
            ["author"] = byline,
            ["datePublished"] = Html(ArticleStringFormatter.Shared.DateString(article.DatePublished)),
            ["externalURL"] = Html(article.RawExternalLink),
            ["baseURL"] = Html(baseUrl),
            ["textSizeClass"] = TextSizeClass(size),
            ["styleSheet"] = style
        };
        AddDateSubstitutions(substitutions, article.LogicalDatePublished());
        var template = LegacyMacro.Replace(theme.TemplateHtml, match => substitutions.ContainsKey(match.Groups[1].Value)
            ? "[[" + match.Groups[1].Value + "]]" : match.Value);
        return new(style, MacroProcessor.RenderedTextWith(template, substitutions), title, baseUrl, ThemeFolder(theme));
    }

    public static ArticleRendering MultipleSelection(ArticleTheme theme, ArticleTextSize size = ArticleTextSize.Medium)
        => Message("Multiple selection", theme, size);
    public static ArticleRendering Loading(ArticleTheme theme, ArticleTextSize size = ArticleTextSize.Medium)
        => Message("Loading\u2026", theme, size);
    public static ArticleRendering NoSelection(ArticleTheme theme, ArticleTextSize size = ArticleTextSize.Medium)
        => Message("No selection", theme, size);
    public static ArticleRendering NoContent(ArticleTheme theme, ArticleTextSize size = ArticleTextSize.Medium)
        => new(Style(theme, size), "", "", "", ThemeFolder(theme));

    public static string Document(ArticleRendering rendering, string? stylesheetUrl = null, bool includeScripts = true)
    {
        var style = stylesheetUrl is null ? "<style>" + rendering.Style + "</style>"
            : "<style>" + ArticleTheme.ReadResource("core.css") + "</style><link rel=\"stylesheet\" href=\"" + Html(stylesheetUrl) + "\">";
        var baseTag = rendering.BaseUrl.Length == 0 ? "" : "<base href=\"" + Html(rendering.BaseUrl) + "\">";
        return "<html dir=\"auto\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + "<title>" + Html(SimpleHtml.Parse(rendering.Title).Text) + "</title>" + style + baseTag
            + (includeScripts ? "<script>" + BrowserScripts + "</script>" : "")
            + "</head><body>" + rendering.Html + "</body></html>";
    }

    public static string BlankPage() => "<html><head><meta charset=\"utf-8\"></head><body></body></html>";

    public static string TextSizeClass(ArticleTextSize size) => size switch
    {
        ArticleTextSize.Small => "smallText",
        ArticleTextSize.Medium => "mediumText",
        ArticleTextSize.Large => "largeText",
        ArticleTextSize.XLarge => "xLargeText",
        ArticleTextSize.XXLarge => "xxLargeText",
        _ => "mediumText"
    };

    private static ArticleRendering Message(string message, ArticleTheme theme, ArticleTextSize size)
        => new(Style(theme, size), "<h3 class='systemMessage'>" + Html(message) + "</h3>", "", "", ThemeFolder(theme));

    private static string? ThemeFolder(ArticleTheme theme) => theme.IsAppTheme ? null : theme.Path;

    private static string Style(ArticleTheme theme, ArticleTextSize size)
    {
        var pixels = size switch { ArticleTextSize.Small => 14, ArticleTextSize.Medium => 16, ArticleTextSize.Large => 18, ArticleTextSize.XLarge => 20, ArticleTextSize.XXLarge => 22, _ => 16 };
        var style = MacroProcessor.RenderedTextWith(theme.StylesheetCss,
            new Dictionary<string, string> { ["font-size"] = pixels.ToString(CultureInfo.InvariantCulture) });
        if (theme.UsesGrayscaleIcon) style += "\n#nnwImageIcon { filter: grayscale(1); }\n";
        return AppleFontFamily.Replace(style, "$1\"Segoe UI Variable\", \"Segoe UI\", sans-serif");
    }

    private static string BaseUrl(Article article, Account.Feed? feed, string? extractedUrl)
    {
        foreach (var candidate in new[] { extractedUrl, article.RawLink, feed?.HomePageUrl, feed?.Url })
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) continue;
            var withoutFragment = new UriBuilder(url) { Fragment = "" }.Uri;
            return ArticleRenderingSpecialCases.BaseUrlForRendering(withoutFragment).AbsoluteUri;
        }
        return "";
    }

    private static string Byline(IReadOnlySet<Author>? authors, string? feedName)
    {
        if (authors is not { Count: > 0 }) return "";
        if (authors.Count == 1 && authors.First().Name == feedName) return "";
        var parts = new List<string>();
        foreach (var author in authors)
        {
            var email = author.EmailAddress;
            if (email?.Contains("noreply@", StringComparison.Ordinal) == true || email?.Contains("no-reply@", StringComparison.Ordinal) == true) email = null;
            if (email?.Contains(' ') == true) parts.Add(Html(email));
            else if (author.Name is { } name && author.Url is { } url) parts.Add("<a href=\"" + Html(url) + "\">" + Html(name) + "</a>");
            else if (author.Name is { } named && email is not null) parts.Add(Html(named) + " &lt;" + Html(email) + "&gt;");
            else if (author.Name is { } nameOnly) parts.Add(Html(nameOnly));
            else if (email is not null) parts.Add("&lt;" + Html(email) + "&gt;");
            else if (author.Url is { } link) parts.Add("<a href=\"" + Html(link) + "\">" + Html(link) + "</a>");
        }
        return string.Join(", ", parts);
    }

    private static void AddDateSubstitutions(Dictionary<string, string> substitutions, DateTime date)
    {
        var local = date.ToLocalTime();
        var culture = CultureInfo.CurrentCulture;
        var datePattern = Regex.Replace(culture.DateTimeFormat.LongDatePattern, @"dddd\s*[,\u060C]?\s*", "", RegexOptions.CultureInvariant).Trim();
        var longDate = local.ToString(datePattern, culture);
        var mediumDate = local.ToString(datePattern.Replace("MMMM", "MMM"), culture);
        var shortTime = local.ToString("t", culture);
        var mediumTime = local.ToString("T", culture);
        substitutions["datetime_long"] = Html(longDate + " " + mediumTime);
        substitutions["datetime_medium"] = Html(mediumDate + " " + shortTime);
        substitutions["datetime_short"] = Html(local.ToString("g", culture));
        substitutions["date_long"] = Html(longDate);
        substitutions["date_medium"] = Html(mediumDate);
        substitutions["date_short"] = Html(local.ToString("d", culture));
        substitutions["time_long"] = Html(local.ToString("T", culture) + " " + local.ToString("zzz", culture));
        substitutions["time_medium"] = Html(mediumTime);
        substitutions["time_short"] = Html(shortTime);
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? "");
}
