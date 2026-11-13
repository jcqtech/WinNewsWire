using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class ArticleThumbnailFinderTests
{
    [Theory]
    [InlineData(ThumbnailMode.Small, "thumbnail.jpg")]
    [InlineData(ThumbnailMode.Large, "image.jpg")]
    public void ModesUseTheirOwnProvidedImagePriority(ThumbnailMode mode, string expected)
    {
        using var database = new ImageMetadataDatabase(":memory:");
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
        using var database = new ImageMetadataDatabase(":memory:");
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
        using var database = new ImageMetadataDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(thumbnail: "https://example.com/icon.png", image: "https://example.com/photo.jpg");
        await database.SaveDownloadedImageAsync(new(article.RawThumbnailLink!, 1024, (48, 48), null));
        Assert.False(database.RecentlyFailed(article.RawThumbnailLink!));
        Assert.Equal(article.RawImageLink, finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public void DecodingFailuresFallThroughWithoutBecomingPersistentOrAffectingOtherFinders()
    {
        using var database = new ImageMetadataDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(thumbnail: "https://example.com/icon.ico", image: "https://example.com/photo.jpg");
        finder.RecordDecodingFailure(article.RawThumbnailLink!);
        Assert.Equal(article.RawImageLink, finder.ThumbnailUrl(article, ThumbnailMode.Small));
        Assert.False(database.RecentlyFailed(article.RawThumbnailLink!));
        Assert.False(database.IsUnsuitableForThumbnail(article.RawThumbnailLink!));
        Assert.Equal(article.RawThumbnailLink, new ArticleThumbnailFinder(database).ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public async Task HtmlDiscoveryPersistsPositiveAndNegativeResultsAndNotifiesOnlyFoundArticles()
    {
        using var database = new ImageMetadataDatabase(":memory:");
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
        Assert.Equal("https://example.com/second-photo.jpg", finder.ThumbnailUrl(photo, ThumbnailMode.Small));

        var reopenedFinder = new ArticleThumbnailFinder(database);
        await reopenedFinder.DiscoverThumbnailUrlsAsync([Article(html: "<img src='/different.jpg'>")]);
        Assert.Equal("https://example.com/second-photo.jpg", reopenedFinder.ThumbnailUrl(photo, ThumbnailMode.Small));
    }

    [Fact]
    public async Task DiscoveryKeepsOnlyThreeOrderedHtmlFallbacks()
    {
        using var database = new ImageMetadataDatabase(":memory:");
        var finder = new ArticleThumbnailFinder(database);
        var article = Article(html: string.Concat(Enumerable.Range(1, 4).Select(index => $"<img src='/photo{index}.jpg'>")));
        await finder.DiscoverThumbnailUrlsAsync([article]);
        var saved = Assert.Single(await database.FetchArticleThumbnailsAsync(["1"]));
        Assert.Equal(3, saved.ThumbnailUrls.Count);
        foreach (var url in saved.ThumbnailUrls)
        {
            Assert.Equal(url, finder.ThumbnailUrl(article, ThumbnailMode.Small));
            await database.RecordFailureAsync(url, 404);
        }
        Assert.Null(finder.ThumbnailUrl(article, ThumbnailMode.Small));
    }

    [Fact]
    public async Task CanceledDiscoveryLeavesArticlesEligibleForRetry()
    {
        using var database = new ImageMetadataDatabase(":memory:");
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
        string? thumbnail = null, IEnumerable<Attachment>? attachments = null)
        => new("account", id, "feed", id, null, html, null, null, "https://example.com/posts/" + id, null,
            null, image, null, null, null, attachments, new ArticleStatus(id, false, false, DateTime.UnixEpoch), thumbnail);
}
