using WinNewsWire.Account;
using WinNewsWire.AppShared.ArticleRendering;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ArticleRendererTests
{
    [Fact]
    public void DefaultTemplateUsesTheNativeMacroNames()
    {
        var theme = Theme(Resource("template.html"), Resource("core.css") + Resource("stylesheet.css"));
        var rendering = ArticleRenderer.Render(Article(), feed: Feed(), theme: theme, avatarUrl: "https://example.com/icon");
        Assert.DoesNotContain("[[", rendering.Html);
        Assert.DoesNotContain("[[", rendering.Style);
        Assert.Contains("My Feed", rendering.Html);
        Assert.Contains("<p>Body</p>", rendering.Html);
        Assert.Contains("articleBody mediumText", rendering.Html);
        Assert.Contains("https://example.com/icon", rendering.Html);
        Assert.Contains("Segoe UI", rendering.Style);
        Assert.Contains("max-width: 48em;", rendering.Style);
        Assert.Contains("padding-left: 40px;", rendering.Style);
        Assert.Contains("padding-right: 40px;", rendering.Style);
    }

    [Fact]
    public void RenderedTitleRetainsAllowedFormattingAndEscapesLiteralTags()
    {
        var rendering = ArticleRenderer.Render(Article(title: "<b>Bold</b> <script>literal</script>"), theme: Theme("[[title]]"));
        Assert.Equal("<b>Bold</b> &lt;script&gt;literal&lt;/script&gt;", rendering.Html);
    }

    [Fact]
    public void MissingTitleUsesTheTitleDateline()
    {
        var rendering = ArticleRenderer.Render(Article(title: null), theme: Theme("[[dateline_style]]"));
        Assert.Equal("articleDatelineTitle", rendering.Html);
    }

    [Fact]
    public void FeedAuthorsSupplyTheBylineAndAreEscaped()
    {
        var feed = Feed();
        feed.Authors = new HashSet<Author> { Author.Create(null, "Alice & Bob", "https://example.com/author?a=1&b=2", null, null)! };
        var rendering = ArticleRenderer.Render(Article(), feed: feed, theme: Theme("[[byline]]"));
        Assert.Equal("<a href=\"https://example.com/author?a=1&amp;b=2\">Alice &amp; Bob</a>", rendering.Html);
    }

    [Fact]
    public void ARedundantBylineAndNoReplyAddressAreOmitted()
    {
        var feed = Feed();
        feed.Authors = new HashSet<Author> { Author.Create(null, "My Feed", null, null, null)! };
        Assert.Equal("", ArticleRenderer.Render(Article(), feed: feed, theme: Theme("[[byline]]")).Html);
        feed.Authors = new HashSet<Author> { Author.Create(null, "Writer", null, null, "noreply@example.com")! };
        Assert.Equal("Writer", ArticleRenderer.Render(Article(), feed: feed, theme: Theme("[[byline]]")).Html);
    }

    [Fact]
    public void ExternalLinkAppearsOnlyWhenDifferentFromThePreferredLink()
    {
        var theme = Theme("[[external_link_label]]|[[external_link_stripped]]|[[external_link]]");
        var rendering = ArticleRenderer.Render(Article(external: "https://other.example/link"), theme: theme);
        Assert.Equal("Link:|other.example/link|https://other.example/link", rendering.Html);
        Assert.Equal("||", ArticleRenderer.Render(Article(url: null, external: "https://other.example/link"), theme: theme).Html);
    }

    [Fact]
    public void BaseUrlLosesTheFragmentButKeepsItsQuery()
    {
        var rendering = ArticleRenderer.Render(Article(url: "https://example.com/post/?page=2#section", body: "<img src=\"image.png\">"), theme: Theme("[[body]]"));
        Assert.Equal("https://example.com/post/?page=2", rendering.BaseUrl);
        Assert.Contains("src=\"https://example.com/post/image.png\"", rendering.Html);
    }

    [Fact]
    public void FeedUrlIsTheLastBaseUrlFallback()
    {
        var feed = new Feed("account", "feed", "https://example.com/feed");
        Assert.Equal("https://example.com/feed", ArticleRenderer.Render(Article(url: null), feed: feed, theme: Theme()).BaseUrl);
    }

    [Fact]
    public void ExtractedContentUsesTheSameThemeAndTheArticleTitle()
    {
        var extracted = new ExtractedArticle("Different title", "Different author", null, "<body><p>Extracted</p></body>", "https://reader.example/post");
        var rendering = ArticleRenderer.Render(Article(), extracted, theme: Theme("<section>[[title]]:[[body]]</section>", "p { color: red; }"));
        Assert.Equal("<section>Article:<p>Extracted</p></section>", rendering.Html);
        Assert.Equal("p { color: red; }", rendering.Style);
        Assert.Equal("https://reader.example/post", rendering.BaseUrl);
    }

    [Fact]
    public void MacRumorsCleanupAppliesOnlyToFeedContent()
    {
        var feed = new Feed("account", "feed", "https://feeds.macrumors.com/MacRumors-All");
        const string body = "<ul><br/><li>Item</li><br/></ul>";
        Assert.Equal("<ul><li>Item</li></ul>",
            ArticleRenderer.Render(Article(body: body), feed: feed, theme: Theme()).Html);
        var extracted = new ExtractedArticle(null, null, null, body, "https://macrumors.com/post");
        Assert.Equal(body, ArticleRenderer.Render(Article(body: body), extracted, feed: feed, theme: Theme()).Html);
    }

    [Fact]
    public void FreshRssRestoresFootnoteAttributesOnlyForItsFeedContent()
    {
        const string body = "<div data-sanitized-class=\"footnotes\"><li data-sanitized-id=\"fn1\">data-sanitized-class</li></div>";
        const string expected = "<div class=\"footnotes\"><li id=\"fn1\">data-sanitized-class</li></div>";
        Assert.Equal(expected, ArticleRenderer.Render(Article(body: body), theme: Theme(), accountType: AccountType.FreshRSS).Html);
        Assert.Equal(body, ArticleRenderer.Render(Article(body: body), theme: Theme(), accountType: AccountType.Feedbin).Html);
        var extracted = new ExtractedArticle(null, null, null, body, "https://example.com/article");
        Assert.Equal(body, ArticleRenderer.Render(Article(body: body), extracted, theme: Theme(), accountType: AccountType.FreshRSS).Html);
    }

    [Theory]
    [InlineData("Plum")]
    [InlineData("Gray")]
    public void BundledThemeCanBeSelectedRenderedAndRestored(string themeName)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinNewsWire.BundledTheme." + Guid.NewGuid().ToString("N"));
        try
        {
            var defaults = new WinNewsWire.Core.AppDefaults(Path.Combine(root, "defaults.json"));
            using (var manager = new ArticleThemesManager(Path.Combine(root, "Themes"), defaults))
            {
                var theme = Assert.Single(manager.Themes, theme => theme.Name == themeName);
                Assert.True(theme.IsAppTheme);
                manager.SelectByName(theme.Name);
                var rendering = ArticleRenderer.Render(Article(), feed: Feed(), theme: manager.CurrentTheme);
                Assert.DoesNotContain("[[", rendering.Html);
                Assert.Contains("<p>Body</p>", rendering.Html);
                Assert.Null(rendering.ThemeFolder);
            }
            using var reopened = new ArticleThemesManager(Path.Combine(root, "Themes"), defaults);
            Assert.Equal(themeName, reopened.CurrentTheme.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ArticleTextSize.Small, "smallText")]
    [InlineData(ArticleTextSize.Medium, "mediumText")]
    [InlineData(ArticleTextSize.Large, "largeText")]
    [InlineData(ArticleTextSize.XLarge, "xLargeText")]
    [InlineData(ArticleTextSize.XXLarge, "xxLargeText")]
    public void AllFiveTextSizesUseTheNativeClasses(ArticleTextSize size, string expected)
        => Assert.Equal(expected, ArticleRenderer.Render(Article(), size: size, theme: Theme("[[text_size_class]]")).Html);

    [Fact]
    public void LegacyWindowsMacrosDoNotCauseRecursiveBodyExpansion()
    {
        var rendering = ArticleRenderer.Render(Article(body: "<p>[[title]] [[[author]]]</p>"), theme: Theme("[[[title]]]:[[[body]]]"));
        Assert.Equal("Article:<p>[[title]] [[[author]]]</p>", rendering.Html);
    }

    [Fact]
    public void PlainTextContentIsNotInterpretedAsMarkup()
    {
        var rendering = ArticleRenderer.Render(Article(body: null, text: "<script>literal</script>"), theme: Theme("[[body]]"));
        Assert.Equal("<pre>&lt;script&gt;literal&lt;/script&gt;</pre>", rendering.Html);
    }

    [Fact]
    public void AllNativeDateMacrosArePopulated()
    {
        var names = new[] { "datetime_long", "datetime_medium", "datetime_short", "date_long", "date_medium", "date_short", "time_long", "time_medium", "time_short" };
        var rendering = ArticleRenderer.Render(Article(), theme: Theme(string.Join("|", names.Select(name => "[[" + name + "]]"))));
        Assert.DoesNotContain("[[", rendering.Html);
        Assert.All(rendering.Html.Split('|'), value => Assert.NotEmpty(value));
    }

    [Fact]
    public void MessageRenderingsShareTheTheme()
    {
        var theme = Theme("unused", "body { color: red; }");
        Assert.Equal("<h3 class='systemMessage'>Multiple selection</h3>", ArticleRenderer.MultipleSelection(theme).Html);
        Assert.Contains("Loading", ArticleRenderer.Loading(theme).Html);
        Assert.Contains("No selection", ArticleRenderer.NoSelection(theme).Html);
        Assert.Equal("", ArticleRenderer.NoContent(theme).Html);
        Assert.Equal(theme.StylesheetCss, ArticleRenderer.Loading(theme).Style);
    }

    [Fact]
    public void DocumentKeepsStylesOutsideTheBodyAndInstallsSharedPageProcessing()
    {
        var rendering = ArticleRenderer.Render(Article(), theme: Theme("[[body]]", "body { color: red; }"));
        var document = ArticleRenderer.Document(rendering, "https://winnewswire-theme.local/stylesheet.css?v=1&x=2");
        Assert.Contains("href=\"https://winnewswire-theme.local/stylesheet.css?v=1&amp;x=2\"", document);
        Assert.Contains("function processPage()", document);
        Assert.Contains("function postRenderProcessing()", document);
        Assert.Contains("newsfoot-footnote-", document);
        Assert.True(document.IndexOf("<link", StringComparison.Ordinal) < document.IndexOf("<body>", StringComparison.Ordinal));
    }

    private static ArticleTheme Theme(string template = "[[body]]", string style = "")
        => new("(test)", isAppTheme: true) { TemplateHtml = template, StylesheetCss = style };
    private static Feed Feed() => new("account", "feed", "https://example.com/feed") { Name = "My Feed", HomePageUrl = "https://example.com/" };
    private static Article Article(string? title = "Article", string? body = "<p>Body</p>", string? url = "https://example.com/article", string? external = null, string? text = null)
        => new("account", "article", "feed", "unique", title, body, text, null, url, external, null, null,
            DateTime.UnixEpoch, null, null, null, new ArticleStatus("article", false, false, DateTime.UnixEpoch));
    private static string Resource(string name)
    {
        var assembly = typeof(ArticleRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Resources." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
