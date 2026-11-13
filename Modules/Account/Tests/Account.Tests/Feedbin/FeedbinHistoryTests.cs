using System.Net;
using System.Text;
using WinNewsWire.Feedbin;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedbin;

public sealed class FeedbinHistoryTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public void InvalidOptionalMediaFieldsDoNotDiscardTheWholeFeedbinEntry()
    {
        var entry = System.Text.Json.JsonSerializer.Deserialize<FeedbinEntry>("""
            {"id":1,"feed_id":1,"images":{"original_url":false,"size_1":false},
             "enclosure":{"enclosure_url":"https://example.test/episode","enclosure_type":false,
                          "enclosure_length":{},"itunes_duration":[],"itunes_image":"https://example.test/artwork.jpg"}}
            """)!;
        Assert.Equal("https://example.test/artwork.jpg", entry.ImageUrl);
        Assert.Null(entry.ThumbnailUrl);
        var attachment = entry.Enclosure!.ParsedAttachment!;
        Assert.Equal("https://example.test/episode", attachment.Url);
        Assert.Null(attachment.MimeType);
        Assert.Null(attachment.SizeInBytes);
        Assert.Null(attachment.DurationInSeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtendedMediaSurvivesRefreshAndColdDatabaseReads(bool hasOriginalImage)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/subscriptions.json"))
                return Json("""[{"id":100,"feed_id":1,"title":"Feed","feed_url":"https://example.test/rss"}]""");
            if (path.EndsWith("/entries.json"))
            {
                Assert.Contains("mode=extended", request.RequestUri!.Query);
                return Json($$$"""
                    [{"id":1,"feed_id":1,"title":"Episode",
                      "images":{"original_url":{{{(hasOriginalImage ? "\"https://example.test/full.jpg\"" : "null")}}},
                                "size_1":{"cdn_url":"https://cdn.example.test/small.jpg"}},
                      "enclosure":{"enclosure_url":"https://example.test/episode.mp3","enclosure_type":"audio/mpeg",
                                   "enclosure_length":"54947132","itunes_duration":"01:54:09","itunes_image":"https://example.test/artwork.jpg"}}]
                    """);
            }
            return Json("[]");
        }));
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        account.Database.EmptyCaches();
        var article = Assert.Single(await account.Database.FetchArticlesAsync("1"));
        Assert.Equal(hasOriginalImage ? "https://example.test/full.jpg" : "https://example.test/artwork.jpg", article.RawImageLink);
        Assert.Equal("https://cdn.example.test/small.jpg", article.RawThumbnailLink);
        var attachment = Assert.Single(article.Attachments!);
        Assert.Equal("https://example.test/episode.mp3", attachment.URL);
        Assert.Equal("audio/mpeg", attachment.MimeType);
        Assert.Equal(54947132L, attachment.SizeInBytes);
        Assert.Equal(6849L, attachment.DurationInSeconds);
    }

    [Fact]
    public async Task CompletionDateIsNotPublishedUntilTheWholeRefreshCompletes()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/subscriptions.json"))
                return Json("""[{"id":100,"feed_id":1,"title":"Feed","feed_url":"https://example.com/feed"}]""");
            if (path.EndsWith("/entries.json"))
                return Json("""[{"id":1,"feed_id":1,"title":"Entry"}]""");
            return Json("[]");
        }));
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        account.Settings.LastRefreshCompletedDate = DateTime.UnixEpoch;
        EventHandler<NewArticlesEventArgs> rejectCompletion = (_, _) => throw new InvalidOperationException("notification failure");
        account.NewArticlesDownloaded += rejectCompletion;
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.RefreshAllAsync());
        Assert.Equal(DateTime.UnixEpoch, account.Settings.LastRefreshCompletedDate);
        account.NewArticlesDownloaded -= rejectCompletion;
        var start = DateTime.UtcNow;
        await account.RefreshAllAsync();
        Assert.InRange(account.Settings.LastRefreshCompletedDate!.Value, start, DateTime.UtcNow);
    }

    [Fact]
    public async Task RecentEntriesFollowEveryPage()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            var response = Json($$"""[{"id":{{++calls}},"feed_id":1,"title":"Entry"}]""");
            if (calls == 1)
            {
                Assert.Contains("mode=extended", request.RequestUri!.Query);
                Assert.Contains("since=", request.RequestUri.Query);
                response.Headers.Add("Link", "<https://api.feedbin.com/v2/entries.json?page=2>; rel=\"next\"");
            }
            return response;
        }));
        var entries = await new FeedbinAPICaller(http).RetrieveRecentEntriesAsync();
        Assert.Equal([1L, 2L], entries.Select(entry => entry.ArticleID));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailedHistoryPageKeepsItsWatermarkAndBackdateWindowForRetry()
    {
        bool fail = true;
        var windows = new List<string>();
        var serverDate = DateTimeOffset.UtcNow.AddMinutes(-1);
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/subscriptions.json")) return Json("""[{"id":100,"feed_id":1,"title":"Feed","feed_url":"https://example.com/feed"}]""");
            if (path.EndsWith("/unread_entries.json")) return Json("[2]");
            if (path.EndsWith("/starred_entries.json")) return Json("[3]");
            if (!path.EndsWith("/entries.json")) return Json("[]");
            var query = request.RequestUri.Query;
            if (query.Contains("ids=")) return Json("""[{"id":3,"feed_id":1,"title":"Starred"}]""");
            if (query.Contains("page=2"))
                return fail ? new(HttpStatusCode.ServiceUnavailable) : Json("""[{"id":2,"feed_id":1,"title":"Unread"}]""");
            windows.Add(query);
            var response = Json("""[{"id":1,"feed_id":1,"title":"First page"}]""");
            response.Headers.Date = serverDate;
            response.Headers.Add("Link", "<https://api.feedbin.com/v2/entries.json?page=2>; rel=\"next\"");
            return response;
        }));
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var previous = DateTime.UtcNow.AddHours(-1);
        account.Settings.LastArticleFetchStartTime = previous;
        await Assert.ThrowsAsync<HttpRequestException>(() => account.RefreshAllAsync());
        Assert.Equal(previous, account.Settings.LastArticleFetchStartTime);
        Assert.Single(await account.Database.FetchArticlesAsync("1"));
        fail = false;
        await account.RefreshAllAsync();
        Assert.Equal(windows[0], windows[1]);
        Assert.Equal(serverDate.UtcDateTime, account.Settings.LastArticleFetchStartTime);
        var articles = await account.Database.FetchArticlesAsync("1");
        Assert.Equal(3, articles.Count);
        Assert.False(articles.Single(article => article.ArticleID == "2").Status.Read);
        Assert.True(articles.Single(article => article.ArticleID == "3").Status.Starred);
    }

    [Fact]
    public async Task SubscriptionChoicesUseTheSharedFeedRanking()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            Assert.Equal("?mode=extended", request.RequestUri!.Query);
            if (calls == 2)
            {
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("https://example.com/rss", body.RootElement.GetProperty("feed_url").GetString());
            }
            return calls == 1 ? Json("""
                [{"feed_url":"https://example.com/comments","title":"Comments"},
                 {"feed_url":"https://example.com/rss","title":"Main feed"}]
                """, HttpStatusCode.MultipleChoices)
                : Json("""{"id":100,"feed_id":1,"title":"Main feed","feed_url":"https://example.com/rss","json_feed":{"icon":"https://example.com/icon.png","favicon":"https://example.com/favicon.ico"}}""", HttpStatusCode.Created);
        }));
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var feed = Assert.IsType<Feed>(await account.CreateFeedAsync("https://example.com/"));
        Assert.Equal("https://example.com/rss", feed.Url);
        Assert.Equal("https://example.com/icon.png", feed.IconUrl);
        Assert.Equal("https://example.com/favicon.ico", feed.FaviconUrl);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AlreadySubscribedIsReportedInsteadOfReturningNull(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new(status)));
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => account.CreateFeedAsync("https://example.com/feed"));
        Assert.Contains("Already subscribed", error.Message);
        Assert.Empty(account.FlattenedFeeds());
    }

    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : this(request => Task.FromResult(send(request))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
    public void Dispose() => _manager.Dispose();
}
