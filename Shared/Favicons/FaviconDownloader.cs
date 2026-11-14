using System.Diagnostics;
using WinNewsWire.Account;

namespace WinNewsWire.AppShared.Favicons;

/// <summary>Windows feed-model adapter for the native Images/HTMLMetadata download pipeline.</summary>
public sealed class FaviconDownloader
{
    public static FaviconDownloader Shared { get; } = new();
    public event EventHandler<string>? FaviconDidBecomeAvailable;
    public void EmptyCache()
    {
        if (Images.FeedImageDownloader.IsSharedCreated) Images.FeedImageDownloader.Shared.EmptyCache();
    }

    public async Task<byte[]?> FaviconAsync(Feed feed, CancellationToken ct = default)
    {
        var image = await GetImageAsync(feed, preferFeedIcon: false, ct);
        return image?.Data;
    }

    /// <summary>Downloads (or re-uses cached) favicon for the feed and returns the file
    /// path on disk, suitable for binding to an <c>Image.Source</c>. Returns null if no
    /// favicon could be resolved.</summary>
    public async Task<string?> FaviconPathAsync(Feed feed, CancellationToken ct = default)
    {
        var image = await GetImageAsync(feed, preferFeedIcon: false, ct);
        return image?.Path;
    }

    public async Task<string?> ImagePathAsync(Feed feed, CancellationToken ct = default)
    {
        var image = await GetImageAsync(feed, preferFeedIcon: true, ct);
        return image?.Path;
    }

    private async Task<Images.DownloadedImage?> GetImageAsync(Feed feed, bool preferFeedIcon, CancellationToken ct)
    {
        var source = new Images.FeedImageSource(feed.Url, feed.HomePageUrl, feed.FaviconUrl, feed.IconUrl);
        try
        {
            ct.ThrowIfCancellationRequested();
            var service = await Images.FeedImageDownloader.GetSharedAsync().WaitAsync(ct);
            var image = preferFeedIcon ? await service.IconAsync(source, ct) : null;
            image ??= await service.FaviconAsync(source, ct);
            if (image is not null) FaviconDidBecomeAvailable?.Invoke(this, image.Url);
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
            or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
        {
            Trace.TraceWarning($"Could not load a feed image for {feed.Url}: {error}");
            return null;
        }
    }
}
