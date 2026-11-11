using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WinNewsWire.Feedly;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

public sealed class FeedlyFeedCreationTests : IDisposable
{
    private const string DiscoveryHtml = """
        <html><head>
        <link rel="alternate" type="application/json" href="feed.json">
        <link rel="alternate" type="application/rss+xml" href="feed.xml">
        </head><body>Website</body></html>
        """;
    private readonly TestAccountManager _manager = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationSearchesForOneFeedAndDownloadsTheInitialPage(bool discoverLocally)
    {
        await using var site = new Website(DiscoveryHtml);
        var feedUrl = new Uri(new Uri(site.Url), "feed.xml").AbsoluteUri;
        using var fixture = new ApiFixture(feedUrl) { EmptyFirstSearch = discoverLocally };
        using var http = new HttpClient(fixture);
        var account = CreateAccount(http);
        var folder = account.AddFolder("Folder");
        folder.ExternalID = "collection";
        var feed = Assert.IsType<Feed>(await account.CreateFeedAsync(site.Url, "Chosen title", folder, site.Token));
        if (discoverLocally) await site.Served;

        Assert.Equal(discoverLocally ? 1 : 0, site.Requests);
        var searches = fixture.Requests.Where(request => request.Uri.AbsolutePath == "/v3/search/feeds").ToArray();
        Assert.Equal(discoverLocally ? 2 : 1, searches.Length);
        Assert.Equal(site.Url, searches[0].Query["query"]);
        if (discoverLocally) Assert.Equal(feedUrl, searches[1].Query["query"]);
        Assert.All(searches, search =>
        {
            Assert.Equal("1", search.Query["count"]);
            Assert.Equal(CultureInfo.CurrentCulture.Name.Replace('-', '_'), search.Query["locale"]);
        });
        var content = Assert.Single(fixture.Requests, request => request.Uri.AbsolutePath == "/v3/streams/contents");
        Assert.Equal("1000", content.Query["count"]);
        Assert.False(content.Query.ContainsKey("newerThan"));
        Assert.False(content.Query.ContainsKey("continuation"));
        Assert.Equal(feedUrl, feed.Url);
        Assert.Equal("Chosen title", feed.EditedName);
        Assert.Same(feed, Assert.Single(folder.Feeds));
        var article = Assert.Single(await account.Database.FetchArticlesAsync(feed.FeedID));
        Assert.Equal("Initial article", article.Title);
        Assert.False(article.Status.Read);
        Assert.Equal(1, feed.UnreadCount);
        Assert.Equal("initial", article.ArticleID);
    }

