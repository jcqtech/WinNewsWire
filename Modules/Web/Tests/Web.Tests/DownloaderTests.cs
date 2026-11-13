using System.Net;
using WinNewsWire.Web;
using Xunit;

namespace Web.Tests;

public sealed class DownloaderTests
{
    [Fact]
    public async Task ResponseScratchBuffersAreReusedAcrossDifferentWorkerThreads()
    {
        var payload = new byte[8 * 1024 * 1024];
        Array.Fill(payload, (byte)65);
        Task<(byte[]? Bytes, long Allocated, int Thread)> ReadOnWorker() => Task.Factory.StartNew(() =>
        {
            using var content = new ByteArrayContent(payload);
            long before = GC.GetAllocatedBytesForCurrentThread();
            var pending = HttpContentReader.ReadAsync(content, CancellationToken.None);
            Assert.True(pending.IsCompletedSuccessfully);
            var result = pending.GetAwaiter().GetResult();
            return (result, GC.GetAllocatedBytesForCurrentThread() - before, Environment.CurrentManagedThreadId);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var first = await ReadOnWorker();
        var second = await ReadOnWorker();
        Assert.NotEqual(first.Thread, second.Thread);
        Assert.Equal(payload, first.Bytes);
        Assert.Equal(payload, second.Bytes);
        Assert.NotSame(first.Bytes, second.Bytes);
        Assert.True(second.Allocated < payload.Length + 128 * 1024,
            $"A different worker allocated {second.Allocated} bytes instead of reusing response scratch buffers.");
    }

    [Fact]
    public async Task RepeatedLargeBodiesReuseScratchBuffersAndReturnOwnedBytes()
    {
        var payload = new byte[8 * 1024 * 1024];
        Array.Fill(payload, (byte)65);
        using (var warm = new ByteArrayContent(payload))
            await HttpContentReader.ReadAsync(warm, CancellationToken.None);
        using var content = new ByteArrayContent(payload);
        int previousLength = 0;
        bool validPrefixes = true;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var pending = HttpContentReader.ReadAsync(content, CancellationToken.None, prefix =>
        {
            validPrefixes &= prefix.Length > previousLength && prefix.Span[^1] == 65;
            previousLength = prefix.Length;
            return true;
        });
        Assert.True(pending.IsCompletedSuccessfully);
        var bytes = await pending;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(validPrefixes);
        Assert.Equal(payload.Length, previousLength);
        Assert.Equal(payload, bytes);
        Assert.True(allocated < payload.Length + 128 * 1024, $"The repeated read allocated {allocated} bytes.");
        Array.Fill(payload, (byte)66);
        using var next = new ByteArrayContent(payload);
        Assert.Equal(payload, await HttpContentReader.ReadAsync(next, CancellationToken.None));
        Assert.Equal((byte)65, bytes![^1]);
    }

    [Fact]
    public async Task CachedResponseRetainsContentAndValidators()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var response = Response();
            response.Headers.ETag = new("\"version\"");
            response.Content.Headers.LastModified = DateTimeOffset.UnixEpoch;
            return Task.FromResult(response);
        }));
        var downloader = new Downloader(http, new DownloadCache());
        var first = await downloader.DownloadAsync("https://example.com/feed");
        var second = await downloader.DownloadAsync("https://example.com/feed");
        Assert.Equal(1, requests);
        Assert.False(first.ReturnedFromCache);
        Assert.True(second.ReturnedFromCache);
        Assert.Equal(first.Data, second.Data);
        Assert.Equal(first.ContentType, second.ContentType);
        Assert.Equal(first.ConditionalGet, second.ConditionalGet);
        Assert.Equal("\"version\"", second.ConditionalGet?.ETag);
    }

    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 200)]
    [InlineData(true, 304)]
    [InlineData(false, 304)]
    public async Task ConditionalGetsBypassAndDoNotReplaceAnUnconditionalCachedBody(bool etag, int status)
    {
        int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            bool conditional = request.Headers.Contains("If-None-Match") || request.Headers.Contains("If-Modified-Since");
            return Task.FromResult(new HttpResponseMessage(conditional ? (HttpStatusCode)status : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(conditional ? [2] : [1])
            });
        }));
        var downloader = new Downloader(http, new DownloadCache());
        const string url = "https://example.com/conditional-cache";
        var original = await downloader.DownloadAsync(url);
        var validator = etag ? new ConditionalGetInfo(null, "\"v1\"")
            : new ConditionalGetInfo("Thu, 01 Jan 1970 00:00:00 GMT", null);
        var validation = await downloader.DownloadAsync(url, validator);
        Assert.False(validation.ReturnedFromCache);
        Assert.Equal((HttpStatusCode)status, validation.StatusCode);
        if (status == 304) Assert.Null(validation.Data);
        else Assert.Equal(new byte[] { 2 }, validation.Data);
        var cached = await downloader.DownloadAsync(url);
        Assert.True(cached.ReturnedFromCache);
        Assert.Equal(original.Data, cached.Data);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConditionalGetsNeverCoalesceWithOtherRequestsForTheSameUrl(bool firstConditional, bool secondConditional)
    {
        using var started = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            started.Release();
            await release.Task.WaitAsync(ct);
            return request.Headers.Contains("If-None-Match") ? new HttpResponseMessage(HttpStatusCode.NotModified) : Response();
        }));
        var downloader = new Downloader(http, new DownloadCache());
        var conditional = new ConditionalGetInfo(null, "\"v1\"");
        var first = downloader.DownloadAsync("https://example.com/concurrent-conditional", firstConditional ? conditional : null);
        await started.WaitAsync(TimeSpan.FromSeconds(5));
        var second = downloader.DownloadAsync("https://example.com/concurrent-conditional", secondConditional ? conditional : null);
        try { Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(5)), "The second request was incorrectly coalesced."); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(firstConditional, results[0].NotModified);
        Assert.Equal(secondConditional, results[1].NotModified);
        Assert.All(results, result => Assert.False(result.ReturnedFromCache));
    }

    [Fact]
    public async Task OptingOutOfResponseStorageLeavesTheNextRequestUncached()
    {
        int requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(Response());
        }));
        var downloader = new Downloader(http, new DownloadCache());
        Assert.False((await downloader.DownloadAsync("https://example.com/image", cachesResponse: false)).ReturnedFromCache);
        Assert.False((await downloader.DownloadAsync("https://example.com/image", cachesResponse: false)).ReturnedFromCache);
        Assert.Equal(2, requests);
        Assert.False((await downloader.DownloadAsync("https://example.com/image")).ReturnedFromCache);
        Assert.True((await downloader.DownloadAsync("https://example.com/image")).ReturnedFromCache);
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task OneShotCacheExpiresAfterThreeMinutes()
    {
        var clock = new Clock();
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Response()); }));
        var downloader = new Downloader(http, new DownloadCache(clock));
        await downloader.DownloadAsync("https://example.com/feed");
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.True((await downloader.DownloadAsync("https://example.com/feed")).ReturnedFromCache);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.False((await downloader.DownloadAsync("https://example.com/feed")).ReturnedFromCache);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ConcurrentRequestsAreCoalescedAndLabelled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            Interlocked.Increment(ref requests);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response();
        }));
        var downloader = new Downloader(http, new DownloadCache());
        var first = downloader.DownloadAsync("https://example.com/feed");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = downloader.DownloadAsync("https://example.com/feed");
        release.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, requests);
        Assert.False(results[0].ReturnedFromCache);
        Assert.True(results[1].ReturnedFromCache);
        Assert.Equal(results[0].Data, results[1].Data);
    }

    [Fact]
    public async Task CancellingOneCallerDoesNotCancelAnother()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response();
        }));
        var downloader = new Downloader(http, new DownloadCache());
        using var cancellation = new CancellationTokenSource();
        var first = downloader.DownloadAsync("https://example.com/feed", ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = downloader.DownloadAsync("https://example.com/feed");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    [Fact]
    public async Task CancellingTheLastCallerCancelsTheUnderlyingRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.SetResult(); throw; }
            return Response();
        }));
        var downloader = new Downloader(http, new DownloadCache());
        using var cancellation = new CancellationTokenSource();
        var task = downloader.DownloadAsync("https://example.com/feed", ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NetworkFailuresAreNotCachedAndRetryRemainsPossible()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1) throw new HttpRequestException("offline");
            return Task.FromResult(Response());
        }));
        var downloader = new Downloader(http, new DownloadCache());
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync("https://example.com/feed"));
        Assert.True((await downloader.DownloadAsync("https://example.com/feed")).Success);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task RedirectDestinationIsKeptWhenTheResponseIsCached()
    {
        using var finalRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com/redirected/feed");
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = Response();
            response.RequestMessage = finalRequest;
            return Task.FromResult(response);
        }));
        var downloader = new Downloader(http, new DownloadCache());
        Assert.Equal(finalRequest.RequestUri!.AbsoluteUri, (await downloader.DownloadAsync("https://example.com/start")).Url);
        var cached = await downloader.DownloadAsync("https://example.com/start");
        Assert.True(cached.ReturnedFromCache);
        Assert.Equal(finalRequest.RequestUri.AbsoluteUri, cached.Url);
    }

    [Fact]
    public async Task AnExactlySixtyFourMiBResponseIsAccepted()
    {
        using var body = new GeneratedStream(HttpContentReader.MaximumFeedBytes);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body)
        })));
        var response = await new Downloader(http, new DownloadCache()).DownloadAsync("https://example.com/boundary");
        Assert.Equal(HttpContentReader.MaximumFeedBytes, response.Data!.Length);
        Assert.Equal((byte)65, response.Data[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedResponsesAreRejectedWithoutPoisoningTheCache(bool contentLengthKnown)
    {
        int calls = 0;
        using var body = new GeneratedStream(HttpContentReader.MaximumFeedBytes + 1L);
        using var http = new HttpClient(new Handler((_, _) =>
        {
            if (++calls > 1) return Task.FromResult(Response());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            if (contentLengthKnown) response.Content.Headers.ContentLength = HttpContentReader.MaximumFeedBytes + 1L;
            return Task.FromResult(response);
        }));
        var downloader = new Downloader(http, new DownloadCache());
        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync("https://example.com/oversized"));
        if (contentLengthKnown) Assert.Equal(0, body.BytesRead);
        Assert.True((await downloader.DownloadAsync("https://example.com/oversized")).Success);
        Assert.Equal(2, calls);
    }

    private sealed class GeneratedStream(long length) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int size = (int)Math.Min(count, length - BytesRead);
            buffer.AsSpan(offset, size).Fill(65);
            BytesRead += size;
            return size;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int size = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..size].Fill(65);
            BytesRead += size;
            return ValueTask.FromResult(size);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Response() => new(HttpStatusCode.OK) { Content = new StringContent("feed content") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
