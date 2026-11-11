using System.Collections.Concurrent;
using System.Net;
using WinNewsWire.Web;
using Xunit;

namespace Web.Tests;

public sealed class DownloadSession429Tests
{
    [Fact]
    public void ThrottleLookupIgnoresHostCase()
    {
        using var session = new DownloadSession(new Delegate());
        var mixed = new Uri("https://Example.com/feed");
        session.Handle429Response(mixed, new(429, mixed, null, null, null, null, "60"));
        Assert.True(session.RequestShouldBeDroppedDueToActive429(mixed));
        Assert.True(session.RequestShouldBeDroppedDueToActive429(new("https://example.com/other")));
        Assert.False(session.RequestShouldBeDroppedDueToActive429(new("https://example.org/feed")));
    }

    [Fact]
    public void CancellationMatchesExactHostOnly()
    {
        var same = new Uri("https://t.co/a");
        var uppercase = new Uri("https://T.CO/b");
        var containing = new Uri("https://at.com/c");
        var subdomain = new Uri("https://feeds.t.co/d");
        Assert.True(DownloadSession.TasksWithHost("t.co", [same, uppercase, containing, subdomain]).SetEquals([same, uppercase]));
    }

    [Fact]
    public async Task ActiveRequestsForTheThrottledHostAreActuallyCancelled()
    {
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = Guid.NewGuid().ToString("N");
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "t.co")
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/limit"))
                {
                    await otherStarted.Task.WaitAsync(ct);
                    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    response.Headers.RetryAfter = new(TimeSpan.FromMinutes(1));
                    return response;
                }
                otherStarted.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("feed") };
        }));
        var observer = new Delegate();
        using var session = new DownloadSession(observer, http);
        session.Download(new HashSet<Uri>
        {
            new($"https://t.co/{path}/limit"), new($"https://T.CO/{path}/waiting"),
            new($"https://at.com/{path}/feed"), new($"https://feeds.t.co/{path}/feed"),
        });
        await observer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["at.com", "feeds.t.co"], observer.Downloaded.Select(uri => uri.Host).Order());
        Assert.Equal(0, session.Progress.NumberRemaining);
    }

    [Fact]
    public async Task EmptyAndFullyThrottledBatchesStillComplete()
    {
        var emptyObserver = new Delegate();
        using var empty = new DownloadSession(emptyObserver);
        empty.Download(new HashSet<Uri>());
        await emptyObserver.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var throttledObserver = new Delegate();
        using var throttled = new DownloadSession(throttledObserver);
        var uri = new Uri("https://throttled.example/feed");
        throttled.Handle429Response(uri, new(429, uri, null, null, null, null, "60"));
        throttled.Download(new HashSet<Uri> { uri });
        await throttledObserver.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(throttledObserver.Downloaded);
    }

    [Fact]
    public async Task BatchRefreshBypassesOneShotCacheAndAwaitsTheParsingCallback()
    {
        var uri = new Uri($"https://cached.example/{Guid.NewGuid():N}");
        DownloadCache.Shared.Add(uri.AbsoluteUri, new(uri.AbsoluteUri, HttpStatusCode.OK, [1, 2, 3], null, null));
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5, 6]) });
        }));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Delegate { OnDownloaded = async () => { started.SetResult(); await release.Task; } };
        using var session = new DownloadSession(observer, http);
        session.Download(new HashSet<Uri> { uri });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(observer.Completed.Task.IsCompleted);
        release.SetResult();
        await observer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(observer.Downloaded);
        Assert.Equal(1, requests);
        Assert.Equal(new byte[] { 4, 5, 6 }, Assert.Single(observer.Contents));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
    private sealed class Delegate : IDownloadSessionDelegate
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentBag<Uri> Downloaded { get; } = [];
        public ConcurrentBag<byte[]> Contents { get; } = [];
        public Func<Task>? OnDownloaded { get; init; }
        public ConditionalGetInfo? ConditionalGetInfoFor(Uri url) => null;
        public async Task DownloadDidCompleteAsync(Uri url, DownloadSessionResponse response, byte[] data, Exception? error)
        {
            if (OnDownloaded is not null) await OnDownloaded();
            Downloaded.Add(url);
            Contents.Add(data);
        }
        public bool ShouldContinueAfterReceivingData(ReadOnlyMemory<byte> dataSoFar, Uri url) => true;
        public Task HttpErrorAsync(int statusCode, Uri url) => Task.CompletedTask;
        public Task DownloadSessionDidCompleteAsync() { Completed.TrySetResult(); return Task.CompletedTask; }
    }
}
