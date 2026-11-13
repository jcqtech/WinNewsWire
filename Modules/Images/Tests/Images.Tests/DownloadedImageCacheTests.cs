using System.Net;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class DownloadedImageCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    private static string Url(string name) => "https://images.test/" + name + ".png";

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
        private readonly HttpClient _http = new(new Handler());
        private readonly Downloader _downloader;
        internal TestClock Clock { get; } = new();
        internal ImageMetadataDatabase Database { get; }
        internal string SourceDirectory => Path.Combine(_directory, "DownloadedImages");
        internal string PathFor(string name) => Path.Combine(SourceDirectory, Url(name).Md5Hex());
        internal Fixture()
        {
            Directory.CreateDirectory(SourceDirectory);
            Database = new(":memory:", Clock);
            _downloader = new(_http, Clock);
        }
        internal async Task SeedAsync(string name)
        {
            File.WriteAllBytes(PathFor(name), Image);
            File.SetLastWriteTimeUtc(PathFor(name), Clock.GetUtcNow().UtcDateTime);
            await Database.SaveDownloadedImageAsync(new(Url(name), Image.LongLength, (1, 1), null));
        }
        internal DownloadedImageCache CreateCache(int limitImages, int hardLimitImages)
            => new(Database, _directory, Image.LongLength * limitImages, Image.LongLength * hardLimitImages, _downloader, Clock);
        internal async Task<DownloadedImage?> FetchAsync(DownloadedImageCache cache, string name)
            => (await cache.FetchAsync(Url(name), new(ActivityOwner.FeedImageDownloader,
                ActivityKind.DownloadFeedImage(Url(name)), null, null), default)).Image;
        public ValueTask DisposeAsync()
        {
            Database.Dispose();
            _http.Dispose();
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Image) });
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = Now;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
