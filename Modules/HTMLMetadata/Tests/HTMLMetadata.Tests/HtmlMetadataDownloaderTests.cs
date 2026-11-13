using System.Net;
using System.Text;
using WinNewsWire.Web;
using Xunit;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.HTMLMetadata.Tests;

public sealed class HtmlMetadataDownloaderTests
{
    private const string Url = "https://original.test/";
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<head><link rel=\"icon\" href=\"icon.png\"></head>");

    [Fact]
    public async Task RepeatedCacheReadsDoNotCreateNotificationLoops()
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        await database.SaveAsync(new(Url));
        var notifications = new QueuedContext();
        var previous = SynchronizationContext.Current;
        HtmlMetadataDownloader downloader;
        try
        {
            SynchronizationContext.SetSynchronizationContext(notifications);
            downloader = new(database, clock: clock, activityLog: new(clock));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await using var owned = downloader;
        int count = 0;
        downloader.MetadataAvailable += (_, _) => count++;
        Assert.NotNull(await downloader.GetMetadataAsync(Url));
        notifications.Drain();
        Assert.Equal(1, count);
        Assert.NotNull(await downloader.GetMetadataAsync(Url));
        notifications.Drain();
        Assert.Equal(1, count);
        downloader.EmptyCache();
        Assert.NotNull(await downloader.GetMetadataAsync(Url));
        notifications.Drain();
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData(Url, "https://original.test/news/")]
    [InlineData(Url, "https://WWW.original.test/news/")]
    [InlineData("https://www.original.test/", "http://original.test/news/")]
    public async Task SameHostRedirectedMetadataIsPersistedUnderTheRequestedUrl(string requested, string redirected)
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        using var handler = new Handler(_ => Task.FromResult(Response(HttpStatusCode.OK, Html, redirected)));
        using var http = new HttpClient(handler);
        var log = new ActivityLogger(clock);
        await using var downloader = new HtmlMetadataDownloader(database, new Downloader(http, clock), clock, log);
        var result = Assert.IsType<HtmlMetadataRecord>(await downloader.GetMetadataAsync(requested));
        Assert.Equal(requested, result.Url);
        Assert.Equal(new Uri(new Uri(redirected), "icon.png").AbsoluteUri, Assert.Single(result.Favicons).UrlString);
        downloader.EmptyCache();
        Assert.NotNull(await downloader.GetMetadataAsync(requested));
        Assert.Equal(1, handler.Calls);
        Assert.NotNull(await database.CachedRecordAsync(requested));
        Assert.Null(await database.CachedRecordAsync(redirected));
        Assert.Single(log.CompletedActivities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossHostRedirectsCannotPoisonSavedHomepageMetadata(bool hasStaleRecord)
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        var savedDate = clock.GetUtcNow();
        const string old = "https://original.test/old.png";
        if (hasStaleRecord) await database.SaveAsync(new(Url, twitterImageUrl: old));
        clock.Advance(TimeSpan.FromDays(7));
        using var handler = new Handler(_ => Task.FromResult(Response(HttpStatusCode.OK, Html, "https://portal.test/login/")));
        using var http = new HttpClient(handler);
        var log = new ActivityLogger(clock);
        await using var downloader = new HtmlMetadataDownloader(database, new Downloader(http, clock), clock, log);
        var result = await downloader.GetMetadataAsync(Url);
        if (hasStaleRecord) Assert.Equal(old, result?.TwitterImageUrl);
        else Assert.Null(result);
        Assert.Equal(hasStaleRecord ? (DateTimeOffset?)savedDate : null, await database.LastDownloadDateAsync(Url));
        Assert.Null(await database.CachedRecordAsync(Url));
        Assert.False(await database.RecentlyFailedAsync(Url));
        Assert.NotNull(Assert.Single(log.CompletedActivities).Error);
    }

    [Fact]
    public async Task HttpFailuresReturnStaleSuccessWithoutAdvancingItsDate()
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        var original = clock.GetUtcNow();
        await database.SaveAsync(new(Url, twitterImageUrl: "https://original.test/old.png"));
        clock.Advance(TimeSpan.FromDays(7));
        using var handler = new Handler(_ => Task.FromResult(Response(HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler);
        var log = new ActivityLogger(clock);
        await using var downloader = new HtmlMetadataDownloader(database, new Downloader(http, clock), clock, log);
        Assert.Equal("https://original.test/old.png", (await downloader.GetMetadataAsync(Url))?.TwitterImageUrl);
        Assert.Equal(original, await database.LastDownloadDateAsync(Url));
        Assert.False(await database.RecentlyFailedAsync(Url));
        Assert.NotNull(Assert.Single(log.CompletedActivities).Error);
    }

    [Theory]
    [InlineData(404, 264)]
    [InlineData(503, 3)]
    public async Task RetryGatesMatchPersistentFailuresOrAttemptWindow(int code, int hours)
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        using var handler = new Handler(_ => Task.FromResult(Response((HttpStatusCode)code)));
        using var http = new HttpClient(handler);
        await using var downloader = new HtmlMetadataDownloader(database, new Downloader(http, clock), clock, new(clock));
        Assert.Null(await downloader.GetMetadataAsync(Url));
        clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(1));
        Assert.Null(await downloader.GetMetadataAsync(Url));
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await downloader.GetMetadataAsync(Url));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task NetworkFailureRetriesAtFiveHours()
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        using var handler = new Handler(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("DNS fixture")));
        using var http = new HttpClient(handler);
        await using var downloader = new HtmlMetadataDownloader(database, new Downloader(http, clock), clock, new(clock));
        Assert.Null(await downloader.GetMetadataAsync(Url));
        clock.Advance(TimeSpan.FromHours(5) - TimeSpan.FromSeconds(1));
        Assert.Null(await downloader.GetMetadataAsync(Url));
        Assert.Equal(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await downloader.GetMetadataAsync(Url));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentWaitersShareOneDownloadAndOneCanCancel()
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response(HttpStatusCode.OK, Html);
        });
        using var http = new HttpClient(handler);
        await using var downloader = new HtmlMetadataDownloader(database, new Downloader(http, clock), clock, new(clock));
        using var cancellation = new CancellationTokenSource();
        var first = downloader.GetMetadataAsync(Url, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = downloader.GetMetadataAsync(Url);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();
        Assert.NotNull(await second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ShutdownCancelsDownloadsWithoutRecordingTransientFailure()
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler);
        var downloader = new HtmlMetadataDownloader(database, new Downloader(http, clock), clock, new(clock));
        var pending = downloader.GetMetadataAsync(Url);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await downloader.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(await database.RecentlyFailedAsync(Url));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => downloader.GetMetadataAsync(Url));
    }

    [Theory]
    [InlineData("https://rachelbythebay.com/")]
    [InlineData("https://openrss.org/example.test")]
    public async Task NativeSpecialCasesDoNotStartDownloads(string url)
    {
        var clock = new HtmlMetadataDatabaseTests.TestClock();
        using var database = new HtmlMetadataDatabase(":memory:", clock);
        using var handler = new Handler(_ => throw new InvalidOperationException("A skipped site must not be requested."));
        using var http = new HttpClient(handler);
        await using var downloader = new HtmlMetadataDownloader(database, new Downloader(http, clock), clock, new(clock));
        Assert.Null(await downloader.GetMetadataAsync(url));
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[]? bytes = null, string? url = null)
        => new(status)
        {
            Content = new ByteArrayContent(bytes ?? []),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, url ?? Url)
        };

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Assert.Contains("Mozilla", request.Headers.UserAgent.ToString());
            return response(cancellationToken);
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void Drain()
        {
            while (_callbacks.TryDequeue(out var item)) item.Callback(item.State);
        }
    }
}
