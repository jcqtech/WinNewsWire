using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class ArticleThumbnailFinderTests
{
    [Fact]
    public async Task SavedUrlsLoadBeforeDiscoveryWithoutParsingMissingArticlesOrNotifying()
    {
        using var database = new ImageDatabase(":memory:");
        await database.SaveArticleThumbnailsAsync([
            new("saved", ["https://example.com/saved.jpg"]), new("negative", []),
            new("empty", ["https://example.com/stale.jpg"])
        ]);
        var finder = new ArticleThumbnailFinder(database);
        var saved = Article("saved", html: "<img src='/changed.jpg'>");
        var negative = Article("negative", html: "<img src='/not-parsed.jpg'>");
        var missing = Article("missing", html: "<img src='/not-discovered.jpg'>");
        var empty = Article("empty");
        int notifications = 0;
        finder.ThumbnailUrlsAvailable += (_, _) => notifications++;
        Assert.Null(finder.ThumbnailUrl(saved, ThumbnailMode.Large));
        await finder.LoadSavedThumbnailUrlsAsync([saved, negative, missing, empty]);
        Assert.Equal("https://example.com/saved.jpg", finder.ThumbnailUrl(saved, ThumbnailMode.Large));
        Assert.Null(finder.ThumbnailUrl(negative, ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(missing, ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(empty, ThumbnailMode.Small));
        Assert.Empty(await database.FetchArticleThumbnailsAsync(["missing"]));
        Assert.Equal(0, notifications);
        await finder.DiscoverThumbnailUrlsAsync([saved, negative, missing]);
        Assert.Equal("https://example.com/not-discovered.jpg", finder.ThumbnailUrl(missing, ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(negative, ThumbnailMode.Small));
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task CanceledSavedUrlLoadingDoesNotConsumeLaterPreload()
    {
        using var database = new ImageDatabase(":memory:");
        await database.SaveArticleThumbnailsAsync([new("1", ["https://example.com/saved.jpg"])]);
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(html: "<img src='/changed.jpg'>");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finder.LoadSavedThumbnailUrlsAsync([article], canceled.Token));
        Assert.Null(finder.ThumbnailUrl(article, ThumbnailMode.Small));
        await finder.LoadSavedThumbnailUrlsAsync([article]);
        Assert.Equal("https://example.com/saved.jpg", finder.ThumbnailUrl(article, ThumbnailMode.Small));
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
        await finder.DiscoverThumbnailUrlsAsync([article], baseUrlResolver: item => item.BaseUrl(homePage, feed));
        Assert.Equal(expected, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        var reopened = new ArticleThumbnailFinder(database);
        await reopened.DiscoverThumbnailUrlsAsync([article]);
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
    public async Task FailedProvidedImageStartsHtmlDiscoveryOnlyAfterTheProvidedSourceFails()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        const string provided = "https://example.com/thumbnail.jpg";
        var article = Article(thumbnail: provided, html: "<img src='/photo.jpg' width='640' height='480'>");
        await finder.DiscoverThumbnailUrlsAsync([article]);
        Assert.Equal(provided, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        Assert.Empty(await database.FetchArticleThumbnailsAsync(["1"]));
        await database.RecordFailureAsync(provided, 404);
        finder.ThumbnailUrlDidFail(provided);
        Assert.Null(finder.ThumbnailUrl(article, ThumbnailMode.Small));
        await finder.DiscoverThumbnailUrlsAsync([article]);
        Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public async Task HtmlDiscoveryPersistsPositiveAndNegativeResultsAndNotifiesOnlyFoundArticles()
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
        await finder.DiscoverThumbnailUrlsAsync([photo, noPhoto]);
        Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(photo, ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(noPhoto, ThumbnailMode.Small));
        Assert.Equal(["1"], notified);
        var saved = await database.FetchArticleThumbnailsAsync(["1", "2"]);
        Assert.Equal(2, saved.Count);
        Assert.Empty(Assert.Single(saved, record => record.ArticleId == "2").ThumbnailUrls);
        Assert.Equal(["https://example.com/photo.jpg", "https://example.com/second-photo.jpg"],
            Assert.Single(saved, record => record.ArticleId == "1").ThumbnailUrls);
        await database.RecordFailureAsync("https://example.com/photo.jpg", 404);
        finder.ThumbnailUrlDidFail("https://example.com/photo.jpg");
        Assert.Equal("https://example.com/second-photo.jpg", finder.ThumbnailUrl(photo, ThumbnailMode.Small));

        var reopenedFinder = new ArticleThumbnailFinder(database);
        await reopenedFinder.DiscoverThumbnailUrlsAsync([Article(html: "<img src='/different.jpg'>")]);
        Assert.Equal("https://example.com/second-photo.jpg", reopenedFinder.ThumbnailUrl(photo, ThumbnailMode.Small));
    }

    [Fact]
    public async Task DiscoveryKeepsOnlyThreeOrderedHtmlFallbacks()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(html: string.Concat(Enumerable.Range(1, 4).Select(index => $"<img src='/photo{index}.jpg'>")));
        await finder.DiscoverThumbnailUrlsAsync([article]);
        var saved = Assert.Single(await database.FetchArticleThumbnailsAsync(["1"]));
        Assert.Equal(3, saved.ThumbnailUrls.Count);
        foreach (var url in saved.ThumbnailUrls)
        {
            Assert.Equal(url, finder.ThumbnailUrl(article, ThumbnailMode.Small));
            await database.RecordFailureAsync(url, 404);
            finder.ThumbnailUrlDidFail(url);
        }
        Assert.Null(finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public async Task ArticlesWithoutHtmlDoNotLoadStaleDiscoveryRecordsOrPreventLaterDiscovery()
    {
        using var database = new ImageDatabase(":memory:");
        await database.SaveArticleThumbnailsAsync([new("1", ["https://example.com/previous.jpg"])]);
        var finder = new ArticleThumbnailFinder(database);
        foreach (var html in new string?[] { null, "" })
        {
            var empty = Article(html: html);
            await finder.DiscoverThumbnailUrlsAsync([empty]);
            Assert.Null(finder.ThumbnailUrl(empty, ThumbnailMode.Small));
        }
        var populated = Article(html: "<img src='/photo.jpg'>");
        await finder.DiscoverThumbnailUrlsAsync([populated]);
        Assert.Equal("https://example.com/previous.jpg", finder.ThumbnailUrl(populated, ThumbnailMode.Small));
    }

    [Fact]
    public async Task DiscoveryReloadsPositiveAndNegativeResultsAfterTheCombinedCacheExceedsItsLimit()
    {
        using var database = new ImageDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var articles = Enumerable.Range(0, 50_001).Select(index => Article(index.ToString(),
            html: index == 0 ? "<img src='/original.jpg'>" : "<p>No photo.</p>")).ToArray();
        await finder.DiscoverThumbnailUrlsAsync(articles.Take(20_001));
        await database.SaveArticleThumbnailsAsync([
            new("0", ["https://example.com/reloaded.jpg"]),
            new("1", ["https://example.com/new-photo.jpg"])
        ]);
        Assert.Equal("https://example.com/original.jpg", finder.ThumbnailUrl(articles[0], ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(articles[1], ThumbnailMode.Small));
        await finder.DiscoverThumbnailUrlsAsync(articles.Take(2));
        Assert.Equal("https://example.com/original.jpg", finder.ThumbnailUrl(articles[0], ThumbnailMode.Small));
        Assert.Null(finder.ThumbnailUrl(articles[1], ThumbnailMode.Small));
        await finder.DiscoverThumbnailUrlsAsync(articles.Skip(20_001));
        await finder.DiscoverThumbnailUrlsAsync(articles.Take(2));
        Assert.Equal("https://example.com/reloaded.jpg", finder.ThumbnailUrl(articles[0], ThumbnailMode.Small));
        Assert.Equal("https://example.com/new-photo.jpg", finder.ThumbnailUrl(articles[1], ThumbnailMode.Small));
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
            finder.DiscoverThumbnailUrlsAsync([article], cancellation.Token));
        await finder.DiscoverThumbnailUrlsAsync([article]);
        Assert.Equal("https://example.com/photo.jpg", finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    private static Article Article(string id = "1", string? html = null, string? image = null,
        string? thumbnail = null, IEnumerable<Attachment>? attachments = null,
        string? link = "https://example.com/posts/1", string? external = null)
        => new("account", id, "feed", id, null, html, null, null, link, external,
            null, image, null, null, null, attachments, new ArticleStatus(id, false, false, DateTime.UnixEpoch), thumbnail);
}
