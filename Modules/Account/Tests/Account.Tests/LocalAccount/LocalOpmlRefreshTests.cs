using System.Net;
using System.Net.Sockets;
using System.Text;
using WinNewsWire.ActivityLog;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using Xunit;

namespace WinNewsWire.Account.Tests.LocalAccount;

public class LocalOpmlRefreshTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public void ContentHashKeepsItsExistingValueWithoutCopyingTheFeedBody()
    {
        var body = new byte[4 * 1024 * 1024];
        Array.Fill(body, (byte)65);
        var prefix = Encoding.UTF8.GetBytes("v4-native-parsers:");
        var expected = "v4-native-parsers:" + Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(prefix.Concat(body).ToArray())).ToLowerInvariant();
        _ = LocalAccountRefresher.ContentHashFor([65]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var actual = LocalAccountRefresher.ContentHashFor(body);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, actual);
        Assert.True(allocated < 64 * 1024, $"Hashing copied the payload: {allocated} bytes were allocated.");
    }

    [Fact]
    public async Task RefreshBoundsParsedBacklogWhileDownloadsRemainConcurrent()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var servers = Enumerable.Range(0, 6).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task blocked = Task.CompletedTask, refresh = Task.CompletedTask;
        try
        {
            var account = _manager.CreateAccount();
            foreach (var server in servers) server.Start();
            var feeds = servers.Select(server =>
                account.AddFeed($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/feed")).ToArray();
            var responses = servers.Select(server => ServeFeedAsync(server, cancellation.Token)).ToArray();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            blocked = account.Database.Queue.RunAsync(async _ =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellation.Token);
            });
            await entered.Task.WaitAsync(cancellation.Token);
            refresh = account.RefreshAllAsync(ct: cancellation.Token);
            await Task.WhenAll(responses).WaitAsync(cancellation.Token);
            while (feeds.Count(feed => feed.LastCheckDate is not null) < 2)
                await Task.Delay(10, cancellation.Token);
            await Task.Delay(150, cancellation.Token);
            Assert.Equal(2, feeds.Count(feed => feed.LastCheckDate is not null));
            Assert.False(refresh.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(blocked, refresh).WaitAsync(cancellation.Token);
            Assert.All(feeds, feed => Assert.Equal(1, feed.UnreadCount));
            Assert.Equal(6, (await account.Database.FetchArticlesAsync(feeds.Select(feed => feed.FeedID))).Count);
            Assert.Empty(ActivityLogger.Shared.RunningActivitiesFor(account.ActivityOwner));
        }
        finally
        {
            release.TrySetResult();
            foreach (var server in servers) server.Stop();
            await Task.WhenAll(blocked, refresh);
        }
    }

    [Fact]
    public async Task UnsupportedFeedResponseDoesNotFailTheOpmlImportOrSkipFinalCounts()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var goodServer = new TcpListener(IPAddress.Loopback, 0);
        var badServer = new TcpListener(IPAddress.Loopback, 0);
        goodServer.Start();
        badServer.Start();
        try
        {
            var goodUrl = $"http://127.0.0.1:{((IPEndPoint)goodServer.LocalEndpoint).Port}/feed";
            var badUrl = $"http://127.0.0.1:{((IPEndPoint)badServer.LocalEndpoint).Port}/not-a-feed";
            var goodResponse = ServeFeedAsync(goodServer, cancellation.Token);
            var badResponse = ServeFeedAsync(badServer, cancellation.Token, "<unrecognized />".PadRight(128));
            var account = _manager.CreateAccount();
            var data = new Parsers.ParserData("subscriptions.opml", Encoding.UTF8.GetBytes(
                $"""<opml version="1.1"><body><outline text="Good" xmlUrl="{goodUrl}"/><outline text="Bad" xmlUrl="{badUrl}"/></body></opml>"""));

            await account.ImportOpmlAsync(data, cancellation.Token);
            await Task.WhenAll(goodResponse, badResponse);

            Assert.Equal(2, account.FlattenedFeeds().Count());
            Assert.Equal(1, account.ExistingFeedWithUrl(goodUrl)!.UnreadCount);
            Assert.Single(await account.Database.FetchArticlesAsync(goodUrl));
            Assert.Empty(await account.Database.FetchArticlesAsync(badUrl));
            Assert.False(account.IsOpmlImportInProgress);
            Assert.Contains(ActivityLogger.Shared.CompletedActivitiesFor(account.ActivityOwner), activity =>
                activity.Kind == ActivityKind.RefreshFeedContent(badUrl) && activity.State == ActivityState.Failed);
            Assert.Empty(ActivityLogger.Shared.RunningActivitiesFor(account.ActivityOwner));
        }
        finally
        {
            goodServer.Stop();
            badServer.Stop();
        }
    }

    [Fact]
    public async Task ImportedUrlUsesTheCanonicalDownloadKeyAndKeepsItsCustomTitle()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var url = $"HTTP://127.0.0.1:{port}/feed";
            var response = ServeFeedAsync(listener, cancellation.Token);
            var account = _manager.CreateAccount();
            account.LoadOpmlItems(OpmlRoundTripTests.Parse($"""<outline title="My Name" xmlUrl="{url}"/>"""));
            await account.RefreshAllAsync(ct: cancellation.Token);
            await response;
            var feed = Assert.Single(account.FlattenedFeeds());
            Assert.Equal("Publisher", feed.Name);
            Assert.Equal("My Name", feed.NameForDisplay);
            Assert.Equal("Article", Assert.Single(await account.Database.FetchArticlesAsync(feed.FeedID)).Title);
            var activities = ActivityLogger.Shared.CompletedActivitiesFor(account.ActivityOwner);
            var download = Assert.Single(activities, activity => activity.Kind == ActivityKind.RefreshFeedContent(url));
            Assert.Equal(ActivityState.Completed, download.State);
            Assert.NotNull(download.StartDate);
            Assert.StartsWith("1 new article, ", download.CompletionMessage);
            Assert.Contains(activities, activity => activity.Kind == ActivityKind.RefreshAll && activity.State == ActivityState.Completed);
            Assert.Empty(ActivityLogger.Shared.PendingActivitiesFor(account.ActivityOwner));
            Assert.Empty(ActivityLogger.Shared.RunningActivitiesFor(account.ActivityOwner));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ParserUpgradeFetchesUnconditionallyAndFailedParsingDoesNotAdvanceValidators()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/upgrade";
            var account = _manager.CreateAccount();
            account.LoadOpmlItems(OpmlRoundTripTests.Parse($"""<outline title="Feed" xmlUrl="{url}"/>"""));
            var feed = Assert.Single(account.FlattenedFeeds());
            feed.ETag = "\"legacy\"";
            feed.LastModified = "Wed, 01 Jan 2020 00:00:00 GMT";
            feed.ContentHash = "legacy-unversioned-hash";
            var first = ServeFeedAsync(listener, cancellation.Token, etag: "\"current\"");
            await account.RefreshAllAsync(ct: cancellation.Token);
            var firstHeaders = await first;
            Assert.DoesNotContain(firstHeaders, line => line.StartsWith("If-None-Match:", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(firstHeaders, line => line.StartsWith("If-Modified-Since:", StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("v4-native-parsers:", feed.ContentHash);
            Assert.Equal("\"current\"", feed.ETag);
            var hash = feed.ContentHash;
            feed.LastCheckDate = DateTime.UtcNow.AddHours(-1);

            var second = ServeFeedAsync(listener, cancellation.Token,
                """{"version":"https://jsonfeed.org/version/1","title":"Invalid","items":[null]}""".PadRight(128), "\"invalid\"");
            await account.RefreshAllAsync(ct: cancellation.Token);
            var secondHeaders = await second;
            Assert.Contains(secondHeaders, line => line.Equals("If-None-Match: \"current\"", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(hash, feed.ContentHash);
            Assert.Equal("\"current\"", feed.ETag);
            Assert.Single(await account.Database.FetchArticlesAsync(feed.FeedID));
            Assert.Contains(ActivityLogger.Shared.CompletedActivitiesFor(account.ActivityOwner), activity =>
                activity.Kind == ActivityKind.RefreshFeedContent(url) && activity.State == ActivityState.Failed);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task CreatingASubscriptionDownloadsAndStoresArticlesBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/new-feed";
            var response = ServeFeedAsync(listener, cancellation.Token, etag: "\"initial\"");
            var account = _manager.CreateAccount();
            var feed = Assert.IsType<Feed>(await account.CreateFeedAsync(url, "Chosen name", ct: cancellation.Token));
            await response;
            Assert.Equal("Publisher", feed.Name);
            Assert.Equal("Chosen name", feed.NameForDisplay);
            Assert.Equal("\"initial\"", feed.ETag);
            Assert.NotNull(feed.LastCheckDate);
            Assert.StartsWith("v4-native-parsers:", feed.ContentHash);
            Assert.Equal("Article", Assert.Single(await account.Database.FetchArticlesAsync(feed.FeedID)).Title);
            await Assert.ThrowsAsync<InvalidOperationException>(() => account.CreateFeedAsync(url, "Do not overwrite", ct: cancellation.Token));
            Assert.Equal("Chosen name", feed.NameForDisplay);
            Assert.Single(account.FlattenedFeeds());
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task FailedInitialParsingDoesNotCreateAnEmptySubscription()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/invalid-feed";
            var response = ServeFeedAsync(listener, cancellation.Token,
                """{"version":"https://jsonfeed.org/version/1","items":[]}""".PadRight(128));
            var account = _manager.CreateAccount();
            await Assert.ThrowsAsync<Parsers.FeedParserException>(() => account.CreateFeedAsync(url, ct: cancellation.Token));
            await response;
            Assert.Empty(account.FlattenedFeeds());
            Assert.Empty(await account.Database.FetchArticlesAsync(url));
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(429)]
    public async Task RepeatedAccountRefreshesKeepTheSessionsErrorAndRateLimitState(int status)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/limited";
            var account = _manager.CreateAccount();
            var feed = account.AddFeed(url);
            var response = ServeFeedAsync(listener, cancellation.Token, status: status,
                extraHeaders: status == 429 ? "Retry-After: 3600\r\n" : "");
            await account.RefreshAllAsync(ct: cancellation.Token);
            await response;
            feed.LastCheckDate = DateTime.UtcNow.AddDays(-3);
            await account.RefreshAllAsync(ct: cancellation.Token);
            Assert.False(listener.Pending());
            Assert.Equal(status, feed.LastResponseCode);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task RepeatedAccountRefreshesKeepTheRedirectCache()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/old";
            var account = _manager.CreateAccount();
            var feed = account.AddFeed(url);
            async Task ServeRedirectAsync()
            {
                await ServeFeedAsync(listener, cancellation.Token, status: 301, extraHeaders: "Location: /new\r\n");
                await ServeFeedAsync(listener, cancellation.Token);
            }
            var first = ServeRedirectAsync();
            await account.RefreshAllAsync(ct: cancellation.Token);
            await first;
            feed.LastCheckDate = DateTime.UtcNow.AddHours(-1);
            var second = ServeFeedAsync(listener, cancellation.Token);
            await account.RefreshAllAsync(ct: cancellation.Token);
            Assert.StartsWith("GET /new ", (await second)[0]);
            Assert.Single(await account.Database.FetchArticlesAsync(feed.FeedID));
        }
        finally { listener.Stop(); }
    }

    internal static async Task<IReadOnlyList<string>> ServeFeedAsync(TcpListener listener, CancellationToken ct, string? content = null,
        string? etag = null, int status = 200, string extraHeaders = "",
        Func<IReadOnlyList<string>, CancellationToken, Task>? beforeResponse = null)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var request = new List<string>();
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } line) request.Add(line);
        if (beforeResponse is not null) await beforeResponse(request, ct);
        var body = Encoding.UTF8.GetBytes(content ?? """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0"><channel><title>Publisher</title><link>https://example.com/</link><description>Feed</description>
            <item><guid>item-1</guid><title>Article</title><link>https://example.com/article</link><description>Body</description></item>
            </channel></rss>
            """);
        var validator = etag is null ? "" : $"ETag: {etag}\r\n";
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: application/rss+xml\r\nCache-Control: no-store\r\n{validator}{extraHeaders}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, ct);
        await stream.WriteAsync(body, ct);
        return request;
    }

    public void Dispose() => _manager.Dispose();
}
