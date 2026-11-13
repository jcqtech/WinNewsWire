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
        int requests = 0, notifications = 0;
        bool initializationOnUI = true, notificationOnUI = true;
        bool networkAllowed = true;
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
                _ => imageData
            };
            return new HttpResponseMessage(request.RequestUri.AbsolutePath == "/missing.bmp"
                ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }));
        var downloader = new Downloader(http);
        using var database = new ImageMetadataDatabase(Path.Combine(OutputDirectory, "thumbnail-metadata.db"));
        await using var service = new ArticleThumbnailService(window.DispatcherQueue, () =>
        {
            initializationOnUI = window.DispatcherQueue.HasThreadAccess;
            return (new ArticleThumbnailFinder(database),
                new ThumbnailDownloader(database, Path.Combine(OutputDirectory, "thumbnail-cache"),
                    () => networkAllowed, downloader));
        }, cacheByteLimit: 3_000_000);
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
        Require(service.ProbeLastDecodedSize == (512, 256), "Small thumbnails were not decoded at display size.");
        Require(ReferenceEquals(small, simultaneous[1]) && service.ProbeDecodes == 1,
            "Concurrent discovery/download/decoding was not coalesced.");
        Require(service.ProbeUrlResolutions == resolutions,
            "The thumbnail service recomputed an already resolved image URL.");
        Require(!initializationOnUI && !service.ProbeDecodedOnUIThread && notificationOnUI && notifications == 1,
            "Thumbnail initialization/decoding or availability notifications ran on the wrong thread.");
        Require(ReferenceEquals(small, await service.ThumbnailAsync(article, ThumbnailMode.Small))
            && service.ProbeDecodes == 1, "A warm thumbnail was decoded again.");

        var large = await service.ThumbnailAsync(article, ThumbnailMode.Large);
        Require(service.ProbeLastDecodedSize == (1200, 600), "Large thumbnails were not decoded at display size.");
        Require(large is not null && !ReferenceEquals(small, large) && service.ProbeDecodes == 2,
            "Small and large decoded thumbnails shared a cache entry.");
        Require(service.ProbeCacheBytes == 1200L * 600 * 4,
            "The decoded-image budget did not evict the older small thumbnail.");
        var reloaded = await service.ThumbnailAsync(article, ThumbnailMode.Small);
        Require(reloaded is not null && !ReferenceEquals(small, reloaded) && service.ProbeDecodes == 3
            && service.ProbeCacheBytes == 512L * 256 * 4,
            "Decoded thumbnail LRU eviction or byte accounting failed.");

        Article FixtureFor(string filename) => new("account", filename, "feed", filename, "Decoder fixture",
            $"<img src='https://thumbnail.test/{filename}'>", null, null, "https://thumbnail.test/article",
            null, null, null, null, null, null, null, new ArticleStatus(filename, false, false, DateTime.UnixEpoch));
        var oriented = await service.ThumbnailAsync(FixtureFor("oriented.tiff"), ThumbnailMode.Small);
        Require(oriented is not null && service.ProbeLastDecodedSize == (200, 400),
            "The native thumbnail decoder ignored EXIF orientation.");
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
            Require(pixels[offset] < 50 && pixels[offset + 1] < 50 && pixels[offset + 2] > 200 && pixels[offset + 3] == 255,
                "The decoded thumbnail did not aspect-fill the square with visible red image pixels.");
            Require(pixels[3] == 0, "The thumbnail image was not clipped to its rounded corner.");
            image.ImageSource = null;
            await raster.RenderAsync(image, 80, 80);
            var reset = (await raster.GetPixelsAsync()).ToArray();
            Require(reset.AsSpan(center, 4).SequenceEqual(dark.AsSpan(center, 4)),
                "Recycling the thumbnail view did not restore its placeholder.");

            image.ImageSource = reloaded;
            image.FitsImage = true;
            await raster.RenderAsync(image, 80, 80);
            var fitted = (await raster.GetPixelsAsync()).ToArray();
            Require(fitted.AsSpan(offset, 4).SequenceEqual(dark.AsSpan(offset, 4))
                && fitted[center + 2] > 200 && fitted[center] < 50,
                "Fit mode cropped the wide image or removed its surrounding fill.");
            image.FitsImage = false;
            image.ImageSource = ThumbnailBands(portrait: true);
            await raster.RenderAsync(image, 80, 80);
            var portrait = (await raster.GetPixelsAsync()).ToArray();
            Require(portrait[center + 2] > 200 && portrait[center] < 50,
                "Portrait fill cropped from the center instead of preserving the top.");
            image.FitsImage = true;
            image.FillBrush = new SolidColorBrush(Microsoft.UI.Colors.Magenta);
            await raster.RenderAsync(image, 80, 80);
            var fullPortrait = (await raster.GetPixelsAsync()).ToArray();
            int side = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth / 8) * 4;
            int bottom = (raster.PixelHeight * 13 / 16 * raster.PixelWidth + raster.PixelWidth / 2) * 4;
            Require(fullPortrait[side] > 200 && fullPortrait[side + 1] < 50 && fullPortrait[side + 2] > 200
                && fullPortrait[offset + 2] > 200 && fullPortrait[offset] < 50
                && fullPortrait[bottom] > 200 && fullPortrait[bottom + 2] < 50,
                "Fit mode lost the custom fill or cropped the portrait's top/bottom.");
            image.FitsImage = false;
            image.ImageSource = ThumbnailBands(portrait: false);
            await raster.RenderAsync(image, 80, 80);
            var wide = (await raster.GetPixelsAsync()).ToArray();
            Require(wide[center + 1] > 200 && wide[center] < 50 && wide[center + 2] < 50,
                "Wide fill did not crop equally from both sides.");
            image.Width = 160;
            await WaitFor(() => Task.FromResult(image.ActualWidth == 160), "Resized thumbnail layout");
            await raster.RenderAsync(image, 160, 80);
            var resized = (await raster.GetPixelsAsync()).ToArray();
            int left = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth / 16) * 4;
            int right = (raster.PixelHeight / 2 * raster.PixelWidth + raster.PixelWidth * 15 / 16) * 4;
            Require(resized[left + 2] > 200 && resized[left] < 50
                && resized[right] > 200 && resized[right + 2] < 50 && resized[3] == 0,
                "Resizing did not update the crop or preserve rounded clipping.");

            image.ImageSource = oriented;
            image.FitsImage = true;
            await raster.RenderAsync(image, 160, 80);
            var rotated = (await raster.GetPixelsAsync()).ToArray();
            int topHalf = (raster.PixelHeight / 4 * raster.PixelWidth + raster.PixelWidth / 2) * 4;
            int bottomHalf = (raster.PixelHeight * 3 / 4 * raster.PixelWidth + raster.PixelWidth / 2) * 4;
            Require(rotated[topHalf + 2] > 200 && rotated[topHalf] < 50
                && rotated[bottomHalf] > 200 && rotated[bottomHalf + 2] < 50,
                "EXIF-oriented native thumbnail pixels were not rotated from left/right to top/bottom.");
        }
        finally { host.Children.Remove(image); }

        int downloaded = requests;
        service.EmptyCache();
        Require(service.ProbeCacheBytes == 0, "Low-memory cleanup retained decoded thumbnails.");
        networkAllowed = false;
        Require(await service.ThumbnailAsync(article, ThumbnailMode.Small) is not null && requests == downloaded,
            "Offline thumbnail display did not reuse disk data.");
        networkAllowed = true;
        await CheckTimelineThumbnailsAsync(window, service, requested, heldStarted, releaseHeld, releaseMissing,
            allowed => networkAllowed = allowed);
        Require(failureOnUI && failures == 4, "Permanent thumbnail failures were not dispatched once to the UI.");
        await service.DisposeAsync();
        Require(service.ProbeCacheBytes == 0, "Shutdown retained the decoded thumbnail cache.");
        bool stopped = false;
        try { await service.ThumbnailAsync(article, ThumbnailMode.Small); }
        catch (ObjectDisposedException) { stopped = true; }
        Require(stopped, "A disposed thumbnail service accepted another request.");
        return new { InitializationOffUI = true, DecodingOffUI = true, NotificationsOnUI = true,
            CoalescedDiscoveryAndDecode = true, SeparateModes = true, DecodedLRUBudget = true,
            DisplaySizeDecode = true, ExifOrientation = true, NativeDimensionRejection = true,
            DecoderFailureStopsRetriesWithoutSuppressingIcons = true,
            VisibleNativePixels = true, AspectFill = true, RoundedClipping = true, ThemedPlaceholder = true,
            RecycledPlaceholder = true, LowMemoryCleanup = true, OfflineDiskReuse = true, Shutdown = true,
            TimelineRows = true, HTMLDiscovery = true, FailedCandidateFallback = true, FailureNotificationsOnUI = true, BoundedLookahead = true,
            RecycledRowRejectsLateImage = true, ColumnLayoutExcludesThumbnails = true,
            LargeRows = true, LargeModePersists = true, SharedModeNotificationsOnUI = true,
            LargeMenuState = true, FixedHeightLargeRows = true, ImagelessRowsStayCompact = true,
            ModeChangesPreserveSelection = true, FailureRefreshTargetsAffectedRows = true,
            WholeImageFit = true, CustomLetterboxFill = true, TopAlignedPortraitCrop = true,
            CenteredWideCrop = true, CropUpdatesOnResize = true, LargeImageAlignsWithIconOrText = true,
            HiddenModeStopsNewRequests = true, MenuAndPreferencesShareMode = true,
            SharedAvailabilityFillsOfflinePlaceholder = true,
            FailedPreferenceWritesRestoreSelectionAndReport = true,
            FeedIconsAlwaysNeverAutomatic = true, FeedIconsColumnException = true,
            FeedIconsSharedSettingsOnUI = true, FeedIconsFailedWriteRecovery = true };
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
            await Task.Run(() => Core.AppDefaults.Shared.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.Large));
            await WaitFor(() => Task.FromResult(modeNotifications > 0 && peer.TimelineThumbnailsDisplay == TimelineThumbnailsDisplay.Large
                && largeMenu.IsChecked && largeMenu.IsEnabled), "Shared large-thumbnail mode");
            Require(modeNotificationsOnUI && new Core.AppDefaults().GetTimelineThumbnailsDisplay() == TimelineThumbnailsDisplay.Large
                && modeItems.Count(item => item.IsChecked) == 1,
                "Large-thumbnail mode was not persisted or a peer notification ran off the UI thread.");
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any(view =>
                view.Tag is Article { ArticleID: "timeline-thumbnail-0" } && Grid.GetRow(view) == 1
                && Grid.GetColumn(view) == 1 && Grid.GetColumnSpan(view) == 3 && view.ActualHeight == 180
                && view.FitsImage && view.ImageSource is not null
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
