using WinNewsWire.Account;
using WinNewsWire.AppShared.ArticleRendering;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ArticleRendererTests
{
    [Theory]
    [InlineData("[[body]]|[[enclosures]]")]
    [InlineData("[[primary_media]]|[[body]]|[[enclosures]]")]
    public void MatchingBodyAndGeneratedImagesUseTheSharedSourceWithAnEscapedOriginalUrl(string template)
    {
        const string image = "https://example.com/photo.jpg?a=1&b=2";
        var theme = Theme(template);
        foreach (string body in new[] { "<p>Body</p>", """<p><img src="/photo.jpg?a=1&amp;b=2"></p>""" })
        {
            var rendering = ArticleRenderer.Render(Article(body: body, image: image), theme: theme);
            string document = ArticleRenderer.Document(rendering, includeScripts: false);
            string routed = ArticleRenderer.RouteThumbnailImages(document, image, rendering.BaseUrl);
            Assert.Contains("src=\"" + ArticleImageResource.Url(image) + "\" data-nnw-src=\"https://example.com/photo.jpg?a=1&amp;b=2\"", routed);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(routed, "data-nnw-src="));
            Assert.DoesNotContain("data-nnw-src=", document);
        }
    }

    [Fact]
    public void PrimaryVideoConsumesItsPosterButLeavesOtherAttachments()
    {
        var poster = Attachment.Create(null, "https://example.com/poster.jpg", "image/jpeg", null, null, null)!;
        var video = Attachment.Create(null, "https://example.com/video.mp4", "video/mp4", null, null, null)!;
        var audio = Attachment.Create(null, "https://example.com/audio.mp3", "audio/mpeg", null, null, null)!;
        var html = ArticleRenderer.Render(Article(image: poster.URL, attachments: [poster, audio, video]),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.StartsWith("<div class=\"nnw-primary-media\"><div class=\"nnw-enclosure nnw-enclosure-video\">", html);
        Assert.Contains("poster=\"https://example.com/poster.jpg\"", html);
        Assert.DoesNotContain("<img ", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<video "));
        Assert.True(html.IndexOf("<audio ", StringComparison.Ordinal) > html.IndexOf("<p>Body</p>", StringComparison.Ordinal));
    }

    [Fact]
    public void PrimaryAudioIncludesArtworkBeforeBody()
    {
        var audio = Attachment.Create(null, "https://example.com/audio.mp3", "audio/mpeg", null, null, 60)!;
        var html = ArticleRenderer.Render(Article(image: "https://example.com/artwork.jpg", attachments: [audio]),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.StartsWith("<div class=\"nnw-primary-media\"><div class=\"nnw-primary-image\">", html);
        Assert.Contains("src=\"https://example.com/artwork.jpg\"", html);
        Assert.True(html.IndexOf("<audio ", StringComparison.Ordinal) < html.IndexOf("<p>Body</p>", StringComparison.Ordinal));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<audio "));
    }

    [Theory]
    [InlineData("https://www.flickr.com/services/feeds/photos_public.gne", true)]
    [InlineData("https://example.com/feed", false)]
    public void FlickrFeedUsesLargeEnclosureImageInTheBody(string feedUrl, bool enlarged)
    {
        const string small = "https://live.staticflickr.com/65535/55640727267_a94c9ae7e7_m.jpg";
        const string large = "https://live.staticflickr.com/65535/55640727267_a94c9ae7e7_b.jpg";
        var image = Attachment.Create(null, large, "image/jpeg", null, null, null)!;
        var article = Article(body: $"<p><img src=\"{small}\" width=\"180\" height=\"240\" alt=\"Photo\"></p>", attachments: [image]);
        var html = ArticleRenderer.Render(article, feed: new Feed("account", "feed", feedUrl), theme: Theme()).Html;
        if (enlarged)
        {
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<img "));
            Assert.Contains($"<img src=\"{large}\" alt=\"Photo\">", html);
        }
        else Assert.Contains(small, html);
    }

    [Fact]
    public void PrimaryImageUsesAttachmentCaptionAndDimensionsWithoutRepeatingIt()
    {
        var image = Attachment.Create(null, "https://example.com/photo.jpg", "image/jpeg", "Photo & <caption>", null, null, 640, 480)!;
        var html = ArticleRenderer.Render(Article(image: image.URL, attachments: [image]),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.StartsWith("<div class=\"nnw-primary-media\"><div class=\"nnw-primary-image\">", html);
        Assert.Contains("width=\"640\" height=\"480\"", html);
        Assert.Contains("<div class=\"nnw-primary-image-caption\">Photo &amp; &lt;caption&gt;</div>", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<img "));
    }

    [Fact]
    public void PrimaryImageCaptionDoesNotRepeatArticleTitle()
    {
        var image = Attachment.Create(null, "https://example.com/photo.jpg", "image/jpeg", " MACBOOK\u00a0Pro ", null, null)!;
        var html = ArticleRenderer.Render(Article(title: "MacBook Pro", attachments: [image]),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.Contains("nnw-primary-image", html);
        Assert.DoesNotContain("nnw-primary-image-caption", html);
    }

    [Fact]
    public void PrimaryImageCanFallBackToThumbnailButLegacyEnclosuresCannot()
    {
        var article = Article(thumbnail: "https://example.com/thumbnail.jpg");
        var html = ArticleRenderer.Render(article, theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.StartsWith("<div class=\"nnw-primary-media\"><div class=\"nnw-primary-image\">", html);
        Assert.Contains("src=\"https://example.com/thumbnail.jpg\"", html);
        Assert.DoesNotContain("thumbnail.jpg", ArticleRenderer.Render(article, theme: Theme()).Html);
    }

    [Theory]
    [InlineData("/lead.jpg", "https://example.com/lead.jpg")]
    [InlineData("//cdn.example.net/lead.jpg", "https://cdn.example.net/lead.jpg")]
    public void PrimaryImageResolvesRootAndProtocolRelativeUrls(string image, string expected)
    {
        var html = ArticleRenderer.Render(Article(image: image),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.Contains("src=\"" + expected + "\"", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private/photo.jpg")]
    public void PrimaryMediaDoesNotGenerateUnsafeImageUrls(string image)
        => Assert.Equal("|<p>Body</p>|", ArticleRenderer.Render(Article(image: image),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html);

    [Fact]
    public void ShortUntitledPostsPlaceLeadImagesAfterTheirText()
    {
        var html = ArticleRenderer.Render(Article(title: null, image: "https://example.com/photo.jpg"),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.StartsWith("|<p>Body</p>|<div class=\"nnw-enclosures\">", html);
        Assert.Contains("<img src=\"https://example.com/photo.jpg\"", html);
    }

    [Theory]
    [InlineData("audio/mpeg", "audio")]
    [InlineData("video/mp4", "video")]
    public void ShortUntitledPostsKeepPlayersAboveBodyButImagesBelow(string mime, string element)
    {
        var media = Attachment.Create(null, "https://example.com/playable", mime, null, null, null)!;
        var image = Attachment.Create(null, "https://example.com/poster.jpg", "image/jpeg", null, null, null)!;
        var html = ArticleRenderer.Render(Article(title: null, image: image.URL, attachments: [image, media]),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        var sections = html.Split('|');
        Assert.Contains("<" + element + " ", sections[0]);
        Assert.DoesNotContain("<img ", sections[0]);
        if (element == "audio") Assert.Contains("<img ", sections[2]);
        else Assert.DoesNotContain("<img ", html);
    }

    [Theory]
    [InlineData(2047, false)]
    [InlineData(2048, true)]
    public void UntitledPostImagePlacementUsesTheUpstreamLengthBoundary(int length, bool primary)
    {
        var html = ArticleRenderer.Render(Article(title: null, body: new string('x', length), image: "https://example.com/photo.jpg"),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.Equal(primary, html.StartsWith("<div class=\"nnw-primary-media\">", StringComparison.Ordinal));
    }

    [Fact]
    public void ShortPostLengthCountsUnicodeTextElementsRatherThanUtf16Units()
    {
        var body = string.Concat(Enumerable.Repeat("\U0001F305", 1100));
        var html = ArticleRenderer.Render(Article(title: null, body: body, image: "https://example.com/photo.jpg"),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.StartsWith("|", html);
        Assert.Contains("<img ", html);
    }

    [Theory]
    [InlineData("[[primary_media]]|[[body]]|[[enclosures]]")]
    [InlineData("[[body]]")]
    public void ResizedBodyImagesAreNotRepeatedAsMedia(string template)
    {
        var image = Attachment.Create(null, "https://original.example/2026/bird.jpg", "image/jpeg", null, null, null)!;
        var html = ArticleRenderer.Render(Article(image: image.URL, attachments: [image],
            body: "<img src=\"https://cdn.example/2026/bird-1024x683.jpg?width=1024\">"), theme: Theme(template)).Html;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<img "));
        Assert.DoesNotContain("nnw-primary-image", html);
    }

    [Theory]
    [InlineData("https://i.ytimg.com/vi/abcdefghijk/hqdefault.jpg")]
    [InlineData("https://substackcdn.com/image/youtube/w_728,c_limit/abcdefghijk")]
    public void EmbeddedYouTubeVideoDoesNotRepeatItsStillImage(string imageUrl)
    {
        var image = Attachment.Create(null, imageUrl, "image/jpeg", null, null, null)!;
        var html = ArticleRenderer.Render(Article(image: imageUrl, attachments: [image],
            body: "<iframe src=\"https://www.youtube-nocookie.com/embed/abcdefghijk\"></iframe>"),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.Contains("<iframe ", html);
        Assert.DoesNotContain("<img ", html);
    }

    [Theory]
    [InlineData("www.engadget.com", 1)]
    [InlineData("example.com", 2)]
    public void EngadgetLargeImagePrefixIsNormalizedOnlyForThatPublisher(string host, int imageCount)
    {
        var imageUrl = $"https://{host}/img/gallery/story/l-intro-1794240937.jpg";
        var image = Attachment.Create(null, imageUrl, "image/jpeg", null, null, null)!;
        var html = ArticleRenderer.Render(Article(image: imageUrl, attachments: [image],
            body: $"<p><img src=\"https://{host}/img/gallery/story/intro-1794240937.jpg\"></p>"),
            theme: Theme("[[primary_media]]|[[body]]|[[enclosures]]")).Html;
        Assert.Equal(imageCount, System.Text.RegularExpressions.Regex.Matches(html, "<img ").Count);
    }

    [Fact]
    public void EnclosuresPreserveFeedOrderInsteadOfSortingUrls()
    {
        var first = Attachment.Create(null, "https://example.com/z.mp3", "audio/mpeg", null, null, null)!;
        var second = Attachment.Create(null, "https://example.com/a.mp3", "audio/mpeg", null, null, null)!;
        var html = ArticleRenderer.Render(Article(attachments: [first, second]), theme: Theme()).Html;
        Assert.True(html.IndexOf(first.URL!, StringComparison.Ordinal) < html.IndexOf(second.URL!, StringComparison.Ordinal));
    }

    [Fact]
    public void UntitledPodcastAttachmentCaptionUsesFileNameDurationAndSize()
    {
        var audio = Attachment.Create(null, "https://example.com/atp311.mp3", "audio/mpeg", null, 54919661, 6849)!;
        var html = ArticleRenderer.Render(Article(image: "https://example.com/artwork.jpg", attachments: [audio]),
            theme: Theme()).Html;
        Assert.Contains("<audio controls preload=\"none\" src=\"https://example.com/atp311.mp3\"", html);
        Assert.Contains("<div class=\"nnw-enclosure-caption\">atp311.mp3 &#183; 1:54:09 &#183; ", html);
        Assert.Contains("MB", html);
        Assert.Contains("<img src=\"https://example.com/artwork.jpg\"", html);
    }

    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData("video/mp4")]
    [InlineData("image/jpeg")]
    public void LegacyTemplatesRetainLeadImagesAlongsideOtherMedia(string mime)
    {
        var attachment = Attachment.Create(null, "https://example.com/media", mime, null, null, null)!;
        var article = new Article("account", "article", "feed", "unique", "Title", "<p>Body</p>", null, null,
            "https://example.com/article", null, null, "https://example.com/poster.jpg",
            null, null, null, new HashSet<Attachment> { attachment }, new ArticleStatus("article", false, false, DateTime.UnixEpoch));
        var html = ArticleRenderer.Render(article, theme: Theme()).Html;
        Assert.Contains("<img src=\"https://example.com/poster.jpg\"", html);
        if (mime == "video/mp4") Assert.Contains("poster=\"https://example.com/poster.jpg\"", html);
    }

    [Fact]
    public void YouTubeFlashAttachmentRendersAsPrivacyEnhancedIframe()
    {
        var attachment = Attachment.Create(null, "https://www.youtube.com/v/abcdefghijk?version=3",
            "application/x-shockwave-flash", null, null, null, 640, 390)!;
        var html = ArticleRenderer.Render(Article(attachments: new HashSet<Attachment> { attachment }), theme: Theme()).Html;
        Assert.Contains("<iframe src=\"https://www.youtube-nocookie.com/embed/abcdefghijk\" width=\"640\" height=\"390\" allowfullscreen>", html);
        Assert.DoesNotContain("shockwave", html);
    }

    [Theory]
    [InlineData("[[body]]")]
    [InlineData("[[body]]|[[enclosures]]")]
    public void EnclosuresRenderWithEscapedCaptionsAndMetadata(string template)
    {
        var attachments = new HashSet<Attachment>
        {
            Attachment.Create(null, "https://example.com/audio.mp3?a=1&b=2", "audio/mpeg", "<Episode>", 54919661, 6849)!,
            Attachment.Create(null, "https://example.com/video.mp4", null, null, null, null, 640, 360)!,
            Attachment.Create(null, "https://example.com/photo.jpg", "image/jpeg", null, null, null, 800, 600)!,
            Attachment.Create(null, "https://example.com/file.exe", null, "Not playable", null, null)!
        };
        var rendering = ArticleRenderer.Render(Article(attachments: attachments), theme: Theme(template));
        Assert.Contains("<audio controls preload=\"none\" src=\"https://example.com/audio.mp3?a=1&amp;b=2\"", rendering.Html);
        Assert.Contains("&lt;Episode&gt;", rendering.Html);
        Assert.Contains("1:54:09", rendering.Html);
        Assert.Contains("<video controls preload=\"metadata\" playsinline src=\"https://example.com/video.mp4\" width=\"640\" height=\"360\"", rendering.Html);
        Assert.Contains("<img src=\"https://example.com/photo.jpg\" alt=\"\" width=\"800\" height=\"600\"", rendering.Html);
        Assert.DoesNotContain("file.exe", rendering.Html);
        Assert.DoesNotContain("[[", rendering.Html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(rendering.Html, "<audio "));
    }

    [Fact]
    public void EnclosuresSkipEmbeddedMediaAndUnsafeUrls()
    {
        var attachments = new HashSet<Attachment>
        {
            Attachment.Create(null, "https://example.com/photo.jpg?a=1&b=2", "image/jpeg", null, null, null)!,
            Attachment.Create(null, "javascript:alert(1)", "image/jpeg", null, null, null)!,
            Attachment.Create(null, "file:///C:/private/video.mp4", "video/mp4", null, null, null)!
        };
        const string body = "<img src=\"https://example.com/photo.jpg?a=1&amp;b=2\">";
        Assert.Equal(body, ArticleRenderer.Render(Article(body: body, attachments: attachments), theme: Theme()).Html);
    }

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
        Assert.Contains("max-width: calc(var(--line-length) * var(--article-text-size));", rendering.Style);
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
                Assert.Equal(themeName == "Gray", rendering.Style.Contains("#nnwImageIcon { filter: grayscale(1); }", StringComparison.Ordinal));
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
    [InlineData(true)]
    [InlineData(false)]
    public void ThemeLoaderPrefersModernTemplateAndRetainsLegacyFallback(bool includeModern)
    {
        var root = Path.Combine(Path.GetTempPath(), "WinNewsWire.VersionedTheme." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Info.json"), "{}");
            File.WriteAllText(Path.Combine(root, "template.html"), "<legacy>[[body]]</legacy>");
            if (includeModern)
                File.WriteAllText(Path.Combine(root, "template7.3.html"), "<modern>[[primary_media]][[body]][[enclosures]]</modern>");
            var theme = Assert.IsType<ArticleTheme>(ArticleTheme.LoadFromFolder(root, isAppTheme: false));
            var html = ArticleRenderer.Render(Article(image: "https://example.com/photo.jpg"), theme: theme).Html;
            Assert.StartsWith(includeModern ? "<modern><div class=\"nnw-primary-media\">" : "<legacy>", html);
            Assert.Contains("<p>Body</p>", html);
            Assert.Contains("src=\"https://example.com/photo.jpg\"", html);
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
        Assert.Equal("<p>&lt;script&gt;literal&lt;/script&gt;</p>", rendering.Html);
    }

    [Fact]
    public void PlainTextUsesParagraphsLineBreaksAndEscapedWebLinks()
    {
        const string text = "HomeView & <everyone>.\r\n\r\nLearn more: https://example.test/shop?a=1&b=2\r\n"
            + "Listen: https://music.example.test/episode\r\n \t\r\nLast paragraph.";
        var rendering = ArticleRenderer.Render(Article(body: null, text: text), theme: Theme("[[body]]"));
        Assert.Equal("<p>HomeView &amp; &lt;everyone&gt;.</p>\n"
            + "<p>Learn more: <a href=\"https://example.test/shop?a=1&amp;b=2\">https://example.test/shop?a=1&amp;b=2</a><br>\n"
            + "Listen: <a href=\"https://music.example.test/episode\">https://music.example.test/episode</a></p>\n"
            + "<p>Last paragraph.</p>", rendering.Html);
    }

    [Fact]
    public void PlainTextLinksKeepBalancedParenthesesAndDoNotIncludeSentencePunctuation()
    {
        const string text = "(https://example.test/a_(b)). mailto:reader@example.test! javascript:alert(1)";
        var html = ArticleRenderer.Render(Article(body: null, text: text), theme: Theme("[[body]]")).Html;
        Assert.Equal("<p>(<a href=\"https://example.test/a_(b)\">https://example.test/a_(b)</a>). "
            + "<a href=\"mailto:reader@example.test\">mailto:reader@example.test</a>! javascript:alert(1)</p>", html);
    }

    [Theory]
    [InlineData("example.com", "http://example.com/")]
    [InlineData("www.example.com/news?a=1&b=2", "http://www.example.com/news?a=1&b=2")]
    [InlineData("EXAMPLE.COM", "http://example.com/")]
    [InlineData("reader+news@example.com", "mailto:reader+news@example.com")]
    [InlineData("bücher.de", "http://bücher.de/")]
    [InlineData("xn--bcher-kva.de", "http://xn--bcher-kva.de/")]
    public void PlainTextRecognizesBareAddressesWithoutChangingTheirText(string text, string expectedTarget)
    {
        var html = ArticleRenderer.Render(Article(body: null, text: text), theme: Theme("[[body]]")).Html;
        var document = System.Xml.Linq.XDocument.Parse(html);
        var link = Assert.Single(document.Descendants("a"));
        Assert.Equal(expectedTarget, link.Attribute("href")?.Value);
        Assert.Equal(text, link.Value);
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:example.com")]
    [InlineData("reader@@example.com")]
    [InlineData("reader..name@example.com")]
    [InlineData("version 1.2.3")]
    public void BareAddressRecognitionDoesNotLinkUnsupportedSchemesOrPartialAddresses(string text)
    {
        var html = ArticleRenderer.Render(Article(body: null, text: text), theme: Theme("[[body]]")).Html;
        Assert.DoesNotContain("<a ", html);
    }

    [Fact]
    public void BareAddressesPreserveSurroundingMarkupTextAndBalancedPathPunctuation()
    {
        const string text = "Contact <reader@example.com> & (www.example.com/a_(b)).";
        var html = ArticleRenderer.Render(Article(body: null, text: text), theme: Theme("[[body]]")).Html;
        Assert.Equal("<p>Contact &lt;<a href=\"mailto:reader@example.com\">reader@example.com</a>&gt; &amp; "
            + "(<a href=\"http://www.example.com/a_(b)\">www.example.com/a_(b)</a>).</p>", html);
    }

    [Fact]
    public void BareAddressRecognitionRejectsOverlongDomainLabels()
    {
        var text = new string('a', 64) + ".com";
        var html = ArticleRenderer.Render(Article(body: null, text: text), theme: Theme("[[body]]")).Html;
        Assert.DoesNotContain("<a ", html);
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
    private static Article Article(string? title = "Article", string? body = "<p>Body</p>", string? url = "https://example.com/article", string? external = null, string? text = null,
        IEnumerable<Attachment>? attachments = null, string? image = null, string? thumbnail = null)
        => new("account", "article", "feed", "unique", title, body, text, null, url, external, null, image,
            DateTime.UnixEpoch, null, null, attachments, new ArticleStatus("article", false, false, DateTime.UnixEpoch), thumbnail);
    private static string Resource(string name)
    {
        var assembly = typeof(ArticleRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Resources." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
