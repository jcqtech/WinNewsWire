using System.Net;
using System.Text;
using System.Text.Json;
using WinNewsWire.NewsBlur;
using Xunit;

namespace WinNewsWire.Account.Tests.NewsBlur;

public sealed class NewsBlurSyncTests : IDisposable
{
    private readonly TestAccountManager _manager = new();
    private const string Structure = """
        {"feeds":{
          "1":{"id":1,"feed_title":"One","feed_address":"https://example.com/one"},
          "2":{"id":2,"feed_title":"Two","feed_address":"https://example.com/two"}},
         "flat_folders":{" ":[1],"Tech":[1,2],"Empty":[]}}
        """;

    [Fact]
    public async Task RefreshMirrorsFoldersAndDownloadsMissingUnreadAndStarredBodies()
    {
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/reader/feeds" => Json(Structure),
            "/reader/unread_story_hashes" => Json("""{"unread_feed_story_hashes":{"1":[["1:unread",1700000000]]}}"""),
            "/reader/starred_story_hashes" => Json("""{"starred_story_hashes":[["2:starred","1700000000"]]}"""),
            "/reader/river_stories" => Json(JsonSerializer.Serialize(new NewsBlurStoriesResponse([
                Story(1, "1:unread") with { ImageUrls = ["http://example.test/first.jpg"],
                    SecureImageUrls = new() { ["http://example.test/first.jpg"] = "https://proxy.example.test/full" },
                    SecureImageThumbnails = new() { ["http://example.test/first.jpg"] = "https://proxy.example.test/thumb" } },
                Story(2, "2:starred")]))),
            _ => throw new InvalidOperationException("Unexpected NewsBlur endpoint."),
        }));
        var account = _manager.CreateAccount(accountDelegate: new NewsBlurAccountDelegate(http: http));
        var stale = account.AddFolder("Stale");
        var first = account.AddFeed("https://example.com/one", "Old name", stale);
        first.ExternalID = "1";
        first.EditedName = "Custom";
        account.AddFeed("https://example.com/removed", folder: stale).ExternalID = "9";
        await account.RefreshAllAsync();
        Assert.DoesNotContain(stale, account.Folders);
        Assert.Equal(2, account.FlattenedFeeds().Count());
        Assert.Same(first, Assert.Single(account.TopLevelFeeds));
        Assert.Contains(first, account.ExistingFolderWithName("Tech")!.Feeds);
        Assert.Equal(2, account.ExistingFolderWithName("Tech")!.Feeds.Count);
        Assert.Empty(account.ExistingFolderWithName("Empty")!.Feeds);
        Assert.Equal("Custom", first.NameForDisplay);
        account.Database.EmptyCaches();
        var articles = await account.Database.FetchArticlesAsync(account.FlattenedFeeds().Select(feed => feed.FeedID));
        Assert.Equal(2, articles.Count);
        Assert.False(articles.Single(article => article.ArticleID == "1:unread").Status.Read);
        Assert.True(articles.Single(article => article.ArticleID == "2:starred").Status.Starred);
        Assert.Equal(first.FeedID, articles.Single(article => article.ArticleID == "1:unread").FeedID);
        Assert.Equal("https://proxy.example.test/full", articles.Single(article => article.ArticleID == "1:unread").RawImageLink);
        Assert.Equal("https://proxy.example.test/thumb", articles.Single(article => article.ArticleID == "1:unread").RawThumbnailLink);
    }

    [Fact]
    public async Task FailedSubscriptionListPreservesLocalState()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        var account = _manager.CreateAccount(accountDelegate: new NewsBlurAccountDelegate(http: http));
        var feed = account.AddFeed("https://example.com/one");
        await Assert.ThrowsAsync<HttpRequestException>(() => account.RefreshAllAsync());
        Assert.Same(feed, Assert.Single(account.TopLevelFeeds));
    }

    [Fact]
    public async Task CompleteEmptyStructureRemovesStaleFeedsAndFolders()
    {
        using var http = new HttpClient(new Handler(request => Json(request.RequestUri!.AbsolutePath switch
        {
            "/reader/feeds" => """{"feeds":{},"flat_folders":{}}""",
            "/reader/unread_story_hashes" => """{"unread_feed_story_hashes":{}}""",
            "/reader/starred_story_hashes" => """{"starred_story_hashes":[]}""",
            _ => throw new InvalidOperationException("Unexpected request."),
        })));
        var account = _manager.CreateAccount(accountDelegate: new NewsBlurAccountDelegate(http: http));
        account.AddFeed("https://example.com/one", folder: account.AddFolder("Stale"));
        await account.RefreshAllAsync();
        Assert.Empty(account.Folders);
        Assert.Empty(account.TopLevelFeeds);
    }

    [Fact]
    public async Task NewlyAddedFeedDownloadsAllInitialPages()
    {
        int pages = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/reader/add_url") return Json("""{"feed":{"id":1,"feed_title":"One","feed_address":"https://example.com/one"}}""");
            if (path == "/reader/feeds") return Json("""{"feeds":{"1":{"id":1,"feed_title":"One","feed_address":"https://example.com/one"}},"flat_folders":{" ":[1]}}""");
            if (path == "/reader/unread_story_hashes") return Json("""{"unread_feed_story_hashes":{}}""");
            if (path == "/reader/starred_story_hashes") return Json("""{"starred_story_hashes":[]}""");
            Assert.Equal("/reader/feed/1", path);
            pages++;
            var page = pages < 3 ? new[] { Story(1, "1:" + pages) } : [];
            return Json(JsonSerializer.Serialize(new { stories = page }));
        }));
        var account = _manager.CreateAccount(accountDelegate: new NewsBlurAccountDelegate(http: http));
        var feed = Assert.IsType<Feed>(await account.CreateFeedAsync("https://example.com/one"));
        await account.RefreshAllAsync();
        Assert.Equal(3, pages);
        var articles = await account.Database.FetchArticlesAsync(feed.FeedID);
        Assert.Equal(2, articles.Count);
        Assert.All(articles, article => Assert.True(article.Status.Read));
    }

    private static NewsBlurStory Story(long feedID, string id)
        => new(id, feedID, id, "https://example.com/" + id, "Author", "<p>Content</p>", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    private static HttpResponseMessage Json(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    public void Dispose() => _manager.Dispose();
}
