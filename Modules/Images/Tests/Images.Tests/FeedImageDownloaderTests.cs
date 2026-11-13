using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.HTMLMetadata;
using WinNewsWire.Web;
using Xunit;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.Images.Tests;

public sealed class FeedImageDownloaderTests
{
    private const string Home = "https://site.test/";
    private const string Feed = "https://site.test/feed";
    private static readonly byte[] Image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZssAAAAASUVORK5CYII=");

    [Fact]
    public async Task ExplicitFaviconSkipsDiscoveryAndUsesTheNativeMd5Filename()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Image)));
        var first = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home, Home + "icon.png")));
        Assert.True(File.Exists(first.Path));
        Assert.Equal(fixture.NativePath(Home + "icon.png"), first.Path);
        fixture.Images.EmptyCache();
        var second = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home, Home + "icon.png")));
        Assert.Equal(first.Data, second.Data);
        Assert.Single(fixture.Requests);
        Assert.Equal(Home + "icon.png", fixture.ImageDatabase.FaviconUrl(Home));
    }

    [Fact]
    public async Task DiscoverySkipsUnsupportedAndFailedCandidatesWithoutStalling()
    {
        await using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/" => HtmlResponse("<link rel='icon' href='data:image/png;base64,AA'><link rel='icon' href='/icon.svg'><link rel='icon' href='/bad.png'><link rel='icon' href='/good.png'>"),
            "/bad.png" => Response(HttpStatusCode.NotFound),
            "/good.png" => Response(HttpStatusCode.OK, Image),
            _ => throw new InvalidOperationException("Unexpected candidate: " + request.RequestUri)
        }));
        var image = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home)));
        Assert.Equal(Home + "good.png", image.Url);
        Assert.Equal([Home, Home + "bad.png", Home + "good.png"], fixture.Requests);
        Assert.True(fixture.ImageDatabase.RecentlyFailed(Home + "bad.png"));
        Assert.Equal(image.Url, fixture.ImageDatabase.FaviconUrl(Home));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RootFaviconIsAvailableEvenWhenHomepageMetadataCannotBeDownloaded(HttpStatusCode homepageStatus)
    {
        await using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/" => Response(homepageStatus),
            "/favicon.ico" => Response(HttpStatusCode.OK, Image),
            _ => throw new InvalidOperationException("Unexpected image request.")
        }));

        var image = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home)));

        Assert.Equal(Home + "favicon.ico", image.Url);
        Assert.Equal([Home, Home + "favicon.ico"], fixture.Requests);
        Assert.Equal(image.Url, fixture.ImageDatabase.FaviconUrl(Home));
        Assert.False(fixture.ImageDatabase.HomePageHasNoFavicon(Home));
    }

    [Fact]
    public async Task CachedHomepageFailureDoesNotPreventRootFaviconFallback()
    {
        await using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? Response(HttpStatusCode.Forbidden) : Response(HttpStatusCode.OK, Image)));
        Assert.Null(await fixture.Metadata.GetMetadataAsync(Home));
        Assert.Null(await fixture.Images.IconAsync(new(Feed, Home)));

        var image = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home)));

        Assert.Equal(Home + "favicon.ico", image.Url);
        Assert.Equal([Home, Home + "favicon.ico"], fixture.Requests);
    }

    [Fact]
    public async Task UnavailableMetadataDoesNotPersistAnEmptyFaviconCandidateList()
    {
        await using var fixture = new Fixture((request, _) => Task.FromResult(Response(
            request.RequestUri!.AbsolutePath == "/" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound)));

        Assert.Null(await fixture.Images.FaviconAsync(new(Feed, Home)));

        Assert.Equal([Home, Home + "favicon.ico"], fixture.Requests);
        Assert.False(fixture.ImageDatabase.HomePageHasNoFavicon(Home));
    }

    [Fact]
    public async Task DefaultOnlyFailureExpiresAndRetriesAfterFiveDays()
    {
        int iconCalls = 0;
        await using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? HtmlResponse("") : Response(++iconCalls == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK, iconCalls > 1 ? Image : [])));
        Assert.Null(await fixture.Images.FaviconAsync(new(Feed, Home)));
        Assert.True(fixture.ImageDatabase.HomePageHasNoFavicon(Home));
        fixture.Clock.Advance(TimeSpan.FromDays(5) - TimeSpan.FromSeconds(1));
        Assert.Null(await fixture.Images.FaviconAsync(new(Feed, Home)));
        Assert.Equal(1, iconCalls);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await fixture.Images.FaviconAsync(new(Feed, Home)));
        Assert.Equal(2, iconCalls);
        Assert.False(fixture.ImageDatabase.HomePageHasNoFavicon(Home));
    }

    [Fact]
    public async Task TransientDefaultFailureDoesNotPersistANoFaviconVerdict()
    {
        int calls = 0;
        await using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/"
            ? HtmlResponse("") : Response(++calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, calls > 1 ? Image : [])));
        Assert.Null(await fixture.Images.FaviconAsync(new(Feed, Home)));
        Assert.False(fixture.ImageDatabase.HomePageHasNoFavicon(Home));
        Assert.False(fixture.ImageDatabase.RecentlyFailed(Home + "favicon.ico"));
        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.NotNull(await fixture.Images.FaviconAsync(new(Feed, Home)));
    }

    [Fact]
    public async Task DeclaredIconWinsWithoutAnEarlyHomepageFallback()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (request, ct) =>
        {
            Assert.Equal("/declared.png", request.RequestUri!.AbsolutePath);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response(HttpStatusCode.OK, Image);
        });
        await fixture.ImageDatabase.SaveFeedIconUrlAsync(Feed, Home + "old.png");
        var pending = fixture.Images.IconAsync(new(Feed, Home, IconUrl: Home + "declared.png"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        Assert.Single(fixture.Requests);
        release.TrySetResult();
        Assert.Equal(Home + "declared.png", (await pending)?.Url);
        Assert.Equal(Home + "declared.png", fixture.ImageDatabase.IconUrl(Feed));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.OK)]
    public async Task DefinitivelyFailedDeclaredIconUsesHomepageMetadata(HttpStatusCode failure)
    {
        await using var fixture = new Fixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/declared.png" => Response(failure, "not an image"u8.ToArray()),
            "/" => HtmlResponse("<link rel='apple-touch-icon' sizes='180x180' href='/touch.png'>"),
            "/touch.png" => Response(HttpStatusCode.OK, Image),
            _ => throw new InvalidOperationException("Unexpected URL.")
        }));
        Assert.Equal(Home + "touch.png", (await fixture.Images.IconAsync(new(Feed, Home, IconUrl: Home + "declared.png")))?.Url);
        Assert.Equal(Home + "touch.png", fixture.ImageDatabase.IconUrl(Feed));
        Assert.True(fixture.ImageDatabase.RecentlyFailed(Home + "declared.png"));
    }

    [Fact]
    public async Task EmptySuccessIsAPersistentImageFailure()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK)));
        Assert.Null(await fixture.Images.FaviconAsync(new(Feed, Home, Home + "empty.png")));
        Assert.True(fixture.ImageDatabase.RecentlyFailed(Home + "empty.png"));
        Assert.Null(await fixture.Images.FaviconAsync(new(Feed, Home, Home + "empty.png")));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task UndecodableSuccessIsLoggedAndRetriedAfterThePersistentFailureInterval()
    {
        int calls = 0;
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK,
            ++calls == 1 ? "<html>not an image</html>"u8.ToArray() : Image)));
        var source = new FeedImageSource(Feed, Home, Home + "bad.png");
        Assert.Null(await fixture.Images.FaviconAsync(source));
        Assert.True(fixture.ImageDatabase.RecentlyFailed(source.FaviconUrl!));
        Assert.False(Directory.Exists(fixture.CacheDirectory));
        var failure = Assert.Single(fixture.Activity.CompletedActivities);
        Assert.Equal(ActivityState.Failed, failure.State);
        Assert.Contains("HTTP 200", Assert.IsType<InvalidDataException>(failure.Error).Message);
        Assert.Null(await fixture.Images.FaviconAsync(source));
        Assert.Equal(1, calls);
        fixture.Clock.Advance(ImageMetadataDatabase.FailureRetry);
        Assert.NotNull(await fixture.Images.FaviconAsync(source));
        Assert.Equal(2, calls);
        Assert.False(fixture.ImageDatabase.RecentlyFailed(source.FaviconUrl!));
    }

    [Fact]
    public async Task ResampledBytesReachMemoryAndDiskWhileActivityCountsTheOriginalDownload()
    {
        var original = Bitmap(192, 96);
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, original)), maxPixelSize: 48);
        var source = new FeedImageSource(Feed, Home, Home + "large.bmp");
        var image = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(source));
        AssertPngSize(image.Data, 48, 24);
        Assert.Equal(image.Data, await File.ReadAllBytesAsync(image.Path));
        Assert.Equal(ActivityLogger.DataSizeMessage(original.LongLength), Assert.Single(fixture.Activity.CompletedActivities).CompletionMessage);
        fixture.Images.EmptyCache();
        var cached = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(source));
        Assert.Equal(image.Data, cached.Data);
        Assert.Single(fixture.Requests);
        Assert.Single(fixture.Activity.CompletedActivities);
    }

    [Fact]
    public async Task OversizedLegacyDiskImagesAreResampledWithoutDownloadingAgain()
    {
        await using var fixture = new Fixture((_, _) => throw new InvalidOperationException("The disk image should be reused."), maxPixelSize: 48);
        string url = Home + "legacy.bmp";
        string path = fixture.LegacyPath(url, ".bmp");
        Directory.CreateDirectory(fixture.LegacyDirectory);
        var original = Bitmap(192, 96);
        await File.WriteAllBytesAsync(path, original);
        var image = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home, url)));
        Assert.Equal(fixture.NativePath(url), image.Path);
        AssertPngSize(image.Data, 48, 24);
        Assert.Equal(image.Data, await File.ReadAllBytesAsync(image.Path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Activity.CompletedActivities);
    }

    [Fact]
    public async Task CorruptDiskImageIsDiscardedAndReplacedByARealImage()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Image)));
        var source = new FeedImageSource(Feed, Home, Home + "cached.png");
        var first = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(source));
        await File.WriteAllBytesAsync(first.Path, "corrupt cached bytes"u8.ToArray());
        fixture.Images.EmptyCache();
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        var repaired = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(source));
        Assert.Equal(Image, repaired.Data);
        Assert.Equal(Image, await File.ReadAllBytesAsync(first.Path));
        Assert.Single(fixture.Requests);
        Assert.False(fixture.ImageDatabase.RecentlyFailed(source.FaviconUrl!));
    }

    [Fact]
    public async Task RasterBytesAtAnSvgUrlDoNotProduceASvgDisplayPath()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Image)));
        var image = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home, Home + "raster.svg")));
        Assert.Equal("", Path.GetExtension(image.Path));
        Assert.Equal(fixture.NativePath(Home + "raster.svg"), image.Path);
        Assert.Equal(Image, image.Data);
    }

    [Theory]
    [InlineData("cached.png", ".png")]
    [InlineData("cached.svg", ".svg")]
    [InlineData("cached.svg", ".image")]
    [InlineData("cached", ".image")]
    public async Task LegacyCacheHitsAreCopiedToNativeFilesWithoutNetworkAccessOrDeletion(string name, string extension)
    {
        await using var fixture = new Fixture((_, _) => throw new InvalidOperationException("No network access is expected."));
        string url = Home + name;
        string legacy = fixture.LegacyPath(url, extension);
        Directory.CreateDirectory(fixture.LegacyDirectory);
        await File.WriteAllBytesAsync(legacy, Image);
        var image = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home, url)));
        Assert.Equal(fixture.NativePath(url), image.Path);
        Assert.Equal(Image, await File.ReadAllBytesAsync(image.Path));
        Assert.Equal(Image, await File.ReadAllBytesAsync(legacy));
        Assert.Empty(fixture.Requests);
        await fixture.ReopenImagesAsync();
        Assert.Equal(Image, (await fixture.Images.FaviconAsync(new(Feed, Home, url)))?.Data);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task NativeCacheEntriesTakePrecedenceOverLegacyFiles()
    {
        await using var fixture = new Fixture((_, _) => throw new InvalidOperationException("No network access is expected."));
        string url = Home + "cached.png";
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.NativePath(url))!);
        Directory.CreateDirectory(fixture.LegacyDirectory);
        await File.WriteAllBytesAsync(fixture.NativePath(url), Image);
        var legacy = Bitmap(16, 16);
        await File.WriteAllBytesAsync(fixture.LegacyPath(url, ".png"), legacy);
        var image = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(new(Feed, Home, url)));
        Assert.Equal(Image, image.Data);
        Assert.Equal(legacy, await File.ReadAllBytesAsync(fixture.LegacyPath(url, ".png")));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task ImageAndFaviconCachesKeepSeparateNativePathsForTheSameUrl()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Image)));
        string url = Home + "shared.png";
        var results = await Task.WhenAll(
            fixture.Images.FaviconAsync(new(Feed, Home, FaviconUrl: url)),
            fixture.Images.IconAsync(new(Feed, Home, IconUrl: url)));
        Assert.Equal(fixture.NativePath(url), results[0]?.Path);
        Assert.Equal(fixture.NativePath(url, favicon: false), results[1]?.Path);
        Assert.Equal(Image, await File.ReadAllBytesAsync(results[0]!.Path));
        Assert.Equal(Image, await File.ReadAllBytesAsync(results[1]!.Path));
        Assert.Single(fixture.Requests);
        await fixture.ReopenImagesAsync();
        Assert.Equal(results[0]!.Path, (await fixture.Images.FaviconAsync(new(Feed, Home, url)))?.Path);
        Assert.Equal(results[1]!.Path, (await fixture.Images.IconAsync(new(Feed, Home, IconUrl: url)))?.Path);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task MigrationWriteFailureKeepsTheLegacyFileAndDoesNotPublishAMapping()
    {
        await using var fixture = new Fixture((_, _) => throw new InvalidOperationException("A cache write failure must not cause a download."));
        string url = Home + "cached.png";
        Directory.CreateDirectory(fixture.LegacyDirectory);
        Directory.CreateDirectory(fixture.CacheDirectory);
        await File.WriteAllBytesAsync(fixture.LegacyPath(url, ".png"), Image);
        await File.WriteAllTextAsync(Path.Combine(fixture.CacheDirectory, "Favicons"), "occupied");
        var error = await Record.ExceptionAsync(() => fixture.Images.FaviconAsync(new(Feed, Home, url)));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(Image, await File.ReadAllBytesAsync(fixture.LegacyPath(url, ".png")));
        Assert.Null(fixture.ImageDatabase.FaviconUrl(Home));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task MemoryHitsRestorePurgedNativeFilesWithoutRedownloading()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Image)));
        var source = new FeedImageSource(Feed, Home, Home + "cached.png");
        var first = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(source));
        Directory.Delete(Path.GetDirectoryName(first.Path)!, recursive: true);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        var restored = Assert.IsType<DownloadedImage>(await fixture.Images.FaviconAsync(source));
        Assert.Equal(first.Path, restored.Path);
        Assert.Equal(Image, await File.ReadAllBytesAsync(restored.Path));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task DisposalDrainsOperationsWaitingForHtmlMetadata()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        });
        var pending = fixture.Images.FaviconAsync(new(Feed, Home));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Images.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(fixture.ImageDatabase.HomePageHasNoFavicon(Home));
        await fixture.Metadata.DisposeAsync();
        Assert.False(await fixture.HtmlDatabase.RecentlyFailedAsync(Home));
    }

    [Fact]
    public async Task ThumbnailSourceKeepsTheLargestDiskRepresentationForNativeDecoding()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native thumbnail decoding requires Windows.");
        var original = Bitmap(1600, 800);
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, original)));
        const string url = Home + "photo.bmp";
        var source = Assert.IsType<DownloadedImage>(await fixture.Thumbnails.SourceAsync(url,
            activityDetail: "Article title"));
        Assert.Equal(Path.Combine(fixture.CacheDirectory, "DownloadedImages", url.Md5Hex()), source.Path);
        Assert.Equal(new byte[] { 0xff, 0xd8 }, source.Data[..2]);
        Assert.Equal((1200u, 600u), WindowsImage.LargestFramePixelSize(source.Data));
        Assert.Equal(source.Data, await File.ReadAllBytesAsync(source.Path));
        Assert.Single(fixture.Requests);
        Assert.False(Directory.Exists(Path.Combine(fixture.CacheDirectory, "Thumbnails")));
        Assert.False(Directory.Exists(Path.Combine(fixture.CacheDirectory, "LargeThumbnails")));
        Assert.All(fixture.Activity.CompletedActivities, activity => Assert.Equal(ActivityOwner.ThumbnailDownloader, activity.Owner));
        Assert.Contains(fixture.Activity.CompletedActivities, activity => activity.Detail == "Article title");
        fixture.NetworkAllowed = false;
        Assert.Equal(source.Data, (await fixture.Thumbnails.SourceAsync(url))?.Data);
        Assert.Single(fixture.Activity.CompletedActivities);
    }

    [Fact]
    public async Task ThumbnailSourcesAreReadFromDiskWithoutRetainingEncodedVariants()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Bitmap(128, 128))));
        const string url = Home + "updated-source.bmp";
        var first = Assert.IsType<DownloadedImage>(await fixture.Thumbnails.SourceAsync(url));
        var replacement = Bitmap(96, 96);
        await File.WriteAllBytesAsync(first.Path, replacement);
        fixture.NetworkAllowed = false;
        Assert.Equal(replacement, (await fixture.Thumbnails.SourceAsync(url))?.Data);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData(32, 32)]
    [InlineData(1000, 64)]
    public async Task NativeDimensionRejectionIsPersistedWithoutSuppressingFeedIcons(int width, int height)
    {
        var original = Bitmap(width, height);
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, original)));
        const string url = Home + "small.bmp";
        Assert.NotNull(await fixture.Thumbnails.SourceAsync(url));
        await fixture.Thumbnails.RejectInsignificantDimensionsAsync(url);
        Assert.True(fixture.ImageDatabase.RecentlyLackedSignificantDimensions(url));
        Assert.False(fixture.ImageDatabase.RecentlyFailed(url));
        Assert.Null(await fixture.Thumbnails.SourceAsync(url));
        Assert.Single(fixture.Requests);
        Assert.NotNull(await fixture.Images.IconAsync(new(Feed, Home, IconUrl: url)));
        Assert.False(fixture.ImageDatabase.RecentlyFailed(url));
    }

    [Fact]
    public async Task IconAndThumbnailConsumersShareOneSourceAndCancelTheirWaitsIndependently()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native image decoding requires Windows.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = Bitmap(1600, 800);
        await using var fixture = new Fixture(async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response(HttpStatusCode.OK, original);
        }, maxPixelSize: 64);
        const string url = Home + "shared-source.bmp";
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.Thumbnails.SourceAsync(url, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var large = fixture.Thumbnails.SourceAsync(url);
        var icon = fixture.Images.IconAsync(new(Feed, Home, IconUrl: url));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        release.TrySetResult();
        var largeImage = Assert.IsType<DownloadedImage>(await large);
        var iconImage = Assert.IsType<DownloadedImage>(await icon);
        Assert.Equal(largeImage.Path, iconImage.Path);
        Assert.Equal((1200u, 600u), WindowsImage.LargestFramePixelSize(largeImage.Data));
        Assert.Equal((64u, 32u), WindowsImage.LargestFramePixelSize(iconImage.Data));
        Assert.Equal(largeImage.Data, await File.ReadAllBytesAsync(largeImage.Path));
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.CacheDirectory, "DownloadedImages")));
        Assert.Single(fixture.Requests);
        Assert.Single(fixture.Activity.CompletedActivities);
    }

    [Fact]
    public async Task SharedDiskSourceDoesNotLeaveASecondCopyInTheHttpResponseCache()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Bitmap(128, 128))));
        const string url = Home + "no-second-cache.bmp";
        Assert.NotNull(await fixture.Thumbnails.SourceAsync(url));
        Assert.False((await fixture.Downloader.DownloadAsync(url)).ReturnedFromCache);
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Fact]
    public async Task CorruptSharedDiskSourceIsRedownloadedRatherThanPermanentlyPoisoningTheUrl()
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Bitmap(128, 128))));
        const string url = Home + "corrupt-source.bmp";
        var first = Assert.IsType<DownloadedImage>(await fixture.Thumbnails.SourceAsync(url));
        await File.WriteAllBytesAsync(first.Path, "not an image"u8.ToArray());
        var repaired = Assert.IsType<DownloadedImage>(await fixture.Thumbnails.SourceAsync(url));
        Assert.Equal(first.Path, repaired.Path);
        Assert.Equal(first.Data, repaired.Data);
        Assert.Equal(repaired.Data, await File.ReadAllBytesAsync(repaired.Path));
        Assert.Equal(2, fixture.Requests.Count);
        Assert.False(fixture.ImageDatabase.RecentlyFailed(url));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task ThumbnailFailureNotificationsFollowPersistedVerdictsAndExcludeTransientErrors(HttpStatusCode status, bool expected)
    {
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(status, Image)));
        const string url = Home + "failed.png";
        string? notified = null;
        fixture.Thumbnails.DownloadFailed += (_, failedUrl) =>
        {
            Assert.True(fixture.ImageDatabase.RecentlyFailed(failedUrl)
                || fixture.ImageDatabase.RecentlyLackedSignificantDimensions(failedUrl));
            notified = failedUrl;
        };
        Assert.Null(await fixture.Thumbnails.SourceAsync(url));
        Assert.Equal(expected ? url : null, notified);
    }

    [Fact]
    public async Task ThumbnailNetworkPolicyDoesNotSuppressDiskHitsOrCreateFailureBackoff()
    {
        var original = Bitmap(128, 128);
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, original)));
        const string url = Home + "photo.bmp";
        fixture.NetworkAllowed = false;
        Assert.Null(await fixture.Thumbnails.SourceAsync(url));
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Activity.CompletedActivities);
        Assert.False(fixture.ImageDatabase.RecentlyFailed(url));
        fixture.NetworkAllowed = true;
        Assert.NotNull(await fixture.Thumbnails.SourceAsync(url));
        fixture.NetworkAllowed = false;
        Assert.NotNull(await fixture.Thumbnails.SourceAsync(url));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task ThumbnailSourcesShareFourNetworkSlotsAndShutdownCancelsQueuedWork()
    {
        var firstFour = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, checks = 0;
        await using var fixture = new Fixture(async (_, ct) =>
        {
            if (Interlocked.Increment(ref running) == 4) firstFour.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable");
        });
        var requests = Enumerable.Range(0, 4)
            .Select(index => fixture.Thumbnails.SourceAsync(Home + index + ".png")).ToList();
        await firstFour.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.NetworkPolicy = () =>
        {
            if (Interlocked.Increment(ref checks) == 2) queued.TrySetResult();
            return true;
        };
        requests.Add(fixture.Thumbnails.SourceAsync(Home + "4.png"));
        requests.Add(fixture.Thumbnails.SourceAsync(Home + "5.png"));
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, Volatile.Read(ref running));
        await fixture.Thumbnails.DisposeAsync();
        foreach (var request in requests)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.False(fixture.ImageDatabase.RecentlyFailed(Home + "4.png"));
        Assert.False(fixture.ImageDatabase.RecentlyFailed(Home + "5.png"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            fixture.Thumbnails.SourceAsync(Home + "after-stop.png"));
    }

    [Fact]
    public async Task CancelingOneThumbnailWaiterDoesNotCancelAnotherWaiter()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response(HttpStatusCode.OK, Bitmap(128, 128));
        });
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.Thumbnails.SourceAsync(Home + "photo.bmp", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retained = fixture.Thumbnails.SourceAsync(Home + "photo.bmp");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.False(retained.IsCompleted);
        release.TrySetResult();
        Assert.NotNull(await retained);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task ResourceMemoryBudgetEvictsTheLeastRecentlyUsedEncodedImage()
    {
        var original = Bitmap(128, 128);
        await using var fixture = new Fixture((_, _) => Task.FromResult(Response(HttpStatusCode.OK, original)));
        await using var resource = new ImageResourceDownloader(fixture.ImageDatabase,
            Path.Combine(fixture.CacheDirectory, "Budgeted"),
            new(512, MemoryCacheByteLimit: original.LongLength * 2,
                NetworkAllowsDownloads: () => fixture.NetworkAllowed),
            ActivityOwner.ThumbnailDownloader, ActivityKind.DownloadThumbnail, fixture.Downloader);
        var first = Assert.IsType<DownloadedImage>((await resource.FetchAsync(Home + "a.bmp", default)).Image);
        var second = Assert.IsType<DownloadedImage>((await resource.FetchAsync(Home + "b.bmp", default)).Image);
        Assert.NotNull((await resource.FetchAsync(first.Url, default)).Image);
        Assert.NotNull((await resource.FetchAsync(Home + "c.bmp", default)).Image);
        File.Delete(first.Path);
        File.Delete(second.Path);
        fixture.NetworkAllowed = false;
        Assert.NotNull((await resource.FetchAsync(first.Url, default)).Image);
        Assert.Null((await resource.FetchAsync(second.Url, default)).Image);
        Assert.Equal(3, fixture.Requests.Count);
    }

    [Fact]
    public void WordPressSizingQueriesAreRemovedOnlyForUploadUrls()
    {
        Assert.Equal("https://site.test/wp-content/uploads/icon.png",
            FeedImageDownloader.SanitizedIconUrl("https://site.test/wp-content/uploads/icon.png?w=32#small"));
        Assert.Equal("https://site.test/icon.png?w=32", FeedImageDownloader.SanitizedIconUrl("https://site.test/icon.png?w=32"));
    }

    private static HttpResponseMessage HtmlResponse(string head) => Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes("<head>" + head + "</head>"));
    private static HttpResponseMessage Response(HttpStatusCode status, byte[]? data = null) => new(status) { Content = new ByteArrayContent(data ?? []) };

    private static void AssertPngSize(byte[] data, int width, int height)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, data[..8]);
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)));
        Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)));
    }

    private static byte[] Bitmap(int width, int height)
    {
        int stride = (width * 3 + 3) / 4 * 4;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4d42);
        writer.Write(54 + stride * height);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width); writer.Write(height);
        writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(stride * height);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) writer.Write(new byte[] { 0, 0, 255 });
            writer.Write(new byte[stride - width * 3]);
        }
        return output.ToArray();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "feed-images-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient _http;
        private readonly Downloader _downloader;
        private readonly int? _maxPixelSize;
        private readonly DownloadedImageCache _downloadedImages;
        public TestClock Clock { get; } = new();
        public ImageMetadataDatabase ImageDatabase { get; }
        public HtmlMetadataDatabase HtmlDatabase { get; }
        public HtmlMetadataDownloader Metadata { get; }
        public FeedImageDownloader Images { get; private set; }
        public ThumbnailDownloader Thumbnails { get; }
        public Downloader Downloader => _downloader;
        public bool NetworkAllowed { get; set; } = true;
        public Func<bool>? NetworkPolicy { get; set; }
        public ActivityLogger Activity { get; }
        public string CacheDirectory => Path.Combine(_directory, "Caches");
        public string LegacyDirectory => Path.Combine(_directory, "Favicons");
        public string NativePath(string url, bool favicon = true) => Path.Combine(CacheDirectory, favicon ? "Favicons" : "DownloadedImages", url.Md5Hex());
        public string LegacyPath(string url, string extension) => Path.Combine(LegacyDirectory, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + extension);
        public ConcurrentQueue<string> Requests { get; } = new();
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, int? maxPixelSize = null)
        {
            ImageDatabase = new(":memory:", Clock);
            HtmlDatabase = new(":memory:", Clock);
            var handler = new Handler(async (request, ct) =>
            {
                Requests.Enqueue(request.RequestUri!.AbsoluteUri);
                return await respond(request, ct);
            });
            _http = new(handler);
            _downloader = new Downloader(_http, Clock);
            _maxPixelSize = maxPixelSize;
            Activity = new ActivityLogger(Clock);
            Metadata = new(HtmlDatabase, _downloader, Clock, Activity);
            _downloadedImages = new(ImageDatabase, CacheDirectory, _downloader, Clock, Activity);
            Images = CreateImages();
            Thumbnails = new(ImageDatabase, CacheDirectory, () => NetworkPolicy?.Invoke() ?? NetworkAllowed,
                _downloader, Clock, Activity, _downloadedImages);
        }
        private FeedImageDownloader CreateImages()
            => new(ImageDatabase, Metadata, CacheDirectory, _downloader, Clock, Activity, _maxPixelSize,
                LegacyDirectory, _downloadedImages);
        public async Task ReopenImagesAsync()
        {
            await Images.DisposeAsync();
            Images = CreateImages();
        }
        public async ValueTask DisposeAsync()
        {
            await Images.DisposeAsync();
            await Thumbnails.DisposeAsync();
            await _downloadedImages.DisposeAsync();
            await Metadata.DisposeAsync();
            ImageDatabase.Dispose();
            HtmlDatabase.Dispose();
            _http.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
