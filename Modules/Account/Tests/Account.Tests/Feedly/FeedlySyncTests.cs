using System.Net;
using System.Text;
using System.Text.Json;
using WinNewsWire.Articles;
using WinNewsWire.Feedly;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

public sealed class FeedlySyncTests : IDisposable
{
    private const string FeedID = "feed/https://example.com/feed";
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task RefreshPagesHistoryAndRetrievesStarredBodiesAndIndividualFeedBackfill()
    {
        using var handler = new Handler(request =>
        {
            if (request.Uri.AbsolutePath == "/v3/collections")
                return Json(new[] { new FeedlyCollection("collection", "Folder", [new(FeedID, "Feed", null, null)]) });
            if (request.Uri.AbsolutePath == "/v3/streams/ids")
            {
                var query = request.Query;
                if (query["streamId"].EndsWith("global.saved")) return Json(new FeedlyStreamIds(null, ["starred-old"]));
                Assert.True(query.ContainsKey("newerThan"));
                if (query.GetValueOrDefault("unreadOnly") == "true") return Json(new FeedlyStreamIds(null, ["unread"]));
                return Json(query.ContainsKey("continuation")
                    ? new FeedlyStreamIds(null, ["updated"]) : new FeedlyStreamIds("next", ["read", "unread"]));
            }
            if (request.Uri.AbsolutePath == "/v3/entries/.mget")
                return Json(JsonSerializer.Deserialize<string[]>(request.Body)!.Select(id => Entry(id)).ToArray());
            Assert.Equal("/v3/streams/contents", request.Uri.AbsolutePath);
            Assert.Equal(FeedID, request.Query["streamId"]);
            Assert.Equal("100", request.Query["count"]);
            return Json(request.Query.ContainsKey("continuation")
                ? new FeedlyStream(FeedID, null, null, [Entry("backfill-2", unread: true)])
                : new FeedlyStream(FeedID, null, "next-content", [Entry("backfill-1", unread: true)]));
        });
        using var http = new HttpClient(handler);
        var accountDelegate = Delegate(http);
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        var start = DateTime.UtcNow;
        await account.RefreshAllAsync();
        var articles = await account.Database.FetchArticlesAsync(FeedID);
        Assert.Equal(6, articles.Count);
        Assert.True(articles.Single(a => a.ArticleID == "read").Status.Read);
        Assert.False(articles.Single(a => a.ArticleID == "unread").Status.Read);
        Assert.True(articles.Single(a => a.ArticleID == "starred-old").Status.Starred);
        Assert.False(articles.Single(a => a.ArticleID == "backfill-2").Status.Read);
        Assert.InRange(account.Settings.LastArticleFetchStartTime!.Value, start.AddMinutes(-5), DateTime.UtcNow.AddMinutes(-5));
        Assert.NotNull(Assert.Single(account.FlattenedFeeds()).LastCheckDate);
        Assert.Equal(2, handler.Requests.Count(r => r.Uri.AbsolutePath == "/v3/streams/contents"));
    }

    [Fact]
    public async Task IncompleteStatusWalksNeverClearUnreadOrStarredStateBeyondThePageCap()
    {
        int unreadPages = 0, starredPages = 0;
        using var handler = new Handler(request =>
        {
            bool starred = request.Query["streamId"].EndsWith("global.saved");
            int page = starred ? ++starredPages : ++unreadPages;
            return Json(new FeedlyStreamIds("next-" + page, [(starred ? "star-" : "unread-") + page]));
        });
        using var http = new HttpClient(handler);
        var accountDelegate = Delegate(http);
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        account.AddFeed("https://example.com/feed", feedID: FeedID);
        var article = new Article(account.AccountID, "beyond", FeedID, "beyond", "Title", null, null, null,
            null, null, null, null, null, null, null, null, new ArticleStatus("beyond", false, true, DateTime.UtcNow));
        await account.Database.UpdateArticlesAsync([article]);
        await accountDelegate.RefreshArticleStatusAsync(account);
        var persisted = Assert.Single(await account.Database.FetchArticlesAsync(FeedID));
        Assert.False(persisted.Status.Read);
        Assert.True(persisted.Status.Starred);
        Assert.Equal(40, unreadPages);
        Assert.Equal(40, starredPages);
    }

