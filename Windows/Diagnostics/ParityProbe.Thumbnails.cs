#if PARITY_PROBE
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsWire.Articles;
using WinNewsWire.AppShared;
using WinNewsWire.Images;
using WinNewsWire.Helpers;
using WinNewsWire.Services;
using WinNewsWire.Web;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckThumbnailsAsync(MainWindow window)
    {
        var imageData = ThumbnailBitmap();
        var orientedData = OrientedThumbnailTiff();
        var smallData = ThumbnailBitmap(48, 48);
        var narrowData = ThumbnailBitmap(1000, 64);
        var framesData = MultiFrameThumbnailTiff();
        int requests = 0, notifications = 0;
        bool initializationOnUI = true, notificationOnUI = true;
        bool networkAllowed = true;
        bool prefetchAllowed = true;
        bool failureOnUI = true;
        int failures = 0;
        var requested = new ConcurrentQueue<string>();
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMissing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new ThumbnailHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref requests);
            requested.Enqueue(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/held.bmp")
            {
                heldStarted.TrySetResult();
                await releaseHeld.Task.WaitAsync(ct);
            }
            if (request.RequestUri.AbsolutePath == "/missing.bmp")
                await releaseMissing.Task.WaitAsync(ct);
            var data = request.RequestUri.AbsolutePath switch
            {
                "/oriented.tiff" => orientedData,
                "/small.bmp" => smallData,
                "/narrow.bmp" => narrowData,
                "/frames.tiff" => framesData,
                _ => imageData
            };
            return new HttpResponseMessage(request.RequestUri.AbsolutePath == "/missing.bmp"
                ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }));
        var downloader = new Downloader(http);
        var clock = new ChangedImageClock();
        using var database = new ImageDatabase(Path.Combine(OutputDirectory, "thumbnail-metadata.db"), clock);
        await using var service = new ArticleThumbnailService(window.DispatcherQueue, () =>
        {
            initializationOnUI = window.DispatcherQueue.HasThreadAccess;
            return (new ArticleThumbnailFinder(database),
                new ThumbnailDownloader(database, Path.Combine(OutputDirectory, "thumbnail-cache"),
                    () => networkAllowed, downloader, clock));
        }, cacheByteLimit: 3_000_000, networkAllowsDownloads: () => networkAllowed,
            networkAllowsPrefetch: () => networkAllowed && prefetchAllowed);
        service.ThumbnailAvailable += (_, _) =>
        {
            notifications++;
            notificationOnUI &= window.DispatcherQueue.HasThreadAccess;
        };
        service.ThumbnailDownloadFailed += (_, _) =>
        {
            failureOnUI &= window.DispatcherQueue.HasThreadAccess;
            failures++;
        };
        var status = new ArticleStatus("thumbnail", false, false, DateTime.UnixEpoch);
        var article = new Article("account", status.ArticleID, "feed", status.ArticleID, "Thumbnail title",
            "<img src='https://thumbnail.test/photo.bmp'>", null, null, "https://thumbnail.test/article",
            null, null, null, null, null, null, null, status);
        var resolvedUrl = await service.ThumbnailUrlAsync(article, ThumbnailMode.Small);
        int resolutions = service.ProbeUrlResolutions;
        var simultaneous = await Task.WhenAll(service.ThumbnailAsync(article, ThumbnailMode.Small, resolvedUrl: resolvedUrl),
            service.ThumbnailAsync(article, ThumbnailMode.Small, resolvedUrl: resolvedUrl));
        var small = simultaneous[0] ?? throw new InvalidOperationException("The thumbnail did not decode.");
        Require(service.ProbeLastDecodedSize == (256, 128), "Small thumbnails were not decoded at display size.");
        Require(ReferenceEquals(small, simultaneous[1]) && service.ProbeDecodes == 1,
            "Concurrent discovery/download/decoding was not coalesced.");
        Require(service.ProbeUrlResolutions == resolutions,
            "The thumbnail service recomputed an already resolved image URL.");
        Require(!initializationOnUI && !service.ProbeDecodedOnUIThread && notificationOnUI && notifications == 1,
            "Thumbnail initialization/decoding or availability notifications ran on the wrong thread.");
        Require(ReferenceEquals(small, await service.ThumbnailAsync(article, ThumbnailMode.Small))
            && service.ProbeDecodes == 1, "A warm thumbnail was decoded again.");
        clock.Advance(TimeSpan.FromDays(1));
        int warmRequests = requests;
        Require(ReferenceEquals(small, await service.ThumbnailAsync(article, ThumbnailMode.Small))
            && service.ProbeDecodes == 1 && requests == warmRequests,
            "Noting a warm thumbnail's use decoded or downloaded it again.");
        Require(((await database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).Single().LastAccessed
            - clock.GetUtcNow()).Duration() < TimeSpan.FromMilliseconds(1),
            "A warm decoded thumbnail did not refresh its source access date.");

        var large = await service.ThumbnailAsync(article, ThumbnailMode.Large);
        Require(service.ProbeLastDecodedSize == (1200, 600), "Large thumbnails were not decoded at display size.");
        Require(large is not null && !ReferenceEquals(small, large) && service.ProbeDecodes == 2,
            "Small and large decoded thumbnails shared a cache entry.");
        Require(service.ProbeCacheBytes == 1200L * 600 * 4,
            "The decoded-image budget did not evict the older small thumbnail.");
        var reloaded = await service.ThumbnailAsync(article, ThumbnailMode.Small);
        Require(reloaded is not null && !ReferenceEquals(small, reloaded) && service.ProbeDecodes == 3
            && service.ProbeCacheBytes == 256L * 128 * 4,
            "Decoded thumbnail LRU eviction or byte accounting failed.");

        Article FixtureFor(string filename) => new("account", filename, "feed", filename, "Decoder fixture",
            $"<img src='https://thumbnail.test/{filename}'>", null, null, "https://thumbnail.test/article",
            null, null, null, null, null, null, null, new ArticleStatus(filename, false, false, DateTime.UnixEpoch));
        var oriented = await service.ThumbnailAsync(FixtureFor("oriented.tiff"), ThumbnailMode.Small);
        Require(oriented is not null && service.ProbeLastDecodedSize == (128, 256),
            "The native thumbnail decoder ignored EXIF orientation.");
        Require(await service.ThumbnailAsync(FixtureFor("frames.tiff"), ThumbnailMode.Small) is not null
            && service.ProbeLastDecodedSize == (256, 128),
            "The native decoder must select the first largest-dimension frame, not the first frame or largest area.");
        foreach (var filename in new[] { "small.bmp", "narrow.bmp" })
        {
            string url = "https://thumbnail.test/" + filename;
            Require(await service.ThumbnailAsync(FixtureFor(filename), ThumbnailMode.Small) is null
                && database.IsUnsuitableForThumbnail(url) && !database.RecentlyFailed(url),
                "The native decoder did not persist a thumbnail-only dimension rejection.");
        }
        await WaitFor(() => Task.FromResult(failures == 2), "Native dimension rejection notifications");
        var unreadable = FixtureFor("decoder-failure.bmp");
        service.ProbeDecodeError = unchecked((int)0x88982f50);
        Require(await service.ThumbnailAsync(unreadable, ThumbnailMode.Small) is null
            && !database.IsUnsuitableForThumbnail("https://thumbnail.test/decoder-failure.bmp")
            && !database.RecentlyFailed("https://thumbnail.test/decoder-failure.bmp"),
            "A native decoder failure was not isolated from otherwise usable source images.");
        int afterFailedDecode = service.ProbeDecodes;
        Require(await service.ThumbnailAsync(unreadable, ThumbnailMode.Small) is null
            && service.ProbeDecodes == afterFailedDecode,
            "The same unreadable thumbnail was decoded again after its failure notification.");
        await WaitFor(() => Task.FromResult(failures == 3), "Native decoding failure notification");

        var host = Descendants(window.ProbeContent).OfType<Grid>().First();
        var image = new ThumbnailView { Width = 80, Height = 80, CornerRadius = new CornerRadius(12) };
        Grid.SetRowSpan(image, Math.Max(1, host.RowDefinitions.Count));
        host.Children.Add(image);
        try
        {
            await WaitFor(() => Task.FromResult(image.IsLoaded && image.ActualWidth == 80), "Native thumbnail layout");
            image.RequestedTheme = ElementTheme.Light;
            await WaitFor(() => Task.FromResult(image.ActualTheme == ElementTheme.Light), "Light thumbnail placeholder");
            var raster = new RenderTargetBitmap();
            await raster.RenderAsync(image, 80, 80);
            var light = (await raster.GetPixelsAsync()).ToArray();
            image.RequestedTheme = ElementTheme.Dark;
            await WaitFor(() => Task.FromResult(image.ActualTheme == ElementTheme.Dark), "Dark thumbnail placeholder");
            await raster.RenderAsync(image, 80, 80);
            var dark = (await raster.GetPixelsAsync()).ToArray();
            int center = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth / 2) * 4;
            Require(!light.AsSpan(center, 4).SequenceEqual(dark.AsSpan(center, 4)),
                "The empty thumbnail placeholder did not follow its actual light/dark theme.");
            image.ImageSource = reloaded;
            await raster.RenderAsync(image, 80, 80);
            var pixels = (await raster.GetPixelsAsync()).ToArray();
            int offset = (raster.PixelHeight / 8 * raster.PixelWidth + raster.PixelWidth / 2) * 4;
            Require(pixels.AsSpan(offset, 4).SequenceEqual(dark.AsSpan(offset, 4))
                && pixels[center] < 50 && pixels[center + 1] < 50 && pixels[center + 2] > 200 && pixels[center + 3] == 255,
                "The default thumbnail presentation did not fit the whole image on its fill.");
            Require(pixels[3] == 0, "The thumbnail image was not clipped to its rounded corner.");
            image.ImageSource = null;
            await raster.RenderAsync(image, 80, 80);
            var reset = (await raster.GetPixelsAsync()).ToArray();
            Require(reset.AsSpan(center, 4).SequenceEqual(dark.AsSpan(center, 4)),
                "Recycling the thumbnail view did not restore its placeholder.");

            image.ImageSource = ThumbnailBands(portrait: true);
            image.FillBrush = new SolidColorBrush(Microsoft.UI.Colors.Magenta);
            await raster.RenderAsync(image, 80, 80);
            var fullPortrait = (await raster.GetPixelsAsync()).ToArray();
            int side = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth / 8) * 4;
            int bottom = (raster.PixelHeight * 13 / 16 * raster.PixelWidth + raster.PixelWidth / 2) * 4;
            Require(fullPortrait[side] > 200 && fullPortrait[side + 1] < 50 && fullPortrait[side + 2] > 200
                && fullPortrait[offset + 2] > 200 && fullPortrait[offset] < 50
                && fullPortrait[bottom] > 200 && fullPortrait[bottom + 2] < 50,
                "Fit mode lost the custom fill or cropped the portrait's top/bottom.");
            image.ImageSource = ThumbnailBands(portrait: false);
            await raster.RenderAsync(image, 80, 80);
            var wide = (await raster.GetPixelsAsync()).ToArray();
            int wideLeft = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth / 16) * 4;
            int wideRight = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth * 15 / 16) * 4;
            Require(wide[center + 1] > 200 && wide[center] < 50 && wide[center + 2] < 50
                && wide[wideLeft + 2] > 200 && wide[wideLeft] < 50
                && wide[wideRight] > 200 && wide[wideRight + 2] < 50,
                "The thumbnail cropped the wide image's left or right edge.");
            image.Width = 160;
            await WaitFor(() => Task.FromResult(image.ActualWidth == 160), "Resized thumbnail layout");
            await raster.RenderAsync(image, 160, 80);
            var resized = (await raster.GetPixelsAsync()).ToArray();
            int left = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth / 16) * 4;
            int right = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth * 15 / 16) * 4;
            Require(resized[left + 2] > 200 && resized[left] < 50
                && resized[right] > 200 && resized[right + 2] < 50 && resized[3] == 0,
                "Resizing did not preserve the whole image and rounded clipping.");

            image.ImageSource = oriented;
            await raster.RenderAsync(image, 160, 80);
            var rotated = (await raster.GetPixelsAsync()).ToArray();
            int topHalf = (raster.PixelHeight / 4 * raster.PixelWidth + raster.PixelWidth / 2) * 4;
            int bottomHalf = (raster.PixelHeight * 3 / 4 * raster.PixelWidth + raster.PixelWidth / 2) * 4;
            Require(rotated[topHalf + 2] > 200 && rotated[topHalf] < 50
                && rotated[bottomHalf] > 200 && rotated[bottomHalf + 2] < 50,
                "EXIF-oriented native thumbnail pixels were not rotated from left/right to top/bottom.");
        }
        finally { host.Children.Remove(image); }

        int beforeRestrictedPrefetch = requests;
        int beforeRestrictedDiscovery = service.ProbeUrlResolutions;
        prefetchAllowed = false;
        var restrictedPrefetch = FixtureFor("restricted-prefetch.bmp");
        await service.PrefetchThumbnailAsync(restrictedPrefetch, ThumbnailMode.Small);
        Require(requests == beforeRestrictedPrefetch && service.ProbeUrlResolutions == beforeRestrictedDiscovery,
            "Restricted prefetch still started image discovery or downloading.");
        Require(await service.ThumbnailAsync(restrictedPrefetch, ThumbnailMode.Small) is not null
            && requests == beforeRestrictedPrefetch + 1,
            "Prefetch restrictions must not suppress a visible image.");
        prefetchAllowed = true;
        await service.PrefetchThumbnailAsync(FixtureFor("allowed-prefetch.bmp"), ThumbnailMode.Small);
        Require(requests == beforeRestrictedPrefetch + 2, "Allowed prefetch did not resume.");
        int downloaded = requests;
        service.EmptyCache();
        Require(service.ProbeCacheBytes == 0, "Low-memory cleanup retained decoded thumbnails.");
        networkAllowed = false;
        Require(await service.ThumbnailAsync(article, ThumbnailMode.Small) is not null && requests == downloaded,
            "Offline thumbnail display did not reuse disk data.");
        networkAllowed = true;
        await CheckTimelineThumbnailsAsync(window, service, requested, heldStarted, releaseHeld, releaseMissing,
            allowed => networkAllowed = allowed);
        var account = AppRuntime.AppService.Shared.Accounts.Accounts.First();
        var baseFeed = account.AddFeed("https://thumbnail.test/base/rss/feed.xml", "Thumbnail base fixture");
        Article WithoutLink(string id) => new(account.AccountID, id, baseFeed.FeedID, id, "Relative image fixture",
            "<img src='relative.bmp'>", null, null, null, "https://external.test/unrelated", null, null,
            null, null, null, null, new ArticleStatus(id, false, false, DateTime.UnixEpoch));
        try
        {
            baseFeed.HomePageUrl = "https://thumbnail.test/base/home/";
            var homePageLookup = service.ThumbnailUrlAsync(WithoutLink("homepage-base"), ThumbnailMode.Small);
            baseFeed.HomePageUrl = null;
            Require(await homePageLookup == "https://thumbnail.test/base/home/relative.bmp",
                "Thumbnail discovery did not snapshot feed homepage metadata on the UI thread.");
            var feedArticle = WithoutLink("feed-base");
            Require(await service.ThumbnailUrlAsync(feedArticle, ThumbnailMode.Small) == "https://thumbnail.test/base/rss/relative.bmp"
                && await service.ThumbnailAsync(feedArticle, ThumbnailMode.Small) is not null,
                "Thumbnail discovery did not resolve and display an image relative to the feed URL.");
        }
        finally { account.RemoveFeed(baseFeed); }
        Require(failureOnUI && failures == 4, "Permanent thumbnail failures were not dispatched once to the UI.");
        await CheckChangedFeedIconsAsync(window);
        await service.DisposeAsync();
        Require(service.ProbeCacheBytes == 0, "Shutdown retained the decoded thumbnail cache.");
        bool stopped = false;
        try { await service.ThumbnailAsync(article, ThumbnailMode.Small); }
        catch (ObjectDisposedException) { stopped = true; }
        Require(stopped, "A disposed thumbnail service accepted another request.");
        return new { InitializationOffUI = true, DecodingOffUI = true, NotificationsOnUI = true,
            CoalescedDiscoveryAndDecode = true, SeparateModes = true, DecodedLRUBudget = true,
            DisplaySizeDecode = true, ExifOrientation = true, NativeDimensionRejection = true,
            LargestFrameDecode = true,
            ChangedFeedIconsReloadNativeAndReaderImages = true,
            ReaderUsesOriginalSharedImage = true,
            RenderedImagesUseSharedSources = true, FailedLocalImagesFallBackWithoutPublisherScripts = true,
            HiddenModeLeavesReaderImagesRemote = true,
            ReaderAndDiscoveryShareBaseUrls = true, FeedBasesCapturedOnUI = true,
            ConnectivityRecoveryRetriesVisiblePlaceholders = true,
            RestrictedPrefetchPreservesVisibleDownloads = true,
            DecoderFailureStopsRetriesWithoutSuppressingIcons = true,
            VisibleNativePixels = true, RoundedClipping = true, ThemedPlaceholder = true,
            RecycledPlaceholder = true, LowMemoryCleanup = true, OfflineDiskReuse = true, Shutdown = true,
            TimelineRows = true, HTMLDiscovery = true, FailedCandidateFallback = true, FailureNotificationsOnUI = true, BoundedLookahead = true,
            RecycledRowRejectsLateImage = true, ColumnLayoutExcludesThumbnails = true,
            LargeRows = true, LargeModePersists = true, SharedModeNotificationsOnUI = true,
            LargeMenuState = true, FixedHeightLargeRows = true, ImagelessRowsStayCompact = true,
            ModeChangesPreserveSelection = true, FailureRefreshTargetsAffectedRows = true,
            WholeImageFit = true, CustomLetterboxFill = true, PortraitAndWideEdgesPreserved = true,
            FitUpdatesOnResize = true, LargeImageAlignsWithIconOrText = true, NativeScrollAnchoring = true,
            HiddenModeStopsNewRequests = true, MenuAndPreferencesShareMode = true,
            SharedAvailabilityFillsOfflinePlaceholder = true,
            FailedPreferenceWritesRestoreSelectionAndReport = true,
            FeedIconsAlwaysNeverAutomatic = true, FeedIconsColumnException = true,
            FeedIconsSharedSettingsOnUI = true, FeedIconsFailedWriteRecovery = true };
    }

    private static async Task CheckChangedFeedIconsAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var previousItems = model.ArticleItems;
        var previousSelection = model.SelectedArticle;
        var previousImages = model.ProbeFeedImages;
        var previousIcons = Core.AppDefaults.Shared.GetTimelineFeedIconsDisplay();
        bool previousColumns = Core.AppDefaults.Shared.UseColumnLayout;
        var clock = new ChangedImageClock();
        int requests = 0;
        using var http = new HttpClient(new ThumbnailHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Interlocked.Increment(ref requests) == 1
                    ? ThumbnailBitmap(128, 128) : ThumbnailBitmap(1600, 800))
            };
            response.Headers.TryAddWithoutValidation("ETag", "\"changed-icon\"");
            return Task.FromResult(response);
        }));
        var downloader = new Downloader(http, clock);
        using var database = new ImageDatabase(Path.Combine(OutputDirectory, "changed-icon.db"), clock);
        using var htmlDatabase = new HTMLMetadata.HtmlMetadataDatabase(":memory:", clock);
        await using var metadata = new HTMLMetadata.HtmlMetadataDownloader(htmlDatabase, downloader, clock);
        await using var images = new FeedImageDownloader(database, metadata, Path.Combine(OutputDirectory, "changed-icons"),
            downloader, clock, maxPixelSize: 48, networkAllowsUpdates: () => true);
        const string url = "https://thumbnail.test/changed-icon.bmp";
        var feed = new FeedImageSource("https://thumbnail.test/changed-icon-feed", IconUrl: url, FaviconUrl: url);
        var original = await images.IconAsync(feed) ?? throw new InvalidOperationException("The original icon did not load.");
        var article = new Article("account", "changed-icon", "feed", "changed-icon", "Changed icon fixture",
            "<p id='changed-icon-body'>Original document</p>", null, null, "https://thumbnail.test/changed-icon-article",
            null, null, null, null, null, null, null, new ArticleStatus("changed-icon", true, false, DateTime.UnixEpoch));
        var row = new Models.FeedItem
        {
            Id = article.ArticleID, AccountID = article.AccountID, SourceArticle = article,
            FeedTitle = "Changed icon fixture", FaviconPath = original.Path, IsRead = true
        };
        var unrelated = new Models.FeedItem { Id = "unrelated-icon" };
        var sidebarRow = new Models.SidebarItem { Title = "Changed icon fixture", FaviconPath = original.Path };
        var section = model.SidebarItems.First();
        bool onUI = true;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Models.FeedItem.FaviconSource))
                onUI &= window.DispatcherQueue.HasThreadAccess;
        };
        var list = (ListView)content.FindName("ArticleListView");
        try
        {
            Core.AppDefaults.Shared.UseColumnLayout = false;
            Core.AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Always);
            model.ProbeFeedImages = images;
            model.SelectedArticle = null;
            model.ArticleItems = new ObservableCollection<Models.FeedItem> { row, unrelated };
            section.Children.Add(sidebarRow);
            list.SelectedItem = row;
            await WaitFor(() => Task.FromResult(list.ContainerFromItem(row) is not null), "Original icon row");
            var icon = Descendants((DependencyObject)list.ContainerFromItem(row)).OfType<IconView>().Single();
            await WaitFor(() => Task.FromResult(icon.ImageSource is BitmapImage { PixelWidth: 128 }),
                "Original native icon");
            await WaitFor(async () => await ScriptBool(content,
                "window.__wnwArticleProcessed === true && document.getElementById('nnwImageIcon')?.naturalWidth === 128"),
                "Original reader icon");
            await content.ProbeWebView.CoreWebView2.ExecuteScriptAsync("window.changedIconSentinel = true;");
            string document = model.ArticleHtml;
            clock.Advance(TimeSpan.FromDays(10));
            Require(await images.FaviconAsync(feed) is not null, "The icon source was not rechecked.");
            await WaitFor(() => Task.FromResult(icon.ImageSource is BitmapImage { PixelWidth: 1600, PixelHeight: 800 }),
                "Replacement native icon at the unchanged file path");
            await WaitFor(async () => await ScriptBool(content,
                "window.changedIconSentinel === true && document.getElementById('nnwImageIcon')?.naturalWidth === 1600"),
                "Replacement reader icon without document navigation");
            Require(onUI && row.FaviconPath == original.Path && row.FaviconSource.Revision > 0
                && sidebarRow.FaviconSource.Revision > 0 && unrelated.FaviconSource.Revision == 0
                && model.ArticleHtml == document && requests == 2,
                "Changed icons must refresh matching rows on UI without changing paths, unrelated rows, or the document.");
        }
        finally
        {
            model.ProbeFeedImages = previousImages;
            section.Children.Remove(sidebarRow);
            model.ArticleItems = previousItems;
            model.SelectedArticle = previousSelection;
            Core.AppDefaults.Shared.UseColumnLayout = previousColumns;
            Core.AppDefaults.Shared.SetTimelineFeedIconsDisplay(previousIcons);
        }
    }

    private sealed class ChangedImageClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan amount) => _now += amount;
    }

    private static async Task CheckTimelineThumbnailsAsync(MainWindow window, ArticleThumbnailService service,
        ConcurrentQueue<string> requests, TaskCompletionSource heldStarted, TaskCompletionSource releaseHeld,
        TaskCompletionSource releaseMissing, Action<bool> setNetworkAllowed)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var previousItems = model.ArticleItems;
        var previousSelection = model.SelectedArticle;
        bool previousColumns = Core.AppDefaults.Shared.UseColumnLayout;
        bool previousScripts = Core.AppDefaults.Shared.ArticleContentJavascriptEnabled;
        var previousDisplay = Core.AppDefaults.Shared.GetTimelineThumbnailsDisplay();
        var previousFeedIcons = Core.AppDefaults.Shared.GetTimelineFeedIconsDisplay();
        bool previousUnified = model.IsUnifiedLayout;
        var list = (ListView)content.FindName("ArticleListView");
        Article ArticleFor(int index, string? image, string? html = null) =>
            new("account", "timeline-thumbnail-" + index, "feed", index.ToString(), "Thumbnail fixture " + index,
                html, null, null, "https://thumbnail.test/article/" + index, null, "A short preview.", image,
                null, null, null, null, new ArticleStatus("timeline-thumbnail-" + index, true, false, DateTime.UnixEpoch));
        Models.FeedItem Row(Article article) => new()
        {
            Id = article.ArticleID, AccountID = article.AccountID, SourceArticle = article,
            FeedTitle = "Thumbnail fixtures", IsRead = true
        };
        var articles = Enumerable.Range(0, 80).Select(index => ArticleFor(index,
            index == 0 ? "https://thumbnail.test/held.bmp" : index is 1 or 3 ? null
                : index == 2 ? "https://thumbnail.test/missing.bmp" : $"https://thumbnail.test/timeline-photo-{index}.bmp",
            index is 1 or 2 ? $"<img src='https://thumbnail.test/timeline-photo-{index}.bmp'>" : null)).ToArray();
        var rows = new ObservableCollection<Models.FeedItem>(articles.Select(Row));
        try
        {
            Core.AppDefaults.Shared.UseColumnLayout = false;
            Core.AppDefaults.Shared.ArticleContentJavascriptEnabled = false;
            Core.AppDefaults.Shared.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.Small);
            Core.AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Always);
            model.IsUnifiedLayout = false;
            model.SelectedArticle = null;
            content.ProbeSetThumbnailService(service);
            model.ArticleItems = rows;
            list.ScrollIntoView(rows[0], ScrollIntoViewAlignment.Leading);
            await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>()
                .Any(view => view.Tag is Article { ArticleID: "timeline-thumbnail-1" } && view.ImageSource is not null)),
                "HTML-only timeline thumbnail");
            var heldView = Descendants(list).OfType<ThumbnailView>().Single(view =>
                view.Tag is Article { ArticleID: "timeline-thumbnail-0" });
            var heldRequest = content.ProbeThumbnailRequestToken(heldView);
            releaseMissing.TrySetResult();
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>()
                .Any(view => view.Tag is Article { ArticleID: "timeline-thumbnail-2" } && view.ImageSource is not null)),
                "Failed provided thumbnail fallback");
            Require(!heldRequest.IsCancellationRequested,
                "A failed thumbnail canceled an unrelated visible row's pending request.");
            var panel = (ItemsStackPanel)list.ItemsPanelRoot;
            int visibleCount = panel.LastVisibleIndex - panel.FirstVisibleIndex + 1;
            int lookaheadEnd = panel.LastVisibleIndex + 1 + visibleCount;
            await WaitFor(() => Task.FromResult(requests.Any(path => path == $"/timeline-photo-{panel.LastVisibleIndex + 1}.bmp")),
                "Next-screen thumbnail prefetch");
            Require(service.ProbeDownloadPriorities.TryGetValue(
                $"https://thumbnail.test/timeline-photo-{panel.LastVisibleIndex + 1}.bmp", out var priority)
                && priority == DownloadPriority.Prefetch,
                "Next-screen timeline requests did not use the low-priority thumbnail path.");
            var fetched = requests.Where(path => path.StartsWith("/timeline-photo-", StringComparison.Ordinal))
                .Select(path => int.Parse(path["/timeline-photo-".Length..^4])).ToArray();
            Require(fetched.All(index => index < lookaheadEnd) && fetched.Length < rows.Count,
                "Thumbnail prefetch downloaded beyond the next screenful.");

            Require(heldView.Visibility == Visibility.Visible && heldView.ImageSource is null
                && heldView.Width > 0 && heldView.Width <= 76,
                "A pending timeline thumbnail did not reserve a bounded square placeholder.");
            var changed = ArticleFor(0, "https://thumbnail.test/replaced.bmp");
            rows[0].UpdateFrom(Row(changed));
            var replacement = await service.ThumbnailAsync(changed, ThumbnailMode.Small);
            await WaitFor(() => Task.FromResult(ReferenceEquals(heldView.ImageSource, replacement)),
                "Updated article thumbnail binding");
            releaseHeld.TrySetResult();
            await service.ThumbnailAsync(articles[0], ThumbnailMode.Small);
            Require(ReferenceEquals(heldView.ImageSource, replacement),
                "A late image from a recycled article replaced its current thumbnail.");

            Core.AppDefaults.Shared.UseColumnLayout = true;
            await WaitFor(() => Task.FromResult(!Descendants(list).OfType<ThumbnailView>().Any()),
                "Thumbnail-free column template");
            int beforeColumns = requests.Count;
            list.ScrollIntoView(rows[60], ScrollIntoViewAlignment.Leading);
            list.UpdateLayout();
            await Task.Delay(150);
            Require(requests.Count == beforeColumns, "Column layout started thumbnail prefetch.");
            Core.AppDefaults.Shared.UseColumnLayout = false;
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any()),
                "Restored standard thumbnail template");
            list.ScrollIntoView(rows[60], ScrollIntoViewAlignment.Leading);
            try
            {
                await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any(view =>
                    view.Tag is Article { ArticleID: "timeline-thumbnail-60" } && view.ImageSource is not null)),
                    "Virtualized thumbnail after scrolling");
            }
            catch
            {
                File.WriteAllText(Path.Combine(OutputDirectory, "thumbnail-timeline-state.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { State = content.ProbeTimelineThumbnailState,
                        Requests = requests.ToArray() }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                throw;
            }

            var thumbnailsMenu = Descendants(window.Content).OfType<MenuBar>().SelectMany(bar => bar.Items)
                .OfType<MenuBarItem>().SelectMany(item => item.Items).OfType<MenuFlyoutSubItem>()
                .Single(item => item.Name == "MenuThumbnails");
            var modeItems = thumbnailsMenu.Items.OfType<ToggleMenuFlyoutItem>().ToArray();
            var largeMenu = modeItems.Single(item => item.Tag is TimelineThumbnailsDisplay.Large);
            using var peer = new ViewModels.MainViewModel();
            int modeNotifications = 0;
            int feedIconNotifications = 0;
            bool modeNotificationsOnUI = true;
            peer.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(peer.TimelineFeedIconsDisplay))
                {
                    feedIconNotifications++;
                    modeNotificationsOnUI &= window.DispatcherQueue.HasThreadAccess;
                    return;
                }
                if (args.PropertyName != nameof(peer.TimelineThumbnailsDisplay)) return;
                modeNotifications++;
                modeNotificationsOnUI &= window.DispatcherQueue.HasThreadAccess;
            };
            model.SelectedArticle = rows[0];
            list.ScrollIntoView(rows[0], ScrollIntoViewAlignment.Leading);
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>()
                .Any(view => view.Tag is Article { ArticleID: "timeline-thumbnail-0" } && view.ImageSource is not null)),
                "Small row before mode change");
            var originalSmall = Descendants(list).OfType<ThumbnailView>().Single(view =>
                view.Tag is Article { ArticleID: "timeline-thumbnail-0" }).ImageSource;
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Article ready for shared image resource");
            string selectedImageUrl = AppShared.ArticleRendering.ArticleImageResource.Url("https://thumbnail.test/replaced.bmp");
            try
            {
                await WaitFor(() => ScriptBool(content, "(() => { const image = document.querySelector('img[data-nnw-src]'); return image?.src === "
                    + System.Text.Json.JsonSerializer.Serialize(selectedImageUrl) + " && image.naturalWidth === 1600; })()"),
                    "Rendered article routes its timeline image to the original shared source");
            }
            catch
            {
                File.WriteAllText(Path.Combine(OutputDirectory, "reader-image-state.json"),
                    await content.ProbeWebView.ExecuteScriptAsync("Array.from(document.images, image => ({src:image.src,"
                        + "original:image.getAttribute('data-nnw-src'),width:image.naturalWidth,complete:image.complete}))"));
                throw;
            }
            Require(await ScriptBool(content, """
                (() => {
                    const source = document.querySelector("img[data-nnw-src]");
                    const fixture = document.createElement("div");
                    fixture.innerHTML = "<p>Selected <a href='copy-probe'>link</a></p>";
                    fixture.firstChild.append(source.cloneNode());
                    document.body.append(fixture);
                    const selection = window.getSelection();
                    const range = document.createRange();
                    range.selectNodeContents(fixture);
                    selection.removeAllRanges();
                    selection.addRange(range);
                    try {
                        const html = window.selectedHTML();
                        const copy = document.createElement("template");
                        copy.innerHTML = html;
                        const image = copy.content.querySelector("img");
                        return image?.getAttribute("src") === source.getAttribute("data-nnw-src")
                            && !image.hasAttribute("data-nnw-src")
                            && copy.content.querySelector("a")?.getAttribute("href") === new URL("copy-probe", document.baseURI).href
                            && !html.includes("<p>")
                            && source.hasAttribute("data-nnw-src");
                    } finally {
                        selection.removeAllRanges();
                        fixture.remove();
                    }
                })()
                """), "Copied reader HTML must contain portable original images and absolute links without changing the displayed image.");
            string readerImageUrl = AppShared.ArticleRendering.ArticleImageResource.Url("https://thumbnail.test/photo.bmp");
            int previousImageRequests = requests.Count(path => path == "/photo.bmp");
            setNetworkAllowed(false);
            try
            {
                await content.ProbeWebView.ExecuteScriptAsync("(() => { const image = new Image(); image.id = 'shared-source-probe';"
                    + "image.src = " + System.Text.Json.JsonSerializer.Serialize(readerImageUrl) + "; document.body.append(image); })()");
                await WaitFor(() => ScriptBool(content,
                    "document.getElementById('shared-source-probe')?.naturalWidth === 1600"
                    + " && document.getElementById('shared-source-probe')?.naturalHeight === 800"),
                    "Original full-size image served from the shared offline source");
                Require(requests.Count(path => path == "/photo.bmp") == previousImageRequests,
                    "The article reader downloaded its cached timeline image again.");
            }
            finally
            {
                setNetworkAllowed(true);
                await content.ProbeWebView.ExecuteScriptAsync("document.getElementById('shared-source-probe')?.remove()");
            }
            const string forbiddenUrl = "https://thumbnail.test/forged.bmp";
            string unsigned = AppShared.ArticleRendering.ArticleImageResource.BaseUrl + "?u=" + Uri.EscapeDataString(forbiddenUrl);
            string forged = readerImageUrl.Replace(Uri.EscapeDataString("https://thumbnail.test/photo.bmp"),
                Uri.EscapeDataString(forbiddenUrl), StringComparison.Ordinal);
            await content.ProbeWebView.ExecuteScriptAsync("(() => { for (const [id, url] of "
                + System.Text.Json.JsonSerializer.Serialize(new[] { new[] { "unsigned-image", unsigned }, new[] { "forged-image", forged } })
                + ") { const image = new Image(); image.id = id; image.src = url; document.body.append(image); } })()");
            try
            {
                await WaitFor(() => ScriptBool(content,
                    "['unsigned-image', 'forged-image'].every(id => { const image = document.getElementById(id);"
                    + " return image?.complete && image.naturalWidth === 0; })"),
                    "Unsigned and forged shared-image requests are rejected");
                Require(!requests.Contains("/forged.bmp"), "Untrusted reader markup started a shared-cache download.");
            }
            finally
            {
                await content.ProbeWebView.ExecuteScriptAsync(
                    "['unsigned-image', 'forged-image'].forEach(id => document.getElementById(id)?.remove())");
            }
            await using (var fallbackServer = new FixtureServer())
            {
                string originalHtml = model.ArticleHtml;
                string fallbackUrl = fallbackServer.Url + "/icon.png";
                string unavailable = AppShared.ArticleRendering.ArticleImageResource.Url("https://thumbnail.test/missing.bmp");
                model.ArticleHtml = originalHtml.Replace("</body>",
                    "<img id='shared-fallback-probe' src='" + unavailable + "' data-nnw-src='" + fallbackUrl + "'>"
                    + "<script>window.__imagePublisherScriptRan = true;</script></body>", StringComparison.Ordinal);
                try
                {
                    await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Reader with an unavailable local image");
                    await WaitFor(() => ScriptBool(content,
                        "document.getElementById('shared-fallback-probe')?.naturalWidth === 1"
                        + " && !document.getElementById('shared-fallback-probe').hasAttribute('data-nnw-src')"),
                        "Unavailable shared image falls back to its original remote source");
                    Require(!content.ProbeWebView.CoreWebView2.Settings.IsScriptEnabled
                        && !await ScriptBool(content, "window.__imagePublisherScriptRan === true"),
                        "Image fallback enabled publisher scripts.");
                    await content.ProbeWebView.ExecuteScriptAsync("document.getElementById('shared-fallback-probe').dispatchEvent(new Event('error'))");
                    Require(await ScriptBool(content, "document.getElementById('shared-fallback-probe').src === "
                        + System.Text.Json.JsonSerializer.Serialize(fallbackUrl)),
                        "A second image error restarted fallback after its marker was removed.");
                }
                catch
                {
                    File.WriteAllText(Path.Combine(OutputDirectory, "reader-fallback-state.json"),
                        await content.ProbeWebView.ExecuteScriptAsync("({images:Array.from(document.images, image => ({src:image.src,"
                            + "original:image.getAttribute('data-nnw-src'),width:image.naturalWidth,complete:image.complete})),"
                            + "processed:window.__wnwArticleProcessed,html:document.body.innerHTML.slice(-4000)})"));
                    throw;
                }
                finally { model.RefreshArticleHtml(); }
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Reader restored after fallback check");
            }
            setNetworkAllowed(false);
            await Task.Run(service.ProbeNotifyNetworkStatusChanged);
            await WaitFor(() => Task.FromResult(!service.NetworkDownloadsAllowed), "Offline policy notification on UI");
            var networkRecoveryArticle = ArticleFor(2, "https://thumbnail.test/recovered.bmp");
            rows[2] = Row(networkRecoveryArticle);
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>()
                .Any(view => ReferenceEquals(view.Tag, networkRecoveryArticle) && view.ImageSource is null
                    && !content.ProbeThumbnailRequestIsLoading(view))),
                "Visible offline thumbnail placeholder");
            await Task.Delay(250);
            await new RenderTargetBitmap().RenderAsync(list);
            await WaitFor(() => Task.FromResult(content.ProbeThumbnailWorkIsIdle),
                "Offline layout and admission settle before connectivity recovery");
            Require(!requests.Contains("/recovered.bmp"), "An offline placeholder initiated a network request.");
            setNetworkAllowed(true);
            await Task.Run(service.ProbeNotifyNetworkStatusChanged);
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>()
                .Any(view => ReferenceEquals(view.Tag, networkRecoveryArticle) && view.ImageSource is not null)),
                "Connectivity recovery fills an idle visible thumbnail");
            Require(requests.Count(path => path == "/recovered.bmp") == 1,
                "Connectivity recovery duplicated the visible image download.");
            await Task.Run(() => Core.AppDefaults.Shared.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.Large));
            await WaitFor(() => Task.FromResult(modeNotifications > 0 && peer.TimelineThumbnailsDisplay == TimelineThumbnailsDisplay.Large
                && largeMenu.IsChecked && largeMenu.IsEnabled), "Shared large-thumbnail mode");
            Require(modeNotificationsOnUI && new Core.AppDefaults().GetTimelineThumbnailsDisplay() == TimelineThumbnailsDisplay.Large
                && modeItems.Count(item => item.IsChecked) == 1,
                "Large-thumbnail mode was not persisted or a peer notification ran off the UI thread.");
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any(view =>
                view.Tag is Article { ArticleID: "timeline-thumbnail-0" } && Grid.GetRow(view) == 1
                && Grid.GetColumn(view) == 1 && Grid.GetColumnSpan(view) == 3 && view.ActualHeight == 180
                && view.ImageSource is not null
                && !ReferenceEquals(view.ImageSource, originalSmall))), "Large thumbnail below row text");
            Require(ReferenceEquals(model.SelectedArticle, rows[0]), "Changing thumbnail mode lost article selection.");
            var largeView = Descendants(list).OfType<ThumbnailView>().Single(view =>
                view.Tag is Article { ArticleID: "timeline-thumbnail-0" });
            var largeRow = (Grid)largeView.Parent;
            double previousWidth = largeRow.Width;
            try
            {
                largeRow.Width = 300;
                var icon = largeRow.Children.OfType<IconView>().Single();
                await WaitFor(() => Task.FromResult(Math.Abs(largeView.ActualWidth - 278) < 1
                    && largeView.ActualHeight == 180
                    && Math.Abs(largeView.TransformToVisual(largeRow).TransformPoint(new(0, 0)).X
                        - icon.TransformToVisual(largeRow).TransformPoint(new(0, 0)).X) < 1),
                    "Large thumbnail keeps fixed height and aligns with the icon after resizing");
                try
                {
                    await Task.Run(() => Core.AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Never));
                    var text = largeRow.Children.OfType<StackPanel>().Single();
                    await WaitFor(() => Task.FromResult(icon.Visibility == Visibility.Collapsed && Grid.GetColumn(largeView) == 2
                        && Math.Abs(largeView.TransformToVisual(largeRow).TransformPoint(new(0, 0)).X
                            - text.TransformToVisual(largeRow).TransformPoint(new(0, 0)).X) < 1),
                        "Large thumbnail aligns with text without an icon");
                }
                finally { Core.AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Always); }
                await WaitFor(() => Task.FromResult(icon.Visibility == Visibility.Visible
                    && Grid.GetColumn(largeView) == 1 && feedIconNotifications == 2),
                    "Shared feed-icon display notifications restore icon alignment");
                Require(modeNotificationsOnUI, "Feed-icon preference notifications ran outside the UI dispatcher.");
            }
            finally { largeRow.Width = previousWidth; }

            list.ScrollIntoView(rows[20], ScrollIntoViewAlignment.Leading);
            await WaitFor(() => Task.FromResult(((ItemsStackPanel)list.ItemsPanelRoot).FirstVisibleIndex >= 19
                && content.ProbeThumbnailWorkIsIdle), "Scrolled large-thumbnail rows settle before height changes");
            await new RenderTargetBitmap().RenderAsync(list);
            int topIndex = ((ItemsStackPanel)list.ItemsPanelRoot).FirstVisibleIndex;
            var topRow = (FrameworkElement)list.ContainerFromItem(rows[topIndex]);
            var precedingRow = list.ContainerFromItem(rows[topIndex - 1]) as FrameworkElement
                ?? throw new InvalidOperationException("The anchoring fixture needs a realized row above the viewport.");
            double topY = topRow.TransformToVisual(list).TransformPoint(new(0, 0)).Y;
            double precedingHeight = precedingRow.ActualHeight;
            Require(precedingHeight > 180, "The preceding row did not initially contain a large thumbnail.");
            var precedingArticle = rows[topIndex - 1].SourceArticle!;
            foreach (bool restoreImage in new[] { false, true })
            {
                rows[topIndex - 1].UpdateFrom(Row(restoreImage ? precedingArticle : ArticleFor(topIndex - 1, null)));
                await WaitFor(() => Task.FromResult(content.ProbeThumbnailWorkIsIdle
                    && (restoreImage ? precedingRow.ActualHeight > 180 : precedingRow.ActualHeight < precedingHeight - 100)),
                    "Large-thumbnail row above the viewport changes height");
                await new RenderTargetBitmap().RenderAsync(list);
                Require(ReferenceEquals(list.ContainerFromItem(rows[topIndex]), topRow)
                    && Math.Abs(topRow.TransformToVisual(list).TransformPoint(new(0, 0)).Y - topY) <= 1,
                    "Changing a large-thumbnail row above the viewport moved the top visible article.");
            }

            list.ScrollIntoView(rows[3], ScrollIntoViewAlignment.Leading);
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any(view =>
                view.Tag is Article { ArticleID: "timeline-thumbnail-3" } && view.Visibility == Visibility.Collapsed
                && ((Grid)view.Parent).ActualHeight < 100)), "Image-less large-mode row remains compact");
            Core.AppDefaults.Shared.UseColumnLayout = true;
            await WaitFor(() => Task.FromResult(!largeMenu.IsEnabled && largeMenu.IsChecked
                && !Descendants(list).OfType<ThumbnailView>().Any()), "Large thumbnails disabled in columns");
            Core.AppDefaults.Shared.UseColumnLayout = false;
            await Task.Run(() => Core.AppDefaults.Shared.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.Small));
            list.ScrollIntoView(rows[0], ScrollIntoViewAlignment.Leading);
            await WaitFor(() => Task.FromResult(modeNotifications == 2 && peer.TimelineThumbnailsDisplay == TimelineThumbnailsDisplay.Small
                && !largeMenu.IsChecked && largeMenu.IsEnabled && Descendants(list).OfType<ThumbnailView>().Any(view =>
                    view.Tag is Article { ArticleID: "timeline-thumbnail-0" } && Grid.GetRow(view) == 0
                    && Grid.GetColumn(view) == 3 && view.ActualHeight <= 76 && view.ImageSource is not null)),
                "Return to small thumbnails without reopening the window");
            Require(ReferenceEquals(model.SelectedArticle, rows[0]), "Restoring small mode lost article selection.");

            var preferences = new AppWindows.PreferencesWindow();
            try
            {
                preferences.Activate();
                var preferencesRoot = (FrameworkElement)preferences.Content;
                var choices = (ComboBox)preferencesRoot.FindName("TimelineThumbnailsCombo");
                var status = (TextBlock)preferencesRoot.FindName("TimelineThumbnailsStatus");
                await WaitFor(() => Task.FromResult(choices.IsLoaded
                    && choices.SelectedItem is ComboBoxItem { Tag: TimelineThumbnailsDisplay.Small }),
                    "Thumbnail settings picker reflects the menu");
                await CheckTimelineFeedIconSettingsAsync(window, preferences, peer);
                using (var locked = new FileStream(Path.Combine(Core.AppConfig.DataDirectory, "AppDefaults.json"),
                    FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    choices.SelectedItem = choices.Items.OfType<ComboBoxItem>()
                        .Single(item => item.Tag is TimelineThumbnailsDisplay.None);
                    Require(choices.SelectedItem is ComboBoxItem { Tag: TimelineThumbnailsDisplay.Small }
                        && status.Text.Contains("Could not save", StringComparison.Ordinal)
                        && model.TimelineThumbnailsDisplay == TimelineThumbnailsDisplay.Small,
                        "A failed thumbnail preference write did not restore the UI and report the error.");
                }
                choices.SelectedItem = choices.Items.OfType<ComboBoxItem>()
                    .Single(item => item.Tag is TimelineThumbnailsDisplay.None);
                await WaitFor(() => Task.FromResult(peer.TimelineThumbnailsDisplay == TimelineThumbnailsDisplay.None
                    && modeItems.Single(item => item.IsChecked).Tag is TimelineThumbnailsDisplay.None
                    && Descendants(list).OfType<ThumbnailView>().All(view => view.Visibility == Visibility.Collapsed)),
                    "Hidden thumbnails propagate from Preferences");
                Require(new Core.AppDefaults().GetTimelineThumbnailsDisplay() == TimelineThumbnailsDisplay.None
                    && string.IsNullOrEmpty(status.Text), "Hidden mode did not persist or clear the previous error.");
                await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Reader applies hidden thumbnail mode");
                Require(await ScriptBool(content, "document.querySelector('img[data-nnw-src]') === null"),
                    "Hidden mode kept routing reader images through the timeline cache.");
                var hiddenRow = Row(ArticleFor(80, "https://thumbnail.test/hidden-only.bmp"));
                rows.Add(hiddenRow);
                list.ScrollIntoView(hiddenRow, ScrollIntoViewAlignment.Leading);
                await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any(view =>
                    ReferenceEquals(view.Tag, hiddenRow.SourceArticle) && view.Visibility == Visibility.Collapsed)),
                    "Hidden-mode newly realized row");
                await Task.Delay(150);
                Require(!requests.Contains("/hidden-only.bmp"),
                    "Hidden mode discovered/downloaded a thumbnail for a newly realized row.");
                var commandPeer = FrameworkElementAutomationPeer.FromElement(largeMenu)
                    ?? FrameworkElementAutomationPeer.CreatePeerForElement(largeMenu);
                (commandPeer?.GetPattern(PatternInterface.Toggle) as IToggleProvider
                    ?? throw new InvalidOperationException("The thumbnail menu choice has no Toggle pattern.")).Toggle();
                await WaitFor(() => Task.FromResult(choices.SelectedItem is ComboBoxItem { Tag: TimelineThumbnailsDisplay.Large }
                    && modeItems.Count(item => item.IsChecked) == 1 && largeMenu.IsChecked
                    && ((ItemsStackPanel)list.ItemsPanelRoot).FirstVisibleIndex == 0
                    && Descendants(list).OfType<ThumbnailView>().Any(view =>
                        ReferenceEquals(view.Tag, rows[0].SourceArticle) && view.ImageSource is not null)),
                    "Menu choice updates Preferences and restores the selected row to view");
                Require(modeNotificationsOnUI && ReferenceEquals(model.SelectedArticle, rows[0]),
                    "Settings/menu mode changes lost selection or notified off the UI thread.");
            }
            finally
            {
                preferences.Close();
                window.Activate();
            }

            setNetworkAllowed(false);
            var recoveryArticle = ArticleFor(0, "https://thumbnail.test/availability-only.bmp");
            rows[0].UpdateFrom(Row(recoveryArticle));
            try
            {
                await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any(view =>
                    ReferenceEquals(view.Tag, recoveryArticle) && view.Visibility == Visibility.Visible
                    && view.ImageSource is null && !content.ProbeThumbnailRequestIsLoading(view))),
                    "Offline row settles with a thumbnail placeholder");
                await Task.Delay(250);
                // Finish phased bindings before an unrelated rebind can mask a missing availability update.
                await new RenderTargetBitmap().RenderAsync(list);
                await WaitFor(() => Task.FromResult(content.ProbeThumbnailWorkIsIdle
                    && ((ItemsStackPanel)list.ItemsPanelRoot).FirstVisibleIndex == 0),
                    "Offline layout and thumbnail admission drain before external availability");
                Require(!requests.Contains("/availability-only.bmp"), "An offline row admitted a network download.");
                setNetworkAllowed(true);
                Require(await service.ThumbnailAsync(recoveryArticle, ThumbnailMode.Large) is not null,
                    "Another consumer did not make the thumbnail available.");
                await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any(view =>
                    ReferenceEquals(view.Tag, recoveryArticle) && view.ImageSource is not null)),
                    "Shared thumbnail availability fills the settled placeholder without scrolling");
            }
            catch
            {
                File.WriteAllText(Path.Combine(OutputDirectory, "thumbnail-availability-state.json"),
                    System.Text.Json.JsonSerializer.Serialize(content.ProbeTimelineThumbnailState,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                throw;
            }
            Require(ReferenceEquals(model.SelectedArticle, rows[0]),
                "Shared thumbnail availability changed the selected article.");
        }
        finally
        {
            setNetworkAllowed(true);
            releaseHeld.TrySetResult();
            releaseMissing.TrySetResult();
            model.ArticleItems = previousItems;
            model.SelectedArticle = previousSelection;
            Core.AppDefaults.Shared.UseColumnLayout = previousColumns;
            Core.AppDefaults.Shared.ArticleContentJavascriptEnabled = previousScripts;
            Core.AppDefaults.Shared.SetTimelineThumbnailsDisplay(previousDisplay);
            Core.AppDefaults.Shared.SetTimelineFeedIconsDisplay(previousFeedIcons);
            model.IsUnifiedLayout = previousUnified;
            content.ProbeSetThumbnailService(AppRuntime.AppService.Shared.ThumbnailImages);
        }
    }

    private static WriteableBitmap ThumbnailBands(bool portrait)
    {
        int width = portrait ? 40 : 120, height = portrait ? 120 : 40;
        var bitmap = new WriteableBitmap(width, height);
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                bool red = portrait ? y < 40 : x < 40;
                bool green = !portrait && x is >= 40 and < 80;
                pixels[offset] = !red && !green ? (byte)255 : (byte)0;
                pixels[offset + 1] = green ? (byte)255 : (byte)0;
                pixels[offset + 2] = red ? (byte)255 : (byte)0;
                pixels[offset + 3] = 255;
            }
        using var buffer = bitmap.PixelBuffer.AsStream();
        buffer.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }

    private static byte[] ThumbnailBitmap(int width = 1600, int height = 800)
    {
        int stride = width * 3;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4d42);
        writer.Write(54 + stride * height);
        writer.Write(0); writer.Write(54); writer.Write(40);
        writer.Write(width); writer.Write(height);
        writer.Write((ushort)1); writer.Write((ushort)24);
        writer.Write(0); writer.Write(stride * height);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        for (int index = 0; index < width * height; index++)
        {
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)255);
        }
        return output.ToArray();
    }

    private static byte[] MultiFrameThumbnailTiff()
    {
        (int Width, int Height)[] frames = [(32, 32), (400, 200), (400, 400)];
        const int entries = 10, directoryBytes = 2 + entries * 12 + 4;
        int bitsStart = 8 + frames.Length * directoryBytes;
        int pixelsOffset = bitsStart + frames.Length * 6;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8);
        void Entry(ushort tag, ushort type, int count, int value)
        {
            writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value);
        }
        for (int index = 0; index < frames.Length; index++)
        {
            var (width, height) = frames[index];
            writer.Write((ushort)entries);
            Entry(256, 4, 1, width);
            Entry(257, 4, 1, height);
            Entry(258, 3, 3, bitsStart + index * 6);
            Entry(259, 3, 1, 1);
            Entry(262, 3, 1, 2);
            Entry(273, 4, 1, pixelsOffset);
            Entry(277, 3, 1, 3);
            Entry(278, 4, 1, height);
            Entry(279, 4, 1, width * height * 3);
            Entry(284, 3, 1, 1);
            writer.Write(index + 1 < frames.Length ? 8 + (index + 1) * directoryBytes : 0);
            pixelsOffset += width * height * 3;
        }
        foreach (var _ in frames)
        {
            writer.Write((ushort)8); writer.Write((ushort)8); writer.Write((ushort)8);
        }
        for (int index = 0; index < frames.Length; index++)
            for (int pixel = 0; pixel < frames[index].Width * frames[index].Height; pixel++)
            {
                writer.Write(index == 1 ? (byte)255 : (byte)0);
                writer.Write(index == 0 ? (byte)255 : (byte)0);
                writer.Write(index == 2 ? (byte)255 : (byte)0);
            }
        return output.ToArray();
    }

    private static byte[] OrientedThumbnailTiff()
    {
        const int width = 400, height = 200, entries = 11;
        const int bitsOffset = 8 + 2 + entries * 12 + 4;
        const int pixelsOffset = bitsOffset + 6;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8);
        writer.Write((ushort)entries);
        void Entry(ushort tag, ushort type, int count, int value)
        {
            writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value);
        }
        Entry(256, 4, 1, width);
        Entry(257, 4, 1, height);
        Entry(258, 3, 3, bitsOffset);
        Entry(259, 3, 1, 1);
        Entry(262, 3, 1, 2);
        Entry(273, 4, 1, pixelsOffset);
        Entry(274, 3, 1, 6);
        Entry(277, 3, 1, 3);
        Entry(278, 4, 1, height);
        Entry(279, 4, 1, width * height * 3);
        Entry(284, 3, 1, 1);
        writer.Write(0);
        writer.Write((ushort)8); writer.Write((ushort)8); writer.Write((ushort)8);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                writer.Write(x < width / 2 ? (byte)255 : (byte)0);
                writer.Write((byte)0);
                writer.Write(x >= width / 2 ? (byte)255 : (byte)0);
            }
        return output.ToArray();
    }

    private sealed class ThumbnailHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return respond(request, ct);
        }
    }
}
#endif