    [Theory]
    [InlineData("json-only")]
    [InlineData("same-url")]
    [InlineData("retry-empty")]
    public async Task FailedDiscoveryDoesNotCreateASubscriptionOrLoop(string failure)
    {
        var body = failure switch
        {
            "json-only" => """<html><head><link rel="alternate" type="application/json" href="feed.json"></head><body>Website</body></html>""",
            "same-url" => """<rss version="2.0"><channel><title>Feed</title><description>Feed</description><link>https://example.com/</link></channel></rss>""",
            _ => DiscoveryHtml
        };
        await using var site = new Website(body);
        using var fixture = new ApiFixture(new Uri(new Uri(site.Url), "feed.xml").AbsoluteUri)
        {
            EmptyFirstSearch = true,
            AlwaysEmpty = failure == "retry-empty"
        };
        using var http = new HttpClient(fixture);
        var account = CreateAccount(http);
        var folder = account.AddFolder("Folder");
        folder.ExternalID = "collection";
        await Assert.ThrowsAsync<FeedFinder.FeedNotFoundException>(() => account.CreateFeedAsync(site.Url, folder: folder, ct: site.Token));
        await site.Served;
        Assert.Equal(failure == "retry-empty" ? 2 : 1, fixture.SearchCount);
        Assert.DoesNotContain(fixture.Requests, request => request.Method == HttpMethod.Put);
        Assert.Empty(account.FlattenedFeeds());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"results\":[null]}")]
    [InlineData("{\"results\":[{\"title\":\"Invalid\"}]}")]
    public async Task InvalidSearchResponsesAreErrorsRatherThanEmptySearchResults(string response)
    {
        using var fixture = new ApiFixture("https://example.com/feed") { SearchBody = response };
        using var http = new HttpClient(fixture);
        var account = CreateAccount(http);
        var folder = account.AddFolder("Folder");
        folder.ExternalID = "collection";
        await Assert.ThrowsAsync<InvalidDataException>(() => account.CreateFeedAsync("https://example.invalid/", folder: folder));
        Assert.Single(fixture.Requests);
        Assert.Empty(account.FlattenedFeeds());
    }

    [Fact]
    public async Task SearchRequestFailureDoesNotFallBackToAnUnrelatedDownload()
    {
        using var fixture = new ApiFixture("https://example.com/feed") { SearchStatus = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(fixture);
        var account = CreateAccount(http);
        var folder = account.AddFolder("Folder");
        folder.ExternalID = "collection";
        await Assert.ThrowsAsync<HttpRequestException>(() => account.CreateFeedAsync("https://example.invalid/", folder: folder));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task FailedInitialBodyDownloadReportsFailureAndRetainsTheServerSubscriptionForRetry()
    {
        using var fixture = new ApiFixture("https://example.com/feed") { ContentStatus = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(fixture);
        var account = CreateAccount(http);
        var folder = account.AddFolder("Folder");
        folder.ExternalID = "collection";
        await Assert.ThrowsAsync<HttpRequestException>(() => account.CreateFeedAsync("https://example.invalid/", folder: folder));
        var reopened = _manager.Reopen(account, new FeedlyAccountDelegate(
            new Credentials(CredentialsType.OAuthAccessToken, "user", "test-access"), http: http));
        Assert.Equal("feed/https://example.com/feed", Assert.Single(reopened.FlattenedFeeds()).FeedID);
        Assert.Empty(await reopened.Database.FetchArticlesAsync("feed/https://example.com/feed"));
    }

    [Fact]
    public async Task MissingCredentialsAreReportedBeforeAnyServiceMutation()
    {
        using var fixture = new ApiFixture("https://example.com/feed");
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate(http: http));
        var folder = account.AddFolder("Folder");
        folder.ExternalID = "collection";
        await Assert.ThrowsAsync<CredentialsException>(() => account.CreateFeedAsync("https://example.com/feed", folder: folder));
        Assert.Empty(fixture.Requests);
    }

    private Account CreateAccount(HttpClient http) => _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate(
        new Credentials(CredentialsType.OAuthAccessToken, "user", "test-access"), http: http));

    private sealed record Request(Uri Uri, HttpMethod Method)
    {
        public Dictionary<string, string> Query => Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
    }

    private sealed class ApiFixture(string feedUrl) : HttpMessageHandler
    {
        public bool EmptyFirstSearch { get; init; }
        public bool AlwaysEmpty { get; init; }
        public string? SearchBody { get; init; }
        public HttpStatusCode SearchStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode ContentStatus { get; init; } = HttpStatusCode.OK;
        public int SearchCount { get; private set; }
        public List<Request> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!, request.Method));
            var feedID = "feed/" + feedUrl;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/v3/search/feeds":
                    SearchCount++;
                    if (SearchStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(SearchStatus));
                    if (SearchBody is not null) return Task.FromResult(Raw(SearchBody));
                    return Task.FromResult(Json(new
                    {
                        results = AlwaysEmpty || EmptyFirstSearch && SearchCount == 1
                            ? Array.Empty<FeedlyFeedsSearchResult>() : [new FeedlyFeedsSearchResult("Feed", feedID)]
                    }));
                case "/v3/collections/collection/feeds":
                    Assert.Equal(HttpMethod.Put, request.Method);
                    return Task.FromResult(Json(new[] { new FeedlyFeed(feedID, "Feed", null, null) }));
                case "/v3/streams/ids":
                    return Task.FromResult(Json(new FeedlyStreamIds(null, ["initial"])));
                case "/v3/streams/contents":
                    if (ContentStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(ContentStatus));
                    return Task.FromResult(Json(new
                    {
                        id = feedID,
                        continuation = "do-not-follow-initial-subscription",
                        items = new[]
                        {
                            new
                            {
                                id = "initial", title = "Initial article", unread = true,
                                content = new { content = "<p>Body</p>", direction = "ltr" },
                                origin = new { streamId = feedID, title = "Feed" }
                            }
                        }
                    }));
                default:
                    throw new InvalidOperationException("Unexpected service request: " + request.RequestUri.AbsolutePath);
            }
        }
        private static HttpResponseMessage Json<T>(T value) => Raw(JsonSerializer.Serialize(value));
        private static HttpResponseMessage Raw(string value)
            => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }

    private sealed class Website : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(15));
        public string Url { get; }
        public Task Served { get; }
        public int Requests { get; private set; }
        public CancellationToken Token => _cancellation.Token;

        public Website(string body)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{Guid.NewGuid():N}/index";
            Served = ServeAsync(body);
        }

        private async Task ServeAsync(string body)
        {
            using var client = await _listener.AcceptTcpClientAsync(Token);
            Requests++;
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(Token) is { Length: > 0 }) { }
            var bytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, Token);
            await stream.WriteAsync(bytes, Token);
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try { await Served; }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
            catch (SocketException) when (_cancellation.IsCancellationRequested) { }
            _cancellation.Dispose();
        }
    }

    public void Dispose() => _manager.Dispose();
}