    [Fact]
    public async Task IncompleteArticleWalkDoesNotAdvanceItsWatermark()
    {
        int pages = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Uri.AbsolutePath == "/v3/collections") return Json(Array.Empty<FeedlyCollection>());
            if (request.Query.ContainsKey("unreadOnly") || request.Query["streamId"].EndsWith("global.saved"))
                return Json(new FeedlyStreamIds(null, []));
            return Json(new FeedlyStreamIds("page-" + ++pages, []));
        }));
        var account = _manager.CreateAccount(accountDelegate: Delegate(http));
        var watermark = DateTime.UtcNow.AddDays(-2);
        account.Settings.LastArticleFetchStartTime = watermark;
        await account.RefreshAllAsync();
        Assert.Equal(40, pages);
        Assert.Equal(watermark, account.Settings.LastArticleFetchStartTime);
        Assert.NotNull(account.Settings.LastRefreshCompletedDate);
    }

    [Fact]
    public async Task FailedUnreadPageIsNotTreatedAsAnEmptyUnreadSet()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Query["streamId"].EndsWith("global.saved")) return Json(new FeedlyStreamIds(null, []));
            return request.Query.ContainsKey("continuation") ? new(HttpStatusCode.ServiceUnavailable)
                : Json(new FeedlyStreamIds("next", ["first-page"]));
        }));
        var accountDelegate = Delegate(http);
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        account.AddFeed("https://example.com/feed", feedID: FeedID);
        await account.Database.UpdateArticlesAsync([new(account.AccountID, "keep", FeedID, "keep", null, null, null, null,
            null, null, null, null, null, null, null, null, new ArticleStatus("keep", false, false, DateTime.UtcNow))]);
        await Assert.ThrowsAsync<HttpRequestException>(() => accountDelegate.RefreshArticleStatusAsync(account));
        Assert.False(Assert.Single(await account.Database.FetchArticlesAsync(FeedID)).Status.Read);
    }

    [Fact]
    public async Task UpdatedBodiesBeyondDownloadCapRemainDurable()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/v3/entries/.mget", request.Uri.AbsolutePath);
            Assert.InRange(JsonSerializer.Deserialize<string[]>(request.Body)!.Length, 1, 1000);
            calls++;
            return Json(Array.Empty<FeedlyEntry>());
        }));
        var accountDelegate = Delegate(http);
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        var ids = Enumerable.Range(0, 20_001).Select(id => id.ToString()).ToArray();
        account.Settings.PendingArticleRefreshIDs = ids;
        await accountDelegate.DownloadEntriesAsync(account, ids, CancellationToken.None);
        Assert.Equal(20, calls);
        Assert.Equal("20000", Assert.Single(account.Settings.PendingArticleRefreshIDs));
        var replacement = Delegate(http);
        var reopened = _manager.Reopen(account, replacement);
        await replacement.DownloadEntriesAsync(reopened, reopened.Settings.PendingArticleRefreshIDs, CancellationToken.None);
        Assert.Empty(reopened.Settings.PendingArticleRefreshIDs);
        Assert.Equal(21, calls);
    }

    [Fact]
    public async Task RateLimitStopsFurtherRequestsAndRetainsTheWatermark()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
            return response;
        }));
        var account = _manager.CreateAccount(accountDelegate: Delegate(http));
        await Assert.ThrowsAsync<SyncRateLimitException>(() => account.RefreshAllAsync());
        await Assert.ThrowsAsync<SyncRateLimitException>(() => account.RefreshAllAsync());
        Assert.Equal(1, calls);
        Assert.Null(account.Settings.LastArticleFetchStartTime);
    }

    [Fact]
    public async Task FeedlyImportCapabilityMatchesUpstream()
    {
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate());
        Assert.False(account.SupportsOpmlImport);
        await Assert.ThrowsAsync<NotSupportedException>(() => account.ImportOpmlAsync(new Parsers.ParserData(
            "https://example.com/", Encoding.UTF8.GetBytes("<opml><body/></opml>"))));
    }

    private static FeedlyEntry Entry(string id, bool unread = false) => new(id, id, new("<p>Content</p>", "ltr"), null, "Author",
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, new("Feed", FeedID, null), [new("https://example.com/" + id, "text/html")],
        null, unread, null, null, null, DateTimeOffset.UtcNow.AddDays(id == "starred-old" ? -365 : -1).ToUnixTimeMilliseconds());

    private static FeedlyAccountDelegate Delegate(HttpClient http)
        => new(new Credentials(CredentialsType.OAuthAccessToken, "user", "access"), http: http);
    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed record Request(Uri Uri, string Body)
    {
        public Dictionary<string, string> Query => Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
    }
    private sealed class Handler(Func<Request, HttpResponseMessage> send) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new Request(request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            return send(captured);
        }
    }
    public void Dispose() => _manager.Dispose();
}
