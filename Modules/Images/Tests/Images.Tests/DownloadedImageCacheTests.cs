using System.Net;
using System.Threading.Channels;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class DownloadedImageCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    private static string Url(string name, string host = "images.test") => "https://" + host + "/" + name + ".png";

    [Fact]
    public void DerivedCopiesFollowTheirOriginalsDuringEvictionAndOrphanCleanup()
    {
        var old = Now.AddDays(-10);
        var records = new[]
        {
            new DownloadedImageUsage(Url("a"), 100, old),
            new DownloadedImageUsage(Url("b"), 100, old.AddSeconds(1)),
            new DownloadedImageUsage(Url("missing"), 100, old.AddSeconds(2))
        };
        var dates = new Dictionary<string, DateTimeOffset>
        {
            [Url("a").Md5Hex()] = old,
            [Url("b").Md5Hex()] = old,
            [DownloadedImageCache.DerivedDiskKey(Url("a"), "144")] = Now,
            [DownloadedImageCache.DerivedDiskKey(Url("b"), "144")] = old,
            [DownloadedImageCache.DerivedDiskKey(Url("missing"), "144")] = Now,
            [DownloadedImageCache.DerivedDiskKey(Url("orphan"), "144")] = old
        };
        var removal = DownloadedImageCache.PlanRemoval(records, dates, Now, new(150), new(1000));
        Assert.Equal([Url("missing"), Url("a")], removal.Urls);
        Assert.Equal(100, removal.RemainingByteCount);
        Assert.True(removal.Keys.SetEquals([
            Url("a").Md5Hex(), DownloadedImageCache.DerivedDiskKey(Url("a"), "144"),
            DownloadedImageCache.DerivedDiskKey(Url("missing"), "144"),
            DownloadedImageCache.DerivedDiskKey(Url("orphan"), "144")]));
    }

    [Fact]
    public async Task DerivedCopiesRoundTripIndependentlyAndDisappearWithEvictedSources()
    {
        await using var fixture = new Fixture();
        await using var cache = fixture.CreateCache(1, 2);
        Assert.NotNull(await fixture.FetchAsync(cache, "a"));
        Assert.Null(await cache.DerivedImageDataAsync(Url("a"), "144"));
        await cache.SaveDerivedImageDataAsync(Url("a"), "144", [1, 2, 3]);
        await cache.SaveDerivedImageDataAsync(Url("a"), "48", [4]);
        Assert.Equal(new byte[] { 1, 2, 3 }, await cache.DerivedImageDataAsync(Url("a"), "144"));
        await cache.DeleteDerivedImageDataAsync(Url("a"), "48");
        Assert.Null(await cache.DerivedImageDataAsync(Url("a"), "48"));
        Assert.Equal(Fixture.Image, File.ReadAllBytes(fixture.PathFor("a")));
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        File.SetLastWriteTimeUtc(fixture.PathFor("a"), fixture.Clock.GetUtcNow().AddDays(-10).UtcDateTime);
        Assert.NotNull(await fixture.FetchAsync(cache, "b"));
        Assert.False(File.Exists(fixture.PathFor("a") + "-144"));
        Assert.Null(await cache.DerivedImageDataAsync(Url("a"), "144"));
        await cache.SaveDerivedImageDataAsync(Url("a"), "144", [5]);
        Assert.False(File.Exists(fixture.PathFor("a") + "-144"));
    }

    [Theory]
    [InlineData(null, true, 8)]
    [InlineData(60, true, 3)]
    [InlineData(0, true, 3)]
    [InlineData(604800, true, 7)]
    [InlineData(3456000, true, 30)]
    [InlineData(60, false, 30)]
    public void RecheckDatesRespectValidatorsAndClampServerFreshness(int? maxAgeSeconds, bool validators, int days)
    {
        var dates = Enumerable.Range(0, 16).Select(_ => DownloadedImageCache.CalculateNextCheck(
            maxAgeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, validators, Now)).ToArray();
        Assert.All(dates, date => Assert.InRange(date, Now.AddDays(days * 0.8), Now.AddDays(days * 1.2)));
        Assert.True(dates.Distinct().Count() > 1, "Sources checked together must not all receive the same next-check date.");
    }

    [Fact]
    public async Task CachedIconsReturnImmediatelyAndA304KeepsTheSourceAndItsValidators()
    {
        int requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (request, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1) return ValidatedResponse(HttpStatusCode.OK, Fixture.Image);
            Assert.Equal("\"v1\"", request.Headers.IfNoneMatch.ToString());
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            var response = new HttpResponseMessage(HttpStatusCode.NotModified);
            response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=60");
            return response;
        });
        await using var cache = fixture.CreateCache(10, 20);
        var changes = new List<string>();
        cache.DownloadedImageChanged += (_, url) => changes.Add(url);
        Assert.NotNull(await fixture.FetchAsync(cache, "icon"));
        var modified = File.GetLastWriteTimeUtc(fixture.PathFor("icon"));
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        Assert.NotNull(await fixture.FetchAsync(cache, "icon", checksForUpdates: true));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(release.Task.IsCompleted);
        Assert.Equal(Fixture.Image, File.ReadAllBytes(fixture.PathFor("icon")));
        release.TrySetResult();
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(fixture.PathFor("icon")));
        var validation = Assert.IsType<DownloadedImageValidation>(await fixture.Database.FetchDownloadedImageValidationAsync(Url("icon")));
        Assert.InRange(validation.NextCheck, fixture.Clock.GetUtcNow().AddDays(2.4), fixture.Clock.GetUtcNow().AddDays(3.6));
        Assert.Equal("\"v1\"", validation.ConditionalGet?.ETag);
        Assert.Equal(2, requests);
        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(200, true)]
    [InlineData(404, false)]
    [InlineData(503, false)]
    [InlineData(0, false)]
    public async Task ChangedSourcesAreReplacedButRevalidationFailuresDoNotPoisonTheCachedCopy(int status, bool corrupt)
    {
        int requests = 0;
        var replacement = Fixture.Image.Concat(new byte[] { 0 }).ToArray();
        await using var fixture = new Fixture((_, _) =>
        {
            if (Interlocked.Increment(ref requests) == 1)
                return Task.FromResult(ValidatedResponse(HttpStatusCode.OK, Fixture.Image));
            if (status == 0) throw new HttpRequestException("Fixture offline.");
            return Task.FromResult(ValidatedResponse((HttpStatusCode)status, corrupt ? "invalid image"u8.ToArray() : replacement));
        });
        await using var cache = fixture.CreateCache(10, 20);
        var changes = new List<string>();
        cache.DownloadedImageChanged += (_, url) =>
        {
            Assert.Equal(replacement, File.ReadAllBytes(fixture.PathFor("icon")));
            Assert.False(File.Exists(fixture.PathFor("icon") + "-48-Png"));
            Assert.False(File.Exists(fixture.PathFor("icon") + "-144-Png"));
            changes.Add(url);
        };
        Assert.NotNull(await fixture.FetchAsync(cache, "icon"));
        await cache.SaveDerivedImageDataAsync(Url("icon"), "48-Png", [1]);
        await cache.SaveDerivedImageDataAsync(Url("icon"), "144-Png", [2]);
        var originalCheck = Assert.IsType<DownloadedImageValidation>(
            await fixture.Database.FetchDownloadedImageValidationAsync(Url("icon"))).NextCheck;
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        var cached = Assert.IsType<DownloadedImage>(await fixture.FetchAsync(cache, "icon", checksForUpdates: true));
        Assert.Equal(Fixture.Image, cached.Data);
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var expected = status == 200 && !corrupt ? replacement : Fixture.Image;
        Assert.Equal(expected, File.ReadAllBytes(fixture.PathFor("icon")));
        Assert.Equal(status != 200 || corrupt, File.Exists(fixture.PathFor("icon") + "-48-Png"));
        Assert.Equal(status != 200 || corrupt, File.Exists(fixture.PathFor("icon") + "-144-Png"));
        if (status == 200 && !corrupt) Assert.Equal([Url("icon")], changes);
        else Assert.Empty(changes);
        Assert.False(fixture.Database.RecentlyFailed(Url("icon")));
        Assert.Equal(expected, (await fixture.FetchAsync(cache, "icon"))?.Data);
        var validation = Assert.IsType<DownloadedImageValidation>(await fixture.Database.FetchDownloadedImageValidationAsync(Url("icon")));
        if (status == 0) Assert.Equal(originalCheck, validation.NextCheck);
        else Assert.InRange(validation.NextCheck, fixture.Clock.GetUtcNow().AddDays(6.4), fixture.Clock.GetUtcNow().AddDays(9.6));
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(408, false)]
    [InlineData(500, false)]
    public async Task TransientRevalidationResponsesApplyTheSharedHostRetryPolicy(int status, bool pausesHost)
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            int count = Interlocked.Increment(ref requests);
            var response = ValidatedResponse(count == 2 ? (HttpStatusCode)status : HttpStatusCode.OK, Fixture.Image);
            if (count == 2) response.Headers.TryAddWithoutValidation("Retry-After", "120");
            return Task.FromResult(response);
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.NotNull(await fixture.FetchAsync(cache, "icon"));
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        Assert.NotNull(await fixture.FetchAsync(cache, "icon", checksForUpdates: true));
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, requests);
        Assert.False(fixture.Database.RecentlyFailed(Url("icon")));
        Assert.Equal(Fixture.Image, File.ReadAllBytes(fixture.PathFor("icon")));
        Assert.Equal(!pausesHost, await fixture.FetchAsync(cache, "peer") is not null);
        if (pausesHost)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(119));
            Assert.Null(await fixture.FetchAsync(cache, "peer"));
            Assert.Equal(2, requests);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.NotNull(await fixture.FetchAsync(cache, "peer"));
        }
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task ExistingHostPauseSkipsDueRevalidationWithoutMovingItsNextCheck()
    {
        int requests = 0;
        await using var fixture = new Fixture((request, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ValidatedResponse(request.RequestUri!.AbsolutePath == "/retry.png"
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, Fixture.Image));
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.NotNull(await fixture.FetchAsync(cache, "icon"));
        var due = Assert.IsType<DownloadedImageValidation>(
            await fixture.Database.FetchDownloadedImageValidationAsync(Url("icon"))).NextCheck;
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.NotNull(await fixture.FetchAsync(cache, "icon", checksForUpdates: true));
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, requests);
        Assert.Equal(due, (await fixture.Database.FetchDownloadedImageValidationAsync(Url("icon")))?.NextCheck);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.NotNull(await fixture.FetchAsync(cache, "icon", checksForUpdates: true));
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, requests);
        Assert.True((await fixture.Database.FetchDownloadedImageValidationAsync(Url("icon")))?.NextCheck > fixture.Clock.GetUtcNow());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ThumbnailReadsAndBlockedNetworksDoNotRevalidate(bool checksForUpdates, bool networkAllowed)
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(ValidatedResponse(HttpStatusCode.OK, Fixture.Image));
        });
        fixture.UpdatesAllowed = networkAllowed;
        await using var cache = fixture.CreateCache(10, 20);
        Assert.NotNull(await fixture.FetchAsync(cache, "a"));
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        Assert.NotNull(await fixture.FetchAsync(cache, "a", checksForUpdates));
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoalescedVisibleRequestsPromoteQueuedSourcesAheadOfNewerPrefetches(bool throughThumbnailFacade)
    {
        using var release = new SemaphoreSlim(0);
        var started = Channel.CreateUnbounded<string>();
        await using var fixture = new Fixture(async (request, ct) =>
        {
            started.Writer.TryWrite(request.RequestUri!.AbsolutePath);
            await release.WaitAsync(ct);
            return ValidatedResponse(HttpStatusCode.OK, Fixture.Image);
        });
        await using var cache = fixture.CreateCache(20, 40);
        await using var thumbnails = fixture.CreateThumbnails(cache);
        Task<DownloadedImage?> FetchAsync(string name, ThumbnailPriority priority = ThumbnailPriority.Visible,
            string host = "images.test")
            => throughThumbnailFacade ? thumbnails.SourceAsync(Url(name, host), priority: priority)
                : fixture.FetchAsync(cache, name, limited: true, priority: priority, host: host);
        var active = Enumerable.Range(0, 4)
            .Select(index => FetchAsync("active" + index, host: "active" + index / 2 + ".test")).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task WaitForQueuedAsync(int count)
        {
            while (cache.WaitingDownloadCount != count) await Task.Delay(5, timeout.Token);
        }
        try
        {
            for (int index = 0; index < 4; index++) await started.Reader.ReadAsync(timeout.Token);
            var older = FetchAsync("older", ThumbnailPriority.Prefetch);
            await WaitForQueuedAsync(1);
            var visible = FetchAsync("visible", host: "visible.test");
            await WaitForQueuedAsync(2);
            var newer = FetchAsync("newer", ThumbnailPriority.Prefetch, host: "newer.test");
            await WaitForQueuedAsync(3);
            var promoted = fixture.FetchAsync(cache, "older", limited: true);
            release.Release();
            Assert.Equal("/older.png", await started.Reader.ReadAsync(timeout.Token));
            release.Release();
            Assert.Equal("/visible.png", await started.Reader.ReadAsync(timeout.Token));
            release.Release();
            Assert.Equal("/newer.png", await started.Reader.ReadAsync(timeout.Token));
            release.Release(4);
            await Task.WhenAll(active.Concat([older, visible, newer, promoted])).WaitAsync(timeout.Token);
            Assert.False(started.Reader.TryRead(out _));
        }
        finally
        {
            release.Release(10);
            await Task.WhenAll(active).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullSizeReaderDownloadsBypassTheFourOccupiedThumbnailSlots(bool alreadyQueued)
    {
        int running = 0, readerRequests = 0;
        var thumbnailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/reader.png") Interlocked.Increment(ref readerRequests);
            if (request.RequestUri!.AbsolutePath.StartsWith("/busy", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref running) == 4) thumbnailsStarted.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return ValidatedResponse(HttpStatusCode.OK, Fixture.Image);
        });
        await using var cache = fixture.CreateCache(10, 20);
        await using var thumbnails = fixture.CreateThumbnails(cache);
        var downloads = Enumerable.Range(0, 4)
            .Select(index => fixture.FetchAsync(cache, "busy" + index, limited: true, host: "busy" + index / 2 + ".test")).ToArray();
        Task<DownloadedImage?>? queued = null;
        try
        {
            await thumbnailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (alreadyQueued)
            {
                queued = thumbnails.SourceAsync(Url("reader"), priority: ThumbnailPriority.Prefetch);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (cache.WaitingDownloadCount != 1) await Task.Delay(5, timeout.Token);
            }
            Assert.Equal(Fixture.Image, Assert.IsType<DownloadedImage>(
                await thumbnails.FullSizeImageAsync(Url("reader")).WaitAsync(TimeSpan.FromSeconds(5))).Data);
            if (queued is not null) Assert.NotNull(await queued.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, readerRequests);
            Assert.All(downloads, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(downloads).WaitAsync(TimeSpan.FromSeconds(5));
            if (queued is not null) await queued.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LimitedDownloadsUseTwoSlotsPerHostAndLeaveRoomForOtherHosts()
    {
        using var slowRelease = new SemaphoreSlim(0);
        using var fastRelease = new SemaphoreSlim(0);
        var started = Channel.CreateUnbounded<string>();
        await using var fixture = new Fixture(async (request, ct) =>
        {
            started.Writer.TryWrite(request.RequestUri!.AbsolutePath);
            await (request.RequestUri.IdnHost == "slow.test" ? slowRelease : fastRelease).WaitAsync(ct);
            return ValidatedResponse(HttpStatusCode.OK, Fixture.Image);
        });
        await using var cache = fixture.CreateCache(10, 20);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tasks = new List<Task<DownloadedImage?>>
        {
            fixture.FetchAsync(cache, "slow1", limited: true, host: "slow.test"),
            fixture.FetchAsync(cache, "slow2", limited: true, host: "SLOW.test:8443")
        };
        try
        {
            var first = new[] { await started.Reader.ReadAsync(timeout.Token), await started.Reader.ReadAsync(timeout.Token) };
            Assert.True(first.ToHashSet().SetEquals(["/slow1.png", "/slow2.png"]));
            tasks.Add(fixture.FetchAsync(cache, "slow3", limited: true, host: "slow.test"));
            while (cache.WaitingDownloadCount != 1) await Task.Delay(5, timeout.Token);
            tasks.Add(fixture.FetchAsync(cache, "fast1", limited: true, host: "fast.test"));
            tasks.Add(fixture.FetchAsync(cache, "fast2", limited: true, host: "fast.test"));
            var fast = new[] { await started.Reader.ReadAsync(timeout.Token), await started.Reader.ReadAsync(timeout.Token) };
            Assert.True(fast.ToHashSet().SetEquals(["/fast1.png", "/fast2.png"]));
            tasks.Add(fixture.FetchAsync(cache, "fast3", limited: true, host: "fast.test"));
            while (cache.WaitingDownloadCount != 2) await Task.Delay(5, timeout.Token);
            slowRelease.Release();
            Assert.Equal("/slow3.png", await started.Reader.ReadAsync(timeout.Token));
            fastRelease.Release();
            Assert.Equal("/fast3.png", await started.Reader.ReadAsync(timeout.Token));
        }
        finally
        {
            slowRelease.Release(10);
            fastRelease.Release(10);
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.All(await Task.WhenAll(tasks), image => Assert.NotNull(image));
        Assert.False(started.Reader.TryRead(out _));
    }

    [Fact]
    public async Task IconRevalidationDoesNotWaitBehindTheFourActiveThumbnailDownloads()
    {
        int running = 0;
        var thumbnailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revalidationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/busy", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref running) == 4) thumbnailsStarted.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            else if (request.Headers.IfNoneMatch.Count != 0)
                revalidationStarted.TrySetResult();
            return ValidatedResponse(HttpStatusCode.OK, Fixture.Image);
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.NotNull(await fixture.FetchAsync(cache, "icon"));
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        var downloads = Enumerable.Range(0, 4)
            .Select(index => fixture.FetchAsync(cache, "busy" + index, limited: true, host: "busy" + index / 2 + ".test")).ToArray();
        try
        {
            await thumbnailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(await fixture.FetchAsync(cache, "icon", checksForUpdates: true));
            await revalidationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(downloads, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(downloads).WaitAsync(TimeSpan.FromSeconds(5));
        }
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsBackgroundRevalidationWithoutPoisoningTheCopy()
    {
        int requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (_, ct) =>
        {
            if (Interlocked.Increment(ref requests) == 1) return ValidatedResponse(HttpStatusCode.OK, Fixture.Image);
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.NotNull(await fixture.FetchAsync(cache, "a"));
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        Assert.NotNull(await fixture.FetchAsync(cache, "a", checksForUpdates: true));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Fixture.Image, File.ReadAllBytes(fixture.PathFor("a")));
        Assert.False(fixture.Database.RecentlyFailed(Url("a")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.FetchAsync(cache, "a"));
    }

    private static HttpResponseMessage ValidatedResponse(HttpStatusCode status, byte[] data)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
        response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
        return response;
    }

    [Fact]
    public async Task ActiveRevalidationSurvivesPruningUntilItsNetworkOperationFinishes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ValidatedResponse(HttpStatusCode.NotModified, []);
        });
        await fixture.SeedAsync("active");
        await fixture.Database.SaveDownloadedImageAsync(new(Url("active"), Fixture.Image.Length, (1, 1),
            new(null, "\"v1\""), fixture.Clock.GetUtcNow()));
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        await using var cache = fixture.CreateCache(2, 2);
        try
        {
            Assert.NotNull(await fixture.FetchAsync(cache, "active", checksForUpdates: true));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.SeedAsync("other1");
            await fixture.SeedAsync("other2");
            fixture.Clock.Advance(TimeSpan.FromDays(10));
            await cache.RemoveImagesOverSizeLimitAsync();
            Assert.True(File.Exists(fixture.PathFor("active")));
            Assert.False(File.Exists(fixture.PathFor("other1")));
            Assert.False(File.Exists(fixture.PathFor("other2")));
        }
        finally
        {
            release.TrySetResult();
            await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        await fixture.SeedAsync("other3");
        await fixture.SeedAsync("other4");
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        await cache.RemoveImagesOverSizeLimitAsync();
        Assert.False(File.Exists(fixture.PathFor("active")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveImagesAreProtectedUnderBothSoftAndHardLimits(bool hardLimit)
    {
        var old = Now.AddDays(hardLimit ? -1 : -10);
        var records = Enumerable.Range(0, 3)
            .Select(index => new DownloadedImageUsage(Url(index.ToString()), 100, old)).ToArray();
        var dates = records.ToDictionary(record => record.Url.Md5Hex(), _ => old);
        var removal = DownloadedImageCache.PlanRemoval(records, dates, Now, new(200), new(hardLimit ? 200 : 1000),
            new HashSet<string>(StringComparer.Ordinal) { records[0].Url });
        Assert.Equal(records.Skip(1).Select(record => record.Url), removal.Urls);
        Assert.Equal(100, removal.RemainingByteCount);
        Assert.DoesNotContain(records[0].Url.Md5Hex(), removal.Keys);
    }

    [Fact]
    public void LeastRecentImagesAndOrphansAreRemovedDownToTheTarget()
    {
        var old = Now.AddDays(-10);
        var urls = new[] { Url("a"), Url("b"), Url("c"), Url("fresh") };
        var records = new[] { new DownloadedImageUsage(Url("missing"), 50, old) }
            .Concat(urls.Select(url => new DownloadedImageUsage(url, 100, url == urls[3] ? Now : old))).ToArray();
        var dates = urls.ToDictionary(url => url.Md5Hex(), _ => old);
        dates["orphan"] = old;
        dates["fresh-orphan"] = Now;
        var removal = DownloadedImageCache.PlanRemoval(records, dates, Now, new(350), new(1000));
        Assert.Equal([Url("missing"), urls[0], urls[1]], removal.Urls);
        Assert.True(removal.Keys.SetEquals(["orphan", urls[0].Md5Hex(), urls[1].Md5Hex()]));
        Assert.Equal(200L, removal.RemainingByteCount);
    }

    [Fact]
    public void RecentUseIsProtectedUnlessTheHardLimitIsExceeded()
    {
        var records = Enumerable.Range(0, 5)
            .Select(index => new DownloadedImageUsage(Url(index.ToString()), 100, Now.AddDays(-1).AddSeconds(index))).ToArray();
        var dates = records.ToDictionary(row => row.Url.Md5Hex(), _ => Now.AddDays(-1));
        var protectedImages = DownloadedImageCache.PlanRemoval(records, dates, Now, new(200), new(1000));
        Assert.Empty(protectedImages.Urls);
        Assert.Equal(500L, protectedImages.RemainingByteCount);
        var capped = DownloadedImageCache.PlanRemoval(records, dates, Now, new(200), new(400));
        Assert.Equal(records.Take(2).Select(row => row.Url), capped.Urls);
        Assert.Equal(300L, capped.RemainingByteCount);
    }

    [Fact]
    public void MinuteOldChangesAreProtectedEvenWhenTheirPreviousRecordIsOld()
    {
        var records = new[]
        {
            new DownloadedImageUsage(Url("being-replaced"), 100, Now.AddDays(-10)),
            new DownloadedImageUsage(Url("old"), 100, Now.AddDays(-9)),
            new DownloadedImageUsage(Url("record-before-file"), 100, Now.AddMinutes(-1))
        };
        var dates = new Dictionary<string, DateTimeOffset>
        {
            [records[0].Url.Md5Hex()] = Now.AddMinutes(-1),
            [records[1].Url.Md5Hex()] = Now.AddDays(-9),
            ["fresh-orphan"] = Now.AddMinutes(-1)
        };
        var removal = DownloadedImageCache.PlanRemoval(records, dates, Now, new(50), new(100));
        Assert.Equal([records[1].Url], removal.Urls);
        Assert.True(removal.Keys.SetEquals([records[1].Url.Md5Hex()]));
        Assert.Equal(200L, removal.RemainingByteCount);
    }

    [Fact]
    public async Task StartupEvictsDiskFilesAndMetadataTogetherWithoutRemovingRecentOrphans()
    {
        await using var fixture = new Fixture();
        foreach (var name in new[] { "a", "b", "c", "missing" }) await fixture.SeedAsync(name);
        File.Delete(fixture.PathFor("missing"));
        string orphan = Path.Combine(fixture.SourceDirectory, "orphan");
        string recentOrphan = Path.Combine(fixture.SourceDirectory, "recent-orphan");
        File.WriteAllBytes(orphan, Fixture.Image);
        File.SetLastWriteTimeUtc(orphan, fixture.Clock.GetUtcNow().UtcDateTime);
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        await fixture.SeedAsync("fresh");
        File.WriteAllBytes(recentOrphan, Fixture.Image);
        File.SetLastWriteTimeUtc(recentOrphan, fixture.Clock.GetUtcNow().UtcDateTime);
        await using (var cache = fixture.CreateCache(3, 10)) { }
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(recentOrphan));
        Assert.False(File.Exists(fixture.PathFor("a")));
        Assert.False(File.Exists(fixture.PathFor("b")));
        Assert.True(File.Exists(fixture.PathFor("c")));
        Assert.True(File.Exists(fixture.PathFor("fresh")));
        Assert.Equal([Url("c"), Url("fresh")],
            (await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).Select(row => row.Url));
        Assert.False(fixture.Database.IsUnsuitableForThumbnail(Url("a")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFolderListingLeavesSourceFilesAndMetadataUntouched(bool blockedByFile)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync("a");
        await fixture.SeedAsync("b");
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        string unavailable = fixture.SourceDirectory + "-unavailable";
        Directory.Move(fixture.SourceDirectory, unavailable);
        try
        {
            if (blockedByFile) File.WriteAllText(fixture.SourceDirectory, "Not a directory");
            await using (var cache = fixture.CreateCache(1, 2)) { }
            Assert.Equal([Url("a"), Url("b")],
                (await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).Select(record => record.Url));
            Assert.Equal(2, Directory.GetFiles(unavailable).Length);
        }
        finally
        {
            if (blockedByFile) File.Delete(fixture.SourceDirectory);
            Directory.Move(unavailable, fixture.SourceDirectory);
        }
    }

    [Fact]
    public async Task FailedSourceWritesStayTransientAndBackOffWithoutBlockingOtherUrls()
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixture.Image) });
        });
        string blocked = fixture.PathFor("write-blocked");
        Directory.CreateDirectory(blocked);
        await using var cache = fixture.CreateCache(10, 20);
        var result = await cache.FetchAsync(Url("write-blocked"), new(ActivityOwner.FeedImageDownloader,
            ActivityKind.DownloadFeedImage(Url("write-blocked")), null, null), default);
        Assert.True(result.IsTransient);
        Assert.False(result.DecodingFailed);
        Assert.Null(result.Image);
        Assert.False(fixture.Database.RecentlyFailed(Url("write-blocked")));
        Assert.Empty(await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync());
        Directory.Delete(blocked);
        Assert.Null(await fixture.FetchAsync(cache, "write-blocked"));
        Assert.Equal(1, requests);
        Assert.NotNull(await fixture.FetchAsync(cache, "peer"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await fixture.FetchAsync(cache, "write-blocked"));
        Assert.Equal(3, requests);
        Assert.Equal(2, (await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).Count);
    }

    [Fact]
    public async Task NewDownloadsTriggerLruRemovalWithoutWaitingForAnotherLaunch()
    {
        await using var fixture = new Fixture();
        foreach (var name in new[] { "a", "b", "c" }) await fixture.SeedAsync(name);
        fixture.Clock.Advance(TimeSpan.FromDays(10));
        await using var cache = fixture.CreateCache(3, 10);
        Assert.NotNull(await fixture.FetchAsync(cache, "fresh"));
        Assert.False(File.Exists(fixture.PathFor("a")));
        Assert.False(File.Exists(fixture.PathFor("b")));
        Assert.True(File.Exists(fixture.PathFor("c")));
        Assert.True(File.Exists(fixture.PathFor("fresh")));
        Assert.Equal(2, (await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).Count);
    }

    [Fact]
    public async Task AccessDateThrottleIsBoundedWithoutLosingPersistentUsage()
    {
        await using var fixture = new Fixture();
        await using var cache = fixture.CreateCache(10, 20);
        Assert.NotNull(await fixture.FetchAsync(cache, "a"));
        var saved = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromHours(23));
        await cache.ImageWasUsedAsync(Url("a"), false, default);
        Assert.Equal(saved, Assert.Single(await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).LastAccessed);
        for (int index = 0; index < 10_000; index++)
            await cache.ImageWasUsedAsync(Url("other-" + index), false, default);
        await cache.ImageWasUsedAsync(Url("a"), false, default);
        Assert.Equal(fixture.Clock.GetUtcNow(),
            Assert.Single(await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).LastAccessed);
        Assert.True(File.Exists(fixture.PathFor("a")));
    }

    [Fact]
    public async Task DiskHitsSaveAccessDatesAtMostOnceADayIncludingTheBoundary()
    {
        await using var fixture = new Fixture();
        await using var cache = fixture.CreateCache(10, 20);
        Assert.NotNull(await fixture.FetchAsync(cache, "a"));
        var saved = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromHours(23));
        Assert.NotNull(await fixture.FetchAsync(cache, "a"));
        Assert.Equal(saved, Assert.Single(await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).LastAccessed);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await fixture.FetchAsync(cache, "a"));
        Assert.Equal(fixture.Clock.GetUtcNow(),
            Assert.Single(await fixture.Database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).LastAccessed);
    }

    [Fact]
    public async Task TransientRetriesDoubleFromOneMinuteToOneHourAndSuccessResetsTheDelay()
    {
        int requests = 0;
        bool succeeds = false;
        await using var fixture = new Fixture((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(succeeds ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            {
                Content = new ByteArrayContent(Fixture.Image)
            });
        });
        await using var cache = fixture.CreateCache(10, 20);
        int expected = 0;
        foreach (int minutes in new[] { 1, 2, 4, 8, 16, 32, 60, 60 })
        {
            Assert.Null(await fixture.FetchAsync(cache, "retry"));
            Assert.Equal(++expected, requests);
            fixture.Clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Null(await fixture.FetchAsync(cache, "retry"));
            Assert.Equal(expected, requests);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        succeeds = true;
        Assert.NotNull(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(++expected, requests);
        File.Delete(fixture.PathFor("retry"));
        succeeds = false;
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(++expected, requests);
        fixture.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(expected, requests);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        succeeds = true;
        Assert.NotNull(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(++expected, requests);
        Assert.False(fixture.Database.RecentlyFailed(Url("retry")));
    }

    [Fact]
    public async Task ASourceWrittenDuringRetryBackoffIsStillAvailableImmediately()
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        await fixture.SeedAsync("retry");
        Assert.NotNull(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkChangesClearTransientDelaysAndRestartAtOneMinute(bool available)
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(1, requests);
        await Task.Run(() => cache.NetworkStatusChanged(null, available));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(2, requests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task PermanentFailuresClearTheEarlierTransientDelay()
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(requests switch
            {
                2 => HttpStatusCode.NotFound,
                4 => HttpStatusCode.OK,
                _ => HttpStatusCode.ServiceUnavailable
            })
            {
                Content = new ByteArrayContent(Fixture.Image)
            });
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(2, requests);
        Assert.True(fixture.Database.RecentlyFailed(Url("retry")));
        fixture.Clock.Advance(ImageDatabase.FailureRetry);
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(3, requests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(4, requests);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("5", 60)]
    [InlineData("120", 120)]
    [InlineData("7200", 7200)]
    [InlineData("invalid", 60)]
    [InlineData("315537897599", 60)]
    public async Task HostCooldownHonorsRetryAfterWithoutBlockingDiskOrOtherHosts(string? retryAfter, int seconds)
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            var response = new HttpResponseMessage(++requests == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Fixture.Image)
            };
            if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Null(await fixture.FetchAsync(cache, "peer"));
        Assert.Equal(1, requests);
        var other = await cache.FetchAsync("https://other.test/icon.png", new(ActivityOwner.FeedImageDownloader,
            ActivityKind.DownloadFeedImage("https://other.test/icon.png"), null, null), default);
        Assert.NotNull(other.Image);
        await fixture.SeedAsync("cached");
        Assert.NotNull(await fixture.FetchAsync(cache, "cached"));
        Assert.Null(await fixture.FetchAsync(cache, "peer"));
        Assert.Equal(2, requests);
        fixture.Clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.Null(await fixture.FetchAsync(cache, "peer"));
        Assert.Equal(2, requests);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await fixture.FetchAsync(cache, "peer"));
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(429, 120)]
    [InlineData(500, 120)]
    [InlineData(429, 5)]
    public async Task ServerRetryAfterWaitsSurviveNetworkNotificationsForUrlsAndHosts(int status, int retryAfter)
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            var response = new HttpResponseMessage(++requests == 1 ? (HttpStatusCode)status : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Fixture.Image)
            };
            if (requests == 1) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter.ToString());
            return Task.FromResult(response);
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        cache.NetworkStatusChanged(null, false);
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(status == 500, await fixture.FetchAsync(cache, "peer") is not null);
        cache.NetworkStatusChanged(null, true);
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(Math.Max(60, retryAfter) - 31));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(status == 500 ? 3 : 2, requests);
    }

    [Theory]
    [InlineData(null, 60, false)]
    [InlineData("5", 60, false)]
    [InlineData("120", 121, false)]
    [InlineData("7200", 7201, false)]
    [InlineData("120", 121, true)]
    [InlineData("7200", 7201, true)]
    public async Task ConcurrentHostFailuresDoNotExtendAnActiveWaitWithoutALongerRetryAfter(
        string? retryAfter, int retryAtSeconds, bool networkChanged)
    {
        int requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (request, ct) =>
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath == "/held.png")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
                return response;
            }
            return new HttpResponseMessage(request.RequestUri.AbsolutePath == "/first.png"
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent(Fixture.Image) };
        });
        await using var cache = fixture.CreateCache(10, 20);
        var held = fixture.FetchAsync(cache, "held");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await fixture.FetchAsync(cache, "first"));
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            release.TrySetResult();
            Assert.Null(await held);
            if (networkChanged) cache.NetworkStatusChanged(null, true);
            fixture.Clock.Advance(TimeSpan.FromSeconds(retryAtSeconds - 2));
            Assert.Null(await fixture.FetchAsync(cache, "peer"));
            Assert.Equal(2, requests);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.NotNull(await fixture.FetchAsync(cache, "peer"));
            Assert.Equal(3, requests);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(200, false)]
    [InlineData(408, false)]
    [InlineData(500, false)]
    [InlineData(502, false)]
    [InlineData(504, false)]
    [InlineData(599, false)]
    public async Task OnlyRateLimitsOverloadAndTransportFailuresPauseTheEntireHost(int statusCode, bool blocksHost)
    {
        int requests = 0;
        await using var fixture = new Fixture((request, _) =>
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath != "/failed.png")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixture.Image) });
            if (statusCode == 0) throw new HttpRequestException("The host did not respond.");
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new ByteArrayContent(statusCode == 200 ? "<html>Sign in</html>"u8.ToArray() : [])
            });
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.Null(await fixture.FetchAsync(cache, "failed"));
        Assert.Null(await fixture.FetchAsync(cache, "failed"));
        Assert.Equal(1, requests);
        var peer = await fixture.FetchAsync(cache, "peer");
        Assert.Equal(blocksHost, peer is null);
        Assert.Equal(blocksHost ? 1 : 2, requests);
        Assert.Null(await fixture.FetchAsync(cache, "failed"));
        Assert.Equal(blocksHost ? 1 : 2, requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task QueuedPrefetchRechecksCurrentPriorityPolicyUnlessAReaderStartsItNow(bool promote, bool reader)
    {
        int requests = 0;
        bool allowsPrefetch = true;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (request, ct) =>
        {
            int count = Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath.StartsWith("/active-", StringComparison.Ordinal))
            {
                if (count == 4) started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixture.Image) };
        });
        await using var cache = fixture.CreateCache(10, 20);
        await using var thumbnails = fixture.CreateThumbnails(cache);
        var active = Enumerable.Range(0, 4).Select(index =>
            thumbnails.SourceAsync(Url("active-" + index, "active" + index / 2 + ".test"))).ToArray();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = thumbnails.SourceAsync(Url("queued"), priority: ThumbnailPriority.Prefetch,
                priorityAllowsDownloads: priority => priority == ThumbnailPriority.Visible || Volatile.Read(ref allowsPrefetch));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (cache.WaitingDownloadCount != 1) await Task.Delay(5, timeout.Token);
            Volatile.Write(ref allowsPrefetch, false);
            if (promote) cache.PrioritizeDownload(Url("queued"));
            if (reader)
            {
                Assert.NotNull(await thumbnails.FullSizeImageAsync(Url("queued")).WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(5, requests);
            }
            release.TrySetResult();
            await Task.WhenAll(active);
            Assert.Equal(promote || reader, await queued is not null);
            Assert.Equal(promote || reader ? 5 : 4, requests);
            Assert.False(fixture.Database.RecentlyFailed(Url("queued")));
            if (!promote && !reader)
            {
                Volatile.Write(ref allowsPrefetch, true);
                Assert.NotNull(await thumbnails.SourceAsync(Url("queued")));
                Assert.Equal(5, requests);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ConsecutiveTransientFailuresOnDifferentUrlsDoubleTheirHostDelay()
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.Null(await fixture.FetchAsync(cache, "first"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await fixture.FetchAsync(cache, "second"));
        Assert.Equal(2, requests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await fixture.FetchAsync(cache, "third"));
        Assert.Equal(2, requests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await fixture.FetchAsync(cache, "third"));
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AnAlreadyRunningNonTransientResponseEndsItsHostCooldown(HttpStatusCode status)
    {
        int requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (request, ct) =>
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.AbsolutePath == "/healthy.png")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
                return new HttpResponseMessage(status) { Content = new ByteArrayContent(Fixture.Image) };
            }
            return new HttpResponseMessage(request.RequestUri.AbsolutePath == "/retry.png"
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new ByteArrayContent(Fixture.Image) };
        });
        await using var cache = fixture.CreateCache(10, 20);
        var healthy = fixture.FetchAsync(cache, "healthy");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Null(await fixture.FetchAsync(cache, "peer"));
        Assert.Equal(2, requests);
        release.TrySetResult();
        await healthy;
        Assert.NotNull(await fixture.FetchAsync(cache, "peer"));
        Assert.Null(await fixture.FetchAsync(cache, "retry"));
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptivePortalDataStaysExcludedUntilNetworkNotificationWithoutPersistingFailure(bool available)
    {
        int requests = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(requests == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(requests == 2 ? "<html>Login</html>"u8.ToArray() : Fixture.Image)
            });
        });
        await using var cache = fixture.CreateCache(10, 20);
        Assert.Null(await fixture.FetchAsync(cache, "portal"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await fixture.FetchAsync(cache, "portal"));
        Assert.Equal(2, requests);
        Assert.False(fixture.Database.RecentlyFailed(Url("portal")));
        Assert.False(File.Exists(fixture.PathFor("portal")));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await fixture.FetchAsync(cache, "portal"));
        Assert.Equal(2, requests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await fixture.FetchAsync(cache, "portal"));
        Assert.Equal(2, requests);
        fixture.Clock.Advance(TimeSpan.FromDays(7));
        Assert.Null(await fixture.FetchAsync(cache, "portal"));
        Assert.Equal(2, requests);
        await Task.Run(() => cache.NetworkStatusChanged(null, available));
        Assert.NotNull(await fixture.FetchAsync(cache, "portal"));
        Assert.Equal(3, requests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly byte[] Image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZssAAAAASUVORK5CYII=");
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "downloaded-image-cache-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly Downloader _downloader;
        internal TestClock Clock { get; } = new();
        internal ImageDatabase Database { get; }
        internal bool UpdatesAllowed { get; set; } = true;
        internal string SourceDirectory => Path.Combine(_directory, "DownloadedImages");
        internal string PathFor(string name) => Path.Combine(SourceDirectory, Url(name).Md5Hex());
        internal Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond = null)
        {
            Directory.CreateDirectory(SourceDirectory);
            Database = new(":memory:", Clock);
            _http = new(new Handler(respond ?? ((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Image) }))));
            _downloader = new(_http, Clock);
        }
        internal async Task SeedAsync(string name)
        {
            File.WriteAllBytes(PathFor(name), Image);
            File.SetLastWriteTimeUtc(PathFor(name), Clock.GetUtcNow().UtcDateTime);
            await Database.SaveDownloadedImageAsync(new(Url(name), Image.LongLength, (1, 1), null));
        }
        internal DownloadedImageCache CreateCache(int limitImages, int hardLimitImages)
            => new(Database, _directory, Image.LongLength * limitImages, Image.LongLength * hardLimitImages,
                _downloader, Clock, networkAllowsUpdates: () => UpdatesAllowed);
        internal ThumbnailDownloader CreateThumbnails(DownloadedImageCache cache)
            => new(Database, _directory, () => true, downloadedImages: cache);
        internal async Task<DownloadedImage?> FetchAsync(DownloadedImageCache cache, string name, bool checksForUpdates = false,
            bool limited = false, ThumbnailPriority priority = ThumbnailPriority.Visible, string host = "images.test")
            => (await cache.FetchAsync(Url(name, host), new(ActivityOwner.FeedImageDownloader,
                ActivityKind.DownloadFeedImage(Url(name, host)), null, limited ? () => true : null, checksForUpdates,
                limited ? priority : null), default)).Image;
        public ValueTask DisposeAsync()
        {
            Database.Dispose();
            _http.Dispose();
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => respond(request, ct);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = Now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
