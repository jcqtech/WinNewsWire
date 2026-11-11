using System.Collections.Concurrent;
using System.Net;
using WinNewsWire.Core;
using WinNewsWire.Web;
using Xunit;

namespace Web.Tests;

public sealed class DownloadSessionLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinNewsWire.WebLifecycle." + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseBodiesStayBoundedThroughCallbacksWhileRequestsRemainConcurrent(bool cancelWaiting)
    {
        int limit = DownloadSession.MaximumBufferedResponses;
        int total = limit * 2, headers = 0, bodies = 0, callbacks = 0;
        var allHeaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Observer
        {
            OnDownloaded = async () =>
            {
                if (Interlocked.Increment(ref callbacks) == limit) admitted.TrySetResult();
                await release.Task;
            }
        };
        using var http = new HttpClient(new Handler((_, _) =>
        {
            if (Interlocked.Increment(ref headers) == total) allHeaders.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ObservedStream(() => Interlocked.Increment(ref bodies)))
            });
        }));
        using var session = new DownloadSession(observer, http);
        session.Download(Enumerable.Range(0, total).Select(index => new Uri($"https://example.com/feed{index}")).ToHashSet());
        try
        {
            await Task.WhenAll(allHeaders.Task, admitted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.Equal(total, headers);
            Assert.Equal(limit, bodies);
            Assert.Equal(limit, callbacks);
            Assert.False(session.Completion.IsCompleted);
            if (cancelWaiting) session.CancelAll();
        }
        finally
        {
            release.TrySetResult();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(cancelWaiting ? limit : total, bodies);
        Assert.Equal(cancelWaiting ? limit : total, callbacks);
        session.Download(new HashSet<Uri> { new("https://example.com/next") });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((cancelWaiting ? limit : total) + 1, callbacks);
    }

    [Fact]
    public async Task CallbackFailuresReleaseEveryResponsePermit()
    {
        int callbacks = 0;
        var observer = new Observer
        {
            OnDownloaded = () =>
            {
                Interlocked.Increment(ref callbacks);
                return Task.FromException(new InvalidOperationException("callback failed"));
            }
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response())));
        using var session = new DownloadSession(observer, http);
        int total = DownloadSession.MaximumBufferedResponses * 2;
        session.Download(Enumerable.Range(0, total).Select(index => new Uri($"https://example.com/feed{index}")).ToHashSet());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(total, callbacks);
        Assert.Equal(0, session.Progress.NumberRemaining);
    }

    [Fact]
    public async Task StalledBodiesTimeOutWithoutStarvingWaitingResponses()
    {
        int headers = 0, callbacks = 0;
        int limit = DownloadSession.MaximumBufferedResponses;
        var observer = new Observer { OnDownloaded = () => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; } };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            Interlocked.Increment(ref headers) <= limit
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) }
                : Response()))) { Timeout = TimeSpan.FromMilliseconds(500) };
        using var session = new DownloadSession(observer, http);
        session.Download(Enumerable.Range(0, limit * 2).Select(index => new Uri($"https://example.com/feed{index}")).ToHashSet());
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(limit * 2, callbacks);
        Assert.Equal(limit, observer.Errors.Count);
        Assert.All(observer.Errors, error => Assert.IsAssignableFrom<OperationCanceledException>(error));
    }

    [Fact]
    public async Task CancellationDrainsCallbacksBeforeStartingTheNextBatch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbacks = 0;
        var observer = new Observer
        {
            OnDownloaded = async () =>
            {
                if (Interlocked.Increment(ref callbacks) == 1)
                {
                    started.SetResult();
                    await release.Task;
                }
            }
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response())));
        using var session = new DownloadSession(observer, http);
        session.Download(new HashSet<Uri> { new("https://example.com/first") });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.CancelAll();
        Assert.False(session.Completion.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => session.Download(new HashSet<Uri>()));
        release.SetResult();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        session.Download(new HashSet<Uri> { new("https://example.com/second") });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, callbacks);
        Assert.Equal(2, observer.Completions);
        Assert.Equal(0, session.Progress.NumberRemaining);
    }

    [Fact]
    public async Task AsyncDisposalWaitsForTheCallbackBeforeReleasingItsOwner()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Observer { OnDownloaded = async () => { started.SetResult(); await release.Task; } };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response())));
        var session = new DownloadSession(observer, http);
        session.Download(new HashSet<Uri> { new("https://example.com/feed") });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = session.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        release.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, observer.Completions);
        Assert.Throws<ObjectDisposedException>(() => session.Download(new HashSet<Uri>()));
    }

    [Fact]
    public async Task CallbackFailureIsReportedOnceAndDoesNotPreventACompletedBatchFromBeingRetried()
    {
        int callbacks = 0;
        var observer = new Observer
        {
            OnDownloaded = () => ++callbacks == 1
                ? Task.FromException(new InvalidOperationException("callback failed")) : Task.CompletedTask
        };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response())));
        using var session = new DownloadSession(observer, http);
        session.Download(new HashSet<Uri> { new("https://example.com/feed") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, callbacks);
        session.Download(new HashSet<Uri> { new("https://example.com/feed") });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, callbacks);
    }

    [Fact]
    public async Task CompletionCallbackFailuresReachTheAwaitingOwner()
    {
        var observer = new Observer { OnCompleted = () => Task.FromException(new IOException("completion failed")) };
        using var session = new DownloadSession(observer);
        session.Download(new HashSet<Uri>());
        await Assert.ThrowsAsync<IOException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RequestTimeoutIsReportedAsFailureRatherThanAnUnexplainedSkippedFeed()
    {
        var observer = new Observer();
        using var http = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("request timed out")));
        using var session = new DownloadSession(observer, http);
        session.Download(new HashSet<Uri> { new("https://example.com/feed") });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<TaskCanceledException>(Assert.Single(observer.Errors));
    }

    [Fact]
    public async Task PreviousClientErrorsRemainCachedAcrossBatches()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        using var session = new DownloadSession(new Observer(), http);
        for (int batch = 0; batch < 2; batch++)
        {
            session.Download(new HashSet<Uri> { new("https://example.com/missing") });
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OpenRssCooldownIsPersistedAndStrictlyLongerThanOneHour()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "defaults.json");
        var defaults = new AppDefaults(path);
        var clock = new Clock();
        var requested = new ConcurrentBag<Uri>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requested.Add(request.RequestUri!);
            return Task.FromResult(Response());
        }));
        using (var session = new DownloadSession(new Observer(), http, defaults, clock))
        {
            session.Download(new HashSet<Uri> { new("https://example.com/feed") });
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(path));
            session.Download(new HashSet<Uri> { new("https://openrss.org/one"), new("https://openrss.org/two") });
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(requested, uri => uri.IsOpenRSSOrgUrl());
        }
        using var reopened = new DownloadSession(new Observer(), http, new AppDefaults(path), clock);
        clock.Now += TimeSpan.FromHours(1);
        reopened.Download(new HashSet<Uri> { new("https://openrss.org/three") });
        await reopened.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(requested, uri => uri.IsOpenRSSOrgUrl());
        clock.Now += TimeSpan.FromTicks(1);
        reopened.Download(new HashSet<Uri> { new("https://openrss.org/three") });
        await reopened.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, requested.Count(uri => uri.IsOpenRSSOrgUrl()));
        Assert.Equal(clock.Now.UtcDateTime, new AppDefaults(path).GetValue<DateTime?>("lastOpenRSSOrgFeedRefresh"));
    }

    [Fact]
    public async Task ConcurrentSessionsShareOneOpenRssAllowance()
    {
        Directory.CreateDirectory(_directory);
        var defaults = new AppDefaults(Path.Combine(_directory, "defaults.json"));
        var clock = new Clock();
        int requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Response());
        }));
        var sessions = Enumerable.Range(0, 8).Select(_ => new DownloadSession(new Observer(), http, defaults, clock)).ToArray();
        try
        {
            await Task.WhenAll(sessions.Select(session => Task.Run(() =>
                session.Download(new HashSet<Uri> { new("https://openrss.org/feed") }))));
            await Task.WhenAll(sessions.Select(session => session.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, requests);
        }
        finally { foreach (var session in sessions) session.Dispose(); }
    }

    [Fact]
    public async Task FailedCooldownPersistenceIsReportedWithoutSendingTheRequest()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "defaults.json");
        var defaults = new AppDefaults(path);
        defaults.Save();
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Response()); }));
        using var session = new DownloadSession(new Observer(), http, defaults, new Clock());
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => session.Download(new HashSet<Uri> { new("https://openrss.org/feed") }));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(0, calls);
        session.Download(new HashSet<Uri> { new("https://openrss.org/feed") });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new StringContent("feed content") };
    private sealed class ObservedStream(Action onRead) : MemoryStream(new byte[256])
    {
        private int _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _read, 1) == 0) onRead();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
    private sealed class Observer : IDownloadSessionDelegate
    {
        public Func<Task>? OnDownloaded { get; init; }
        public Func<Task>? OnCompleted { get; init; }
        public ConcurrentBag<Exception> Errors { get; } = [];
        public int Completions { get; private set; }
        public ConditionalGetInfo? ConditionalGetInfoFor(Uri url) => null;
        public async Task DownloadDidCompleteAsync(Uri url, DownloadSessionResponse response, byte[] data, Exception? error)
        {
            if (error is not null) Errors.Add(error);
            if (OnDownloaded is not null) await OnDownloaded();
        }
        public bool ShouldContinueAfterReceivingData(ReadOnlyMemory<byte> dataSoFar, Uri url) => true;
        public Task HttpErrorAsync(int statusCode, Uri url) => Task.CompletedTask;
        public async Task DownloadSessionDidCompleteAsync()
        {
            Completions++;
            if (OnCompleted is not null) await OnCompleted();
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
