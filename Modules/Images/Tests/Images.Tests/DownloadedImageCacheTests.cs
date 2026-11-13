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
    private static string Url(string name) => "https://images.test/" + name + ".png";

    [Theory]
    [InlineData(null, true, 8)]
    [InlineData(60, true, 3)]
    [InlineData(0, true, 3)]
    [InlineData(604800, true, 7)]
    [InlineData(3456000, true, 30)]
    [InlineData(60, false, 30)]
    public void RecheckDatesRespectValidatorsAndClampServerFreshness(int? maxAgeSeconds, bool validators, int days)
        => Assert.Equal(Now.AddDays(days), DownloadedImageCache.CalculateNextCheck(
            maxAgeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, validators, Now));

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
        Assert.NotNull(await fixture.FetchAsync(cache, "icon"));
        var modified = File.GetLastWriteTimeUtc(fixture.PathFor("icon"));
        fixture.Clock.Advance(TimeSpan.FromDays(8));
        Assert.NotNull(await fixture.FetchAsync(cache, "icon", checksForUpdates: true));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(release.Task.IsCompleted);
        Assert.Equal(Fixture.Image, File.ReadAllBytes(fixture.PathFor("icon")));
        release.TrySetResult();
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(fixture.PathFor("icon")));
        var validation = Assert.IsType<DownloadedImageValidation>(await fixture.Database.FetchDownloadedImageValidationAsync(Url("icon")));
        Assert.Equal(fixture.Clock.GetUtcNow().AddDays(3), validation.NextCheck);
        Assert.Equal("\"v1\"", validation.ConditionalGet?.ETag);
        Assert.Equal(2, requests);
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
        Assert.NotNull(await fixture.FetchAsync(cache, "icon"));
        fixture.Clock.Advance(TimeSpan.FromDays(8));
        var cached = Assert.IsType<DownloadedImage>(await fixture.FetchAsync(cache, "icon", checksForUpdates: true));
        Assert.Equal(Fixture.Image, cached.Data);
        await cache.WaitForRevalidationsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var expected = status == 200 && !corrupt ? replacement : Fixture.Image;
        Assert.Equal(expected, File.ReadAllBytes(fixture.PathFor("icon")));
        Assert.False(fixture.Database.RecentlyFailed(Url("icon")));
        Assert.Equal(expected, (await fixture.FetchAsync(cache, "icon"))?.Data);
        var validation = Assert.IsType<DownloadedImageValidation>(await fixture.Database.FetchDownloadedImageValidationAsync(Url("icon")));
        Assert.Equal(status == 0 || corrupt ? fixture.Clock.GetUtcNow() : fixture.Clock.GetUtcNow().AddDays(8), validation.NextCheck);
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
        fixture.Clock.Advance(TimeSpan.FromDays(8));
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
        Task<DownloadedImage?> FetchAsync(string name, DownloadPriority priority = DownloadPriority.Visible)
            => throughThumbnailFacade ? thumbnails.SourceAsync(Url(name), priority: priority)
                : fixture.FetchAsync(cache, name, limited: true, priority: priority);
        var active = Enumerable.Range(0, 4)
            .Select(index => FetchAsync("active" + index)).ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task WaitForQueuedAsync(int count)
        {
            while (cache.WaitingDownloadCount != count) await Task.Delay(5, timeout.Token);
        }
        try
        {
            for (int index = 0; index < 4; index++) await started.Reader.ReadAsync(timeout.Token);
            var older = FetchAsync("older", DownloadPriority.Prefetch);
            await WaitForQueuedAsync(1);
            var visible = FetchAsync("visible");
            await WaitForQueuedAsync(2);
            var newer = FetchAsync("newer", DownloadPriority.Prefetch);
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

    [Fact]
    public async Task FullSizeReaderDownloadsBypassTheFourOccupiedThumbnailSlots()
    {
        int running = 0;
        var thumbnailsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (request, ct) =>
        {
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
            .Select(index => fixture.FetchAsync(cache, "busy" + index, limited: true)).ToArray();
        try
        {
            await thumbnailsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Fixture.Image, Assert.IsType<DownloadedImage>(
                await thumbnails.FullSizeImageAsync(Url("reader")).WaitAsync(TimeSpan.FromSeconds(5))).Data);
            Assert.All(downloads, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(downloads).WaitAsync(TimeSpan.FromSeconds(5));
        }
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
        fixture.Clock.Advance(TimeSpan.FromDays(8));
        var downloads = Enumerable.Range(0, 4)
            .Select(index => fixture.FetchAsync(cache, "busy" + index, limited: true)).ToArray();
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
        fixture.Clock.Advance(TimeSpan.FromDays(8));
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
            bool limited = false, DownloadPriority priority = DownloadPriority.Visible)
            => (await cache.FetchAsync(Url(name), new(ActivityOwner.FeedImageDownloader,
                ActivityKind.DownloadFeedImage(Url(name)), null, limited ? () => true : null, checksForUpdates,
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
