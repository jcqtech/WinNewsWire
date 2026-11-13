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
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinNewsWire.Articles;
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
            return new HttpResponseMessage(request.RequestUri.AbsolutePath == "/missing.bmp"
                ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new ByteArrayContent(imageData) };
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
        var simultaneous = await Task.WhenAll(service.ThumbnailAsync(article, ThumbnailMode.Small),
            service.ThumbnailAsync(article, ThumbnailMode.Small));
        var small = simultaneous[0] ?? throw new InvalidOperationException("The thumbnail did not decode.");
        Require(ReferenceEquals(small, simultaneous[1]) && service.ProbeDecodes == 1,
            "Concurrent discovery/download/decoding was not coalesced.");
        Require(!initializationOnUI && !service.ProbeDecodedOnUIThread && notificationOnUI && notifications == 1,
            "Thumbnail initialization/decoding or availability notifications ran on the wrong thread.");
        Require(ReferenceEquals(small, await service.ThumbnailAsync(article, ThumbnailMode.Small))
            && service.ProbeDecodes == 1, "A warm thumbnail was decoded again.");

        var large = await service.ThumbnailAsync(article, ThumbnailMode.Large);
        Require(large is not null && !ReferenceEquals(small, large) && service.ProbeDecodes == 2,
            "Small and large decoded thumbnails shared a cache entry.");
        Require(service.ProbeCacheBytes == 1200L * 600 * 4,
            "The decoded-image budget did not evict the older small thumbnail.");
        var reloaded = await service.ThumbnailAsync(article, ThumbnailMode.Small);
        Require(reloaded is not null && !ReferenceEquals(small, reloaded) && service.ProbeDecodes == 3
            && service.ProbeCacheBytes == 512L * 256 * 4,
            "Decoded thumbnail LRU eviction or byte accounting failed.");

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
        }
        finally { host.Children.Remove(image); }

        int downloaded = requests;
        service.EmptyCache();
        Require(service.ProbeCacheBytes == 0, "Low-memory cleanup retained decoded thumbnails.");
        networkAllowed = false;
        Require(await service.ThumbnailAsync(article, ThumbnailMode.Small) is not null && requests == downloaded,
            "Offline thumbnail display did not reuse disk data.");
        networkAllowed = true;
        await CheckTimelineThumbnailsAsync(window, service, requested, heldStarted, releaseHeld, releaseMissing);
        Require(failureOnUI && failures == 1, "Permanent thumbnail failures were not dispatched once to the UI.");
        await service.DisposeAsync();
        Require(service.ProbeCacheBytes == 0, "Shutdown retained the decoded thumbnail cache.");
        bool stopped = false;
        try { await service.ThumbnailAsync(article, ThumbnailMode.Small); }
        catch (ObjectDisposedException) { stopped = true; }
        Require(stopped, "A disposed thumbnail service accepted another request.");
        return new { InitializationOffUI = true, DecodingOffUI = true, NotificationsOnUI = true,
            CoalescedDiscoveryAndDecode = true, SeparateModes = true, DecodedLRUBudget = true,
            VisibleNativePixels = true, AspectFill = true, RoundedClipping = true, ThemedPlaceholder = true,
            RecycledPlaceholder = true, LowMemoryCleanup = true, OfflineDiskReuse = true, Shutdown = true,
            TimelineRows = true, HTMLDiscovery = true, FailedCandidateFallback = true, FailureNotificationsOnUI = true, BoundedLookahead = true,
            RecycledRowRejectsLateImage = true, ColumnLayoutExcludesThumbnails = true,
            LargeRows = true, LargeModePersists = true, SharedModeNotificationsOnUI = true,
            LargeMenuState = true, FixedHeightLargeRows = true, ImagelessRowsStayCompact = true,
            ModeChangesPreserveSelection = true, FailureRefreshTargetsAffectedRows = true,
            WholeImageFit = true, CustomLetterboxFill = true, TopAlignedPortraitCrop = true,
            CenteredWideCrop = true, CropUpdatesOnResize = true, LargeImageAlignsWithIconOrText = true };
    }

    private static async Task CheckTimelineThumbnailsAsync(MainWindow window, ArticleThumbnailService service,
        ConcurrentQueue<string> requests, TaskCompletionSource heldStarted, TaskCompletionSource releaseHeld,
        TaskCompletionSource releaseMissing)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var previousItems = model.ArticleItems;
        var previousSelection = model.SelectedArticle;
        bool previousColumns = Core.AppDefaults.Shared.UseColumnLayout;
        bool previousLarge = Core.AppDefaults.Shared.TimelineShowsLargeThumbnails;
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
            Core.AppDefaults.Shared.TimelineShowsLargeThumbnails = false;
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

            var largeMenu = Descendants(window.Content).OfType<MenuBar>().SelectMany(bar => bar.Items)
                .OfType<MenuBarItem>().SelectMany(item => item.Items).OfType<ToggleMenuFlyoutItem>()
                .Single(item => item.Name == "MenuLargeThumbnails");
            using var peer = new ViewModels.MainViewModel();
            int modeNotifications = 0;
            bool modeNotificationsOnUI = true;
            peer.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(peer.TimelineShowsLargeThumbnails)) return;
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
            await Task.Run(() => Core.AppDefaults.Shared.TimelineShowsLargeThumbnails = true);
            await WaitFor(() => Task.FromResult(modeNotifications > 0 && peer.TimelineShowsLargeThumbnails
                && largeMenu.IsChecked && largeMenu.IsEnabled), "Shared large-thumbnail mode");
            Require(modeNotificationsOnUI && new Core.AppDefaults().TimelineShowsLargeThumbnails,
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
                var previousIconVisibility = icon.Visibility;
                try
                {
                    icon.Visibility = Visibility.Collapsed;
                    var text = largeRow.Children.OfType<StackPanel>().Single();
                    await WaitFor(() => Task.FromResult(Grid.GetColumn(largeView) == 2
                        && Math.Abs(largeView.TransformToVisual(largeRow).TransformPoint(new(0, 0)).X
                            - text.TransformToVisual(largeRow).TransformPoint(new(0, 0)).X) < 1),
                        "Large thumbnail aligns with text without an icon");
                }
                finally { icon.Visibility = previousIconVisibility; }
            }
            finally { largeRow.Width = previousWidth; }

            list.ScrollIntoView(rows[3], ScrollIntoViewAlignment.Leading);
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any(view =>
                view.Tag is Article { ArticleID: "timeline-thumbnail-3" } && view.Visibility == Visibility.Collapsed
                && ((Grid)view.Parent).ActualHeight < 100)), "Image-less large-mode row remains compact");
            Core.AppDefaults.Shared.UseColumnLayout = true;
            await WaitFor(() => Task.FromResult(!largeMenu.IsEnabled && largeMenu.IsChecked
                && !Descendants(list).OfType<ThumbnailView>().Any()), "Large thumbnails disabled in columns");
            Core.AppDefaults.Shared.UseColumnLayout = false;
            await Task.Run(() => Core.AppDefaults.Shared.TimelineShowsLargeThumbnails = false);
            list.ScrollIntoView(rows[0], ScrollIntoViewAlignment.Leading);
            await WaitFor(() => Task.FromResult(modeNotifications == 2 && !peer.TimelineShowsLargeThumbnails
                && !largeMenu.IsChecked && largeMenu.IsEnabled && Descendants(list).OfType<ThumbnailView>().Any(view =>
                    view.Tag is Article { ArticleID: "timeline-thumbnail-0" } && Grid.GetRow(view) == 0
                    && Grid.GetColumn(view) == 3 && view.ActualHeight <= 76 && view.ImageSource is not null)),
                "Return to small thumbnails without reopening the window");
            Require(ReferenceEquals(model.SelectedArticle, rows[0]), "Restoring small mode lost article selection.");
        }
        finally
        {
            releaseHeld.TrySetResult();
            releaseMissing.TrySetResult();
            model.ArticleItems = previousItems;
            model.SelectedArticle = previousSelection;
            Core.AppDefaults.Shared.UseColumnLayout = previousColumns;
            Core.AppDefaults.Shared.TimelineShowsLargeThumbnails = previousLarge;
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

    private static byte[] ThumbnailBitmap()
    {
        const int width = 1600, height = 800, stride = width * 3;
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
