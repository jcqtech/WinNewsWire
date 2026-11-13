using System.Runtime.CompilerServices;
using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class ArticleThumbnailFinderTests
{
    [Fact]
    public async Task DiscoveryReusesPositiveAndNegativeMemoryResultsAndMakesNewUrlsReadyBeforeNotification()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        await finder.FindThumbnailUrlsAsync([Article("saved", html: "<img src='/saved.jpg'>"), Article("negative", html: "<p>No photo</p>")]);
        var saved = Article("saved", html: "<img src='/changed.jpg'>");
        var negative = Article("negative", html: "<img src='/not-parsed.jpg'>");
        var missing = Article("missing", html: "<img src='/new.jpg'>");
        bool ready = false;
        var parsed = new List<string>();
        var notified = new HashSet<string>();
        finder.ThumbnailUrlsAvailable += (_, ids) => notified.UnionWith(ids);
        await finder.FindThumbnailUrlsAsync([saved, negative, missing], baseUrlResolver: article =>
        {
            Assert.False(ready);
            parsed.Add(article.ArticleID);
            return article.BaseUrl();
        }, savedUrlsLoaded: () =>
        {
            Assert.Equal("https://example.com/saved.jpg", finder.ThumbnailUrl(saved, ThumbnailMode.Large));
            Assert.Null(finder.ThumbnailUrl(negative, ThumbnailMode.Small));
            Assert.Equal("https://example.com/new.jpg", finder.ThumbnailUrl(missing, ThumbnailMode.Small));
            Assert.Empty(notified);
            ready = true;
        });
        Assert.True(ready);
        Assert.Equal(["missing"], parsed);
        Assert.True(notified.SetEquals(["missing"]));
        Assert.Equal("https://example.com/new.jpg", finder.ThumbnailUrl(missing, ThumbnailMode.Small));
    }

    [Fact]
    public async Task CanceledFindingDoesNotConsumeLaterHtmlDiscovery()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(html: "<img src='/changed.jpg'>");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finder.FindThumbnailUrlsAsync([article], canceled.Token));
        Assert.Null(finder.ThumbnailUrl(article, ThumbnailMode.Small));
        await finder.FindThumbnailUrlsAsync([article]);
        Assert.Equal("https://example.com/changed.jpg", finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public async Task ChosenUrlsStayCachedUntilFailureAndInvalidateAffectedArticlesInBothModes()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        const string shared = "https://example.com/shared.jpg";
        const string fallback = "https://example.com/fallback.jpg";
        var first = Article(thumbnail: shared, image: fallback);
        var second = Article(id: "2", image: shared, thumbnail: fallback);
        var unrelated = Article(id: "3", image: "https://example.com/unrelated.jpg");
        Assert.Equal(shared, finder.ThumbnailUrl(first, ThumbnailMode.Small));
        Assert.Equal(fallback, finder.ThumbnailUrl(first, ThumbnailMode.Large));
        Assert.Equal(shared, finder.ThumbnailUrl(second, ThumbnailMode.Large));
        Assert.Equal(unrelated.RawImageLink, finder.ThumbnailUrl(unrelated, ThumbnailMode.Small));
        await database.RecordFailureAsync(shared, 404);
        Assert.Equal(shared, finder.ThumbnailUrl(first, ThumbnailMode.Small));
        ThumbnailUrlFailure? failure = null;
        finder.ThumbnailDownloadFailed += (_, value) =>
        {
            failure = value;
            Assert.Equal(fallback, finder.ThumbnailUrl(first, ThumbnailMode.Small));
            Assert.Equal(fallback, finder.ThumbnailUrl(second, ThumbnailMode.Large));
        };
        finder.ThumbnailUrlDidFail(shared);
        Assert.True(failure?.ArticleIds.SetEquals(["1", "2"]));
        Assert.Equal(unrelated.RawImageLink, finder.ThumbnailUrl(unrelated, ThumbnailMode.Small));
        failure = null;
        finder.ThumbnailUrlDidFail("https://example.com/unknown.jpg");
        Assert.Null(failure);
    }

    [Fact]
    public void ReplacingAnImmutableArticleWithTheSameIdRecomputesBothPositiveAndNegativeChoices()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var empty = Article();
        var first = Article(image: "https://example.com/first.jpg");
        var replacement = Article(image: "https://example.com/replacement.jpg");
        Assert.Null(finder.ThumbnailUrl(empty, ThumbnailMode.Small));
        Assert.Equal(first.RawImageLink, finder.ThumbnailUrl(first, ThumbnailMode.Small));
        Assert.Equal(replacement.RawImageLink, finder.ThumbnailUrl(replacement, ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(empty, ThumbnailMode.Small));
    }

    [Fact]
    public void CachedChoicesDoNotRetainArticlesAndTheirHtml()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = CacheArticle(finder);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(article.TryGetTarget(out _));

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference<Article> CacheArticle(ArticleThumbnailFinder finder)
        {
            var article = Article(html: "<p>Body</p>", image: "https://example.com/image.jpg");
            finder.ThumbnailUrl(article, ThumbnailMode.Small);
            finder.ThumbnailUrl(article, ThumbnailMode.Large);
            return new(article);
        }
    }

    [Theory]
    [InlineData(ThumbnailMode.Small, "image")]
    [InlineData(ThumbnailMode.Small, "thumbnail")]
    [InlineData(ThumbnailMode.Small, "attachments")]
    [InlineData(ThumbnailMode.Large, "image")]
    [InlineData(ThumbnailMode.Large, "thumbnail")]
    [InlineData(ThumbnailMode.Large, "attachments")]
    public async Task ReplacedArticlesReuseChoicesOnlyWhenAllImageFieldsHaveEqualValues(ThumbnailMode mode, string field)
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        const string original = "https://example.com/original.jpg";
        const string replacement = "https://example.com/replacement.jpg";
        Assert.Equal(original, finder.ThumbnailUrl(WithImage(original, "<p>Original body</p>"), mode));
        await database.RecordFailureAsync(original, 404);
        Assert.Equal(original, finder.ThumbnailUrl(WithImage(original), mode));
        Assert.Equal(replacement, finder.ThumbnailUrl(WithImage(replacement), mode));

        Article WithImage(string url, string html = "<p>Edited body</p>") => Article(html: html,
            image: field == "image" ? url : null,
            thumbnail: field == "thumbnail" ? url : null,
            attachments: field == "attachments"
                ? [Attachment.Create(null, url, "image/jpeg", null, null, null, 640, 480)!] : null);
    }

    [Fact]
    public async Task ChosenUrlCapacityResetsOnlyTheFullMode()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        const string url = "https://example.com/photo.jpg";
        var article = Article(image: url);
        Assert.Equal(url, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        Assert.Equal(url, finder.ThumbnailUrl(article, ThumbnailMode.Large));
        await database.RecordFailureAsync(url, 404);
        for (int index = 0; index < 49_999; index++)
            Assert.Null(finder.ThumbnailUrl(Article("empty-" + index), ThumbnailMode.Small));
        Assert.Equal(url, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        finder.ThumbnailUrl(Article("overflow"), ThumbnailMode.Small);
        Assert.Null(finder.ThumbnailUrl(article, ThumbnailMode.Small));
        Assert.Equal(url, finder.ThumbnailUrl(article, ThumbnailMode.Large));
    }

    [Theory]
    [InlineData("https://article.test/with spaces/post#section", "https://home.test/", "https://feed.test/rss", "https://article.test/with%20spaces/photo.jpg")]
    [InlineData(null, "https://home.test/posts/#section", "https://feed.test/rss", "https://home.test/posts/photo.jpg")]
    [InlineData(null, null, "https://feed.test/rss/feed.xml#section", "https://feed.test/rss/photo.jpg")]
    [InlineData("magnet:unsupported", "https://home.test/posts/", "https://feed.test/rss", "https://home.test/posts/photo.jpg")]
    [InlineData(null, null, null, null)]
    public async Task DiscoverySharesReaderBaseUrlsAndDoesNotUseUnrelatedExternalLinks(
        string? link, string? homePage, string? feed, string? expected)
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(html: "<img src='photo.jpg'>", link: link, external: "https://external.test/unrelated");
        await finder.FindThumbnailUrlsAsync([article], baseUrlResolver: item => item.BaseUrl(homePage, feed));
        Assert.Equal(expected, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        var reopened = new ArticleThumbnailFinder(database);
        await reopened.FindThumbnailUrlsAsync([article], baseUrlResolver: item => item.BaseUrl(homePage, feed));
        Assert.Equal(expected, reopened.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Theory]
    [InlineData(ThumbnailMode.Small, "thumbnail.jpg")]
    [InlineData(ThumbnailMode.Large, "image.jpg")]
    public void ModesUseTheirOwnProvidedImagePriority(ThumbnailMode mode, string expected)
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var image = Attachment.Create(null, "https://example.com/attachment.jpg", "image/jpeg",
            null, null, null, 1200, 800)!;
        var article = Article(image: "https://example.com/image.jpg",
            thumbnail: "https://example.com/thumbnail.jpg", attachments: [image]);
        Assert.Equal("https://example.com/" + expected, finder.ThumbnailUrl(article, mode));
    }

    [Theory]
    [InlineData(ThumbnailMode.Small)]
    [InlineData(ThumbnailMode.Large)]
    public void UsableEarlierProvidedImagesAvoidAttachmentClassificationAllocations(ThumbnailMode mode)
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        const string image = "https://example.com/photo.jpg";
        var attachment = Attachment.Create(null, "https://example.com/document.bin", null, null, null, null)!;
        var withoutAttachments = Article(image: image, thumbnail: image);
        var withAttachments = Article(image: image, thumbnail: image, attachments: Enumerable.Repeat(attachment, 512));
        long Allocated(Article article)
        {
            finder.EmptyCache();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var actual = finder.ThumbnailUrl(article, mode);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(image, actual);
            return bytes;
        }
        _ = Allocated(withoutAttachments);
        _ = Allocated(withAttachments);
        long baseline = Allocated(withoutAttachments);
        long allocated = Allocated(withAttachments);
        Assert.True(allocated <= baseline + 16 * 1024,
            $"An earlier usable image still classified attachments: {allocated} bytes versus {baseline} without attachments.");
    }

    [Fact]
    public async Task FailedProvidedImagesFallThroughToSignificantAttachments()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var small = Attachment.Create(null, "https://example.com/tiny.jpg", "image/jpeg",
            null, null, null, 20, 20)!;
        var large = Attachment.Create(null, "https://example.com/large.jpg", "image/jpeg",
            null, null, null, 800, 600)!;
        var article = Article(image: "https://example.com/image.jpg", thumbnail: "https://example.com/thumbnail.jpg",
            attachments: [small, large]);
        await database.RecordFailureAsync(article.RawThumbnailLink!, 404);
        await database.RecordFailureAsync(article.RawImageLink!, 404);
        Assert.Equal(large.URL, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        Assert.Equal(large.URL, finder.ThumbnailUrl(article, ThumbnailMode.Large));
    }

    [Fact]
    public async Task InsignificantThumbnailFallsThroughWithoutHidingItsFeedIcon()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(thumbnail: "https://example.com/icon.png", image: "https://example.com/photo.jpg");
        await database.SaveDownloadedImageAsync(new(article.RawThumbnailLink!, 1024, (48, 48), null));
        Assert.False(database.RecentlyFailed(article.RawThumbnailLink!));
        Assert.Equal(article.RawImageLink, finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public void DecodingFailuresFallThroughWithoutBecomingPersistentOrAffectingOtherFinders()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(thumbnail: "https://example.com/icon.ico", image: "https://example.com/photo.jpg");
        finder.RecordDecodingFailure(article.RawThumbnailLink!);
        Assert.Equal(article.RawImageLink, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        Assert.False(database.RecentlyFailed(article.RawThumbnailLink!));
        Assert.False(database.IsUnsuitableForThumbnail(article.RawThumbnailLink!));
        Assert.Equal(article.RawThumbnailLink, new ArticleThumbnailFinder(database).ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public void TransientNonImageResponsesFallThroughUntilNetworkChanges()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        const string provided = "https://example.com/thumbnail.jpg";
        const string fallback = "https://example.com/photo.jpg";
        var article = Article(thumbnail: provided, image: fallback);
        Assert.Equal(provided, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        finder.RecordDecodingFailure(provided);
        Assert.Equal(fallback, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        Assert.False(database.RecentlyFailed(provided));
        Assert.False(database.IsUnsuitableForThumbnail(provided));
        finder.ResetDecodingFailures();
        Assert.Equal(provided, finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public async Task FailedProvidedImageStartsHtmlDiscoveryOnlyAfterTheProvidedSourceFails()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        const string provided = "https://example.com/thumbnail.jpg";
        var article = Article(thumbnail: provided, html: "<img src='/photo.jpg' width='640' height='480'>");
        await finder.FindThumbnailUrlsAsync([article]);
        Assert.Equal(provided, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        await database.RecordFailureAsync(provided, 404);
        finder.ThumbnailUrlDidFail(provided);
        Assert.Null(finder.ThumbnailUrl(article, ThumbnailMode.Small));
        await finder.FindThumbnailUrlsAsync([article]);
        Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public async Task HtmlDiscoveryCachesPositiveAndNegativeResultsOnlyInMemoryAndNotifiesFoundArticles()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var photo = Article(html: """
            <a href="https://twitter.com/intent/tweet"><img src="/share.jpg"></a>
            <img src="/photo.jpg" width="640" height="480">
            <img src="/second-photo.jpg" width="640" height="480">
            """);
        var noPhoto = Article(id: "2", html: "<p>No photo.</p>");
        IReadOnlySet<string>? notified = null;
        finder.ThumbnailUrlsAvailable += (_, ids) => notified = ids;
        Assert.Null(finder.ThumbnailUrl(photo, ThumbnailMode.Small));
        await finder.FindThumbnailUrlsAsync([photo, noPhoto]);
        Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(photo, ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(noPhoto, ThumbnailMode.Small));
        Assert.Equal(["1"], notified);
        await database.RecordFailureAsync("https://example.com/photo.jpg", 404);
        finder.ThumbnailUrlDidFail("https://example.com/photo.jpg");
        Assert.Equal("https://example.com/second-photo.jpg", finder.ThumbnailUrl(photo, ThumbnailMode.Small));

        var reopenedFinder = new ArticleThumbnailFinder(database);
        await reopenedFinder.FindThumbnailUrlsAsync([Article(html: "<img src='/different.jpg'>")]);
        Assert.Equal("https://example.com/different.jpg", reopenedFinder.ThumbnailUrl(photo, ThumbnailMode.Small));
    }

    [Fact]
    public async Task DiscoveryKeepsOnlyThreeOrderedHtmlFallbacks()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(html: string.Concat(Enumerable.Range(1, 4).Select(index => $"<img src='/photo{index}.jpg'>")));
        await finder.FindThumbnailUrlsAsync([article]);
        foreach (var url in Enumerable.Range(1, 3).Select(index => $"https://example.com/photo{index}.jpg"))
        {
            Assert.Equal(url, finder.ThumbnailUrl(article, ThumbnailMode.Small));
            await database.RecordFailureAsync(url, 404);
            finder.ThumbnailUrlDidFail(url);
        }
        Assert.Null(finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public async Task ArticlesWithoutHtmlDoNotPreventLaterDiscovery()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        foreach (var html in new string?[] { null, "" })
        {
            var empty = Article(html: html);
            await finder.FindThumbnailUrlsAsync([empty]);
            Assert.Null(finder.ThumbnailUrl(empty, ThumbnailMode.Small));
        }
        var populated = Article(html: "<img src='/photo.jpg'>");
        await finder.FindThumbnailUrlsAsync([populated]);
        Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(populated, ThumbnailMode.Small));
    }

    [Fact]
    public async Task DiscoveryReparsesPositiveAndNegativeResultsAfterTheCombinedCacheExceedsItsLimit()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var articles = Enumerable.Range(0, 50_001).Select(index => Article(index.ToString(),
            html: index == 0 ? "<img src='/original.jpg'>" : "<p>No photo.</p>")).ToArray();
        await finder.FindThumbnailUrlsAsync(articles.Take(20_001));
        articles[0] = Article("0", html: "<img src='/reloaded.jpg'>");
        articles[1] = Article("1", html: "<img src='/new-photo.jpg'>");
        Assert.Equal("https://example.com/original.jpg", finder.ThumbnailUrl(articles[0], ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(articles[1], ThumbnailMode.Small));
        await finder.FindThumbnailUrlsAsync(articles.Take(2));
        Assert.Equal("https://example.com/original.jpg", finder.ThumbnailUrl(articles[0], ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(articles[1], ThumbnailMode.Small));
        await finder.FindThumbnailUrlsAsync(articles.Skip(20_001));
        await finder.FindThumbnailUrlsAsync(articles.Take(2));
        Assert.Equal("https://example.com/reloaded.jpg", finder.ThumbnailUrl(articles[0], ThumbnailMode.Small));
        Assert.Equal("https://example.com/new-photo.jpg", finder.ThumbnailUrl(articles[1], ThumbnailMode.Small));
    }

    [Fact]
    public async Task LowMemoryInvalidatesProvidedChoicesAndReparsesHtml()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var provided = Article(image: "https://example.com/provided.jpg");
        var html = Article("html", html: "<img src='/original.jpg'>");
        await finder.FindThumbnailUrlsAsync([html]);
        foreach (var mode in new[] { ThumbnailMode.Small, ThumbnailMode.Large })
        {
            Assert.Equal(provided.RawImageLink, finder.ThumbnailUrl(provided, mode));
            Assert.Equal("https://example.com/original.jpg", finder.ThumbnailUrl(html, mode));
        }
        await database.RecordFailureAsync(provided.RawImageLink!, 404);
        html = Article("html", html: "<img src='/reloaded.jpg'>");
        finder.EmptyCache();
        foreach (var mode in new[] { ThumbnailMode.Small, ThumbnailMode.Large })
        {
            Assert.Null(finder.ThumbnailUrl(provided, mode));
            Assert.Null(finder.ThumbnailUrl(html, mode));
        }
        await finder.FindThumbnailUrlsAsync([html]);
        foreach (var mode in new[] { ThumbnailMode.Small, ThumbnailMode.Large })
            Assert.Equal("https://example.com/reloaded.jpg", finder.ThumbnailUrl(html, mode));
    }

    [Fact]
    public async Task ParsedThumbnailUrlsBecomeAvailableInOrderedBatchesAndCanceledRemaindersCanRetry()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var articles = Enumerable.Range(0, 1001).Select(index => Article(index.ToString(),
            html: index == 999 ? "<p>No image</p>" : "<img src='/photo.jpg'>")).ToArray();
        using var cancellation = new CancellationTokenSource();
        var batches = new List<IReadOnlySet<string>>();
        bool ready = false;
        finder.ThumbnailUrlsAvailable += (_, ids) =>
        {
            batches.Add(ids);
            if (batches.Count == 1)
            {
                Assert.True(ready);
                Assert.Null(finder.ThumbnailUrl(articles[1000], ThumbnailMode.Small));
                cancellation.Cancel();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finder.FindThumbnailUrlsAsync(articles, cancellation.Token,
            savedUrlsLoaded: () =>
            {
                Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(articles[998], ThumbnailMode.Small));
                Assert.Null(finder.ThumbnailUrl(articles[999], ThumbnailMode.Small));
                Assert.Null(finder.ThumbnailUrl(articles[1000], ThumbnailMode.Small));
                Assert.Empty(batches);
                ready = true;
            }));
        Assert.True(batches[0].SetEquals(articles.Take(999).Select(article => article.ArticleID)));
        await finder.FindThumbnailUrlsAsync(articles);
        Assert.Equal([999, 1], batches.Select(ids => ids.Count));
        Assert.True(batches[1].SetEquals(["1000"]));
        Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(articles[1000], ThumbnailMode.Small));
    }

    [Fact]
    public async Task CanceledDiscoveryLeavesArticlesEligibleForRetry()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(html: "<img src='/photo.jpg'>");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            finder.FindThumbnailUrlsAsync([article], cancellation.Token));
        await finder.FindThumbnailUrlsAsync([article]);
        Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    private static Article Article(string id = "1", string? html = null, string? image = null,
        string? thumbnail = null, IEnumerable<Attachment>? attachments = null,
        string? link = "https://example.com/posts/1", string? external = null)
        => new("account", id, "feed", id, null, html, null, null, link, external,
            null, image, null, null, null, attachments, new ArticleStatus(id, false, false, DateTime.UnixEpoch), thumbnail);
}
