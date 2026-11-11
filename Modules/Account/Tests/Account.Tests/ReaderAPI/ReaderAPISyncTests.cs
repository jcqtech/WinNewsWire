using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WinNewsWire.Articles;
using WinNewsWire.ReaderAPI;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests.ReaderAPI;

public sealed class ReaderAPISyncTests : IDisposable
{
    private const string FeedUrl = "https://example.com/feed";
    private const string FeedID = "feed/42";
    private const string FolderID = "user/-/label/Tech";
    private const string ReadingList = "user/-/state/com.google/reading-list";
    private const string StarredState = "user/-/state/com.google/starred";
    private const string LegacyID = "tag:google.com,2005:reader/item/0000000000000001";
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task RefreshPagesIDsDownloadsBodiesAndPreservesLegacyIDs()
    {
        var serverDate = DateTimeOffset.UtcNow.AddSeconds(-5);
        using var handler = new Handler(request =>
        {
            var response = request.Uri.AbsolutePath.Split('/').Last() switch
            {
                "list" when request.Uri.AbsolutePath.Contains("/tag/") => Json(new ReaderAPITagsResponse([new(FolderID)])),
                "list" => Json(new ReaderAPISubscriptionsResponse([new(FeedID, "News \uFF06 Things", FeedUrl, "https://example.com/", [new(FolderID, "Tech")])])),
                "ids" => IDs(request.Query["s"] == StarredState ? ["1"] : request.Query.ContainsKey("xt") ? ["2"]
                    : request.Query.ContainsKey("c") ? ["2"] : ["1"],
                    request.Query["s"] == ReadingList && !request.Query.ContainsKey("xt") && !request.Query.ContainsKey("c") ? "next" : null),
                "token" => Plain("write-token"),
                "contents" => Json(new ReaderAPIStreamContentsResponse([Entry(1, read: true, starred: true), Entry(2, read: false)], null)),
                _ => throw new InvalidOperationException("Unexpected Reader API endpoint."),
            };
            response.Headers.Date = serverDate;
            return response;
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: Delegate(http));
        var stale = account.AddFolder("Stale"); stale.ExternalID = "user/-/label/Stale";
        var localEmpty = account.AddFolder("Local empty");
        var feed = account.AddFeed(FeedUrl, "Old", stale); feed.ExternalID = FeedID; feed.EditedName = "Custom";
        await account.Database.UpdateArticlesAsync([new(account.AccountID, LegacyID, feed.FeedID, "1", "Old", null, null, null,
            null, null, null, null, null, null, null, null, new ArticleStatus(LegacyID, false, true, DateTime.UtcNow))]);
        await account.RefreshAllAsync();
        Assert.DoesNotContain(stale, account.Folders);
        Assert.Contains(localEmpty, account.Folders);
        var folder = account.Folders.Single(item => item.ExternalID == FolderID);
        Assert.Same(feed, Assert.Single(folder.Feeds));
        Assert.Empty(account.TopLevelFeeds);
        Assert.Equal("News & Things", feed.Name);
        Assert.Equal("Custom", feed.NameForDisplay);
        var articles = await account.Database.FetchArticlesAsync(feed.FeedID);
        Assert.Equal(2, articles.Count);
        Assert.True(articles.Single(article => article.ArticleID == LegacyID).Status.Read);
        Assert.True(articles.Single(article => article.ArticleID == LegacyID).Status.Starred);
        Assert.False(articles.Single(article => article.ArticleID == "2").Status.Read);
        Assert.Equal("Title <2> &", articles.Single(article => article.ArticleID == "2").Title);
        Assert.Equal("Author &", Assert.Single(articles.Single(article => article.ArticleID == "2").Authors!).Name);
        Assert.Equal(serverDate.UtcDateTime, account.Settings.LastArticleFetchStartTime);
        Assert.Equal(4, handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/ids")));
        var bodyRequest = handler.Requests.Single(request => request.Uri.AbsolutePath.EndsWith("/contents"));
        Assert.Contains("i=tag%3Agoogle.com%2C2005%3Areader%2Fitem%2F0000000000000001", bodyRequest.Body);
        Assert.Contains("i=tag%3Agoogle.com%2C2005%3Areader%2Fitem%2F0000000000000002", bodyRequest.Body);
    }

    [Fact]
    public async Task NotModifiedListsPreserveTheirStructure()
    {
        using var handler = new Handler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/list"))
            {
                Assert.Equal("\"version\"", request.IfNoneMatch);
                return new(HttpStatusCode.NotModified);
            }
            return IDs([]);
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: Delegate(http));
        var folder = account.AddFolder("Tech"); folder.ExternalID = FolderID;
        var feed = account.AddFeed(FeedUrl, folder: folder); feed.ExternalID = FeedID;
        account.Settings.SetConditionalGetInfo("tags", new(null, "\"version\""));
        account.Settings.SetConditionalGetInfo("subscriptions", new(null, "\"version\""));
        await account.RefreshAllAsync();
        Assert.Same(folder, Assert.Single(account.Folders));
        Assert.Same(feed, Assert.Single(folder.Feeds));
    }

    [Fact]
    public async Task ExpiredWriteTokenIsRefetchedOnceThenCached()
    {
        int tokens = 0, posts = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/token")) return Plain("token-" + ++tokens);
            posts++;
            return request.Body.Contains("T=token-1") ? new(HttpStatusCode.Forbidden) : Plain("OK");
        }));
        var api = new ReaderAPICaller(ReaderAPIVariant.FreshRSS, "https://reader.example/", http) { AuthToken = "auth" };
        await api.MarkAsReadAsync(["1"]);
        await api.MarkAsReadAsync(["2"]);
        Assert.Equal(2, tokens);
        Assert.Equal(3, posts);
    }

    [Fact]
    public async Task ExpiredAuthenticationLogsInAndRetriesOnlyOnce()
    {
        int logins = 0, lists = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/ClientLogin"))
            {
                logins++;
                return Plain("Auth=fresh\n");
            }
            lists++;
            return request.Authorization == "GoogleLogin auth=old" ? new(HttpStatusCode.Unauthorized)
                : Json(new ReaderAPISubscriptionsResponse([]));
        }));
        var api = new ReaderAPICaller(ReaderAPIVariant.FreshRSS, "https://reader.example/", http)
        {
            AuthToken = "old", Credentials = new(CredentialsType.ReaderBasic, "test-user", "test-password"),
        };
        Assert.Empty((await api.RetrieveSubscriptionsAsync())!.Subscriptions);
        Assert.Equal("fresh", api.AuthToken);
        Assert.Equal(1, logins);
        Assert.Equal(2, lists);
    }

    [Fact]
    public async Task NearQuotaSkipsBothAllIDsAndUnreadDownloads()
    {
        using var handler = new Handler(request =>
        {
            Assert.EndsWith("/list", request.Uri.AbsolutePath);
            var response = request.Uri.AbsolutePath.Contains("/tag/")
                ? Json(new ReaderAPITagsResponse([new(FolderID, "folder")]))
                : Json(new ReaderAPISubscriptionsResponse([new(FeedID, "Feed", FeedUrl, null, [new(FolderID, "Tech")])]));
            response.Headers.Add("X-Reader-Zone1-Usage", "90");
            response.Headers.Add("X-Reader-Zone1-Limit", "100");
            response.Headers.Add("X-Reader-Limits-Reset-After", "86400");
            return response;
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(ReaderAPIVariant.Inoreader, authToken: "auth", http: http));
        var folder = account.AddFolder("Tech"); folder.ExternalID = FolderID;
        var feed = account.AddFeed(FeedUrl, folder: folder); feed.ExternalID = FeedID;
        await account.Database.UpdateArticlesAsync([new(account.AccountID, "1", feed.FeedID, "1", null, null, null, null,
            null, null, null, null, null, null, null, null, new ArticleStatus("1", false, true, DateTime.UtcNow))]);
        await account.RefreshAllAsync();
        var article = Assert.Single(await account.Database.FetchArticlesAsync(feed.FeedID));
        Assert.False(article.Status.Read);
        Assert.True(article.Status.Starred);
        Assert.Null(account.Settings.LastArticleFetchStartTime);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task InoreaderStatusPollingSendsStarsButNeverDownloadsStatusStreams()
    {
        using var handler = new Handler(request =>
        {
            Assert.DoesNotContain("/stream/", request.Uri.AbsolutePath);
            return request.Uri.AbsolutePath.EndsWith("/token") ? Plain("edit-token") : Plain("OK");
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(ReaderAPIVariant.Inoreader, authToken: "auth", http: http));
        Assert.False(await account.SyncArticleStatusAsync());
        Assert.Empty(handler.Requests);
        await account.Database.CreateStatusesIfNeededAsync(["1"]);
        await account.MarkAsync(["1"], ArticleStatus.Key.Starred, true);
        Assert.True(await account.SyncArticleStatusAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, await account.SyncDatabase.SelectPendingCountAsync());
    }

    [Fact]
    public async Task FailedIDPageDoesNotAdvanceTheWatermark()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/list"))
                return request.Uri.AbsolutePath.Contains("/tag/") ? Json(new ReaderAPITagsResponse([])) : Json(new ReaderAPISubscriptionsResponse([]));
            return request.Query.ContainsKey("c") ? new(HttpStatusCode.ServiceUnavailable) : IDs(["1"], "next");
        }));
        var account = _manager.CreateAccount(accountDelegate: Delegate(http));
        var previous = DateTime.UtcNow.AddDays(-1);
        account.Settings.LastArticleFetchStartTime = previous;
        await Assert.ThrowsAsync<HttpRequestException>(() => account.RefreshAllAsync());
        Assert.Equal(previous, account.Settings.LastArticleFetchStartTime);
    }

    [Fact]
    public void FullwidthDecodingLeavesOtherUnicodeAlone()
    {
        Assert.Equal("A & <B> \uFF21", ReaderAPIAccountDelegate.DecodeFullwidth("A \uFF06 \uFF1CB\uFF1E \uFF21"));
    }

    private static ReaderAPIAccountDelegate Delegate(HttpClient http)
        => new(ReaderAPIVariant.FreshRSS, "https://reader.example/", authToken: "auth", http: http);
    private static ReaderAPIEntry Entry(int id, bool read, bool starred = false)
        => new("tag:google.com,2005:reader/item/" + id.ToString("x16", CultureInfo.InvariantCulture),
            $"Title \uFF1C{id}\uFF1E \uFF06", "Author \uFF06", 1700000000, null, new("<p>Content</p>"), null,
            [new("https://example.com/" + id)], new(FeedID, "Feed"),
            new[] { read ? "user/-/state/com.google/read" : null, starred ? StarredState : null }.OfType<string>().ToList());
    private static HttpResponseMessage IDs(IReadOnlyList<string> ids, string? continuation = null)
        => Json(new ReaderAPIReferenceWrapper(ids.Select(id => new ReaderAPIReference(id)).ToList(), continuation));
    private static HttpResponseMessage Json<T>(T value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Plain(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed record Request(Uri Uri, string Body, string? Authorization, string? IfNoneMatch)
    {
        public Dictionary<string, string> Query => Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
    }
    private sealed class Handler(Func<Request, HttpResponseMessage> send) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new Request(request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString(), request.Headers.IfNoneMatch.FirstOrDefault()?.ToString());
            Requests.Add(captured);
            return send(captured);
        }
    }
    public void Dispose() => _manager.Dispose();
}
