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
        using var http = new HttpClient(new ThumbnailHandler(async (request, ct) =>
        {
            Interlocked.Increment(ref requests);
            requested.Enqueue(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/held.bmp")
            {
                heldStarted.TrySetResult();
                await releaseHeld.Task.WaitAsync(ct);
            }
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
            int center = (40 * raster.PixelWidth + 40) * 4;
            Require(!light.AsSpan(center, 4).SequenceEqual(dark.AsSpan(center, 4)),
                "The empty thumbnail placeholder did not follow its actual light/dark theme.");
            image.ImageSource = reloaded;
            await raster.RenderAsync(image, 80, 80);
            var pixels = (await raster.GetPixelsAsync()).ToArray();
            int offset = (10 * raster.PixelWidth + 40) * 4;
            Require(pixels[offset] < 50 && pixels[offset + 1] < 50 && pixels[offset + 2] > 200 && pixels[offset + 3] == 255,
                "The decoded thumbnail did not aspect-fill the square with visible red image pixels.");
            Require(pixels[3] == 0, "The thumbnail image was not clipped to its rounded corner.");
            image.ImageSource = null;
            await raster.RenderAsync(image, 80, 80);
            var reset = (await raster.GetPixelsAsync()).ToArray();
            Require(reset.AsSpan(center, 4).SequenceEqual(dark.AsSpan(center, 4)),
                "Recycling the thumbnail view did not restore its placeholder.");
        }
        finally { host.Children.Remove(image); }

        int downloaded = requests;
        service.EmptyCache();
        Require(service.ProbeCacheBytes == 0, "Low-memory cleanup retained decoded thumbnails.");
        networkAllowed = false;
        Require(await service.ThumbnailAsync(article, ThumbnailMode.Small) is not null && requests == downloaded,
            "Offline thumbnail display did not reuse disk data.");
        networkAllowed = true;
        await CheckTimelineThumbnailsAsync(window, service, requested, heldStarted, releaseHeld);
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
            RecycledRowRejectsLateImage = true, ColumnLayoutExcludesThumbnails = true };
    }

    private static async Task CheckTimelineThumbnailsAsync(MainWindow window, ArticleThumbnailService service,
        ConcurrentQueue<string> requests, TaskCompletionSource heldStarted, TaskCompletionSource releaseHeld)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var previousItems = model.ArticleItems;
        var previousSelection = model.SelectedArticle;
        bool previousColumns = Core.AppDefaults.Shared.UseColumnLayout;
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
            index == 0 ? "https://thumbnail.test/held.bmp" : index == 1 ? null
                : index == 2 ? "https://thumbnail.test/missing.bmp" : $"https://thumbnail.test/timeline-photo-{index}.bmp",
            index is 1 or 2 ? $"<img src='https://thumbnail.test/timeline-photo-{index}.bmp'>" : null)).ToArray();
        var rows = new ObservableCollection<Models.FeedItem>(articles.Select(Row));
        try
        {
            Core.AppDefaults.Shared.UseColumnLayout = false;
            model.IsUnifiedLayout = false;
            model.SelectedArticle = null;
            content.ProbeSetThumbnailService(service);
            model.ArticleItems = rows;
            list.ScrollIntoView(rows[0], ScrollIntoViewAlignment.Leading);
            await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>()
                .Any(view => view.Tag is Article { ArticleID: "timeline-thumbnail-1" } && view.ImageSource is not null)),
                "HTML-only timeline thumbnail");
            await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>()
                .Any(view => view.Tag is Article { ArticleID: "timeline-thumbnail-2" } && view.ImageSource is not null)),
                "Failed provided thumbnail fallback");
            var panel = (ItemsStackPanel)list.ItemsPanelRoot;
            int visibleCount = panel.LastVisibleIndex - panel.FirstVisibleIndex + 1;
            int lookaheadEnd = panel.LastVisibleIndex + 1 + visibleCount;
            await WaitFor(() => Task.FromResult(requests.Any(path => path == $"/timeline-photo-{panel.LastVisibleIndex + 1}.bmp")),
                "Next-screen thumbnail prefetch");
            var fetched = requests.Where(path => path.StartsWith("/timeline-photo-", StringComparison.Ordinal))
                .Select(path => int.Parse(path["/timeline-photo-".Length..^4])).ToArray();
            Require(fetched.All(index => index < lookaheadEnd) && fetched.Length < rows.Count,
                "Thumbnail prefetch downloaded beyond the next screenful.");

            var heldView = Descendants(list).OfType<ThumbnailView>().Single(view =>
                view.Tag is Article { ArticleID: "timeline-thumbnail-0" });
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
        }
        finally
        {
            releaseHeld.TrySetResult();
            model.ArticleItems = previousItems;
            model.SelectedArticle = previousSelection;
            Core.AppDefaults.Shared.UseColumnLayout = previousColumns;
            model.IsUnifiedLayout = previousUnified;
            content.ProbeSetThumbnailService(AppRuntime.AppService.Shared.ThumbnailImages);
        }
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
