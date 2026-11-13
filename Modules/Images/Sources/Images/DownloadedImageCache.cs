using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.Images;

/// <summary>One disk source per URL; display-size representations stay in their owners' memory caches.</summary>
public sealed class DownloadedImageCache : IAsyncDisposable
{
    public const int MaxPixelSize = 1200;
    private readonly ImageResourceDownloader _downloads;

    internal event EventHandler<string>? DownloadFailed
    {
        add => _downloads.DownloadFailed += value;
        remove => _downloads.DownloadFailed -= value;
    }

    public DownloadedImageCache(ImageMetadataDatabase database, string cacheFolder,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        _downloads = new(database, Path.Combine(cacheFolder, "DownloadedImages"),
            new(MaxPixelSize, ScaledImageFormat.Jpeg, DownloadLimiter: new DownloadLimiter(4), StoresSourceData: true),
            ActivityOwner.FeedImageDownloader, ActivityKind.DownloadFeedImage, downloader, clock, activityLog);
    }

    internal Task<ImageResourceResult> FetchAsync(string url, ImageResourceRequest request, CancellationToken ct)
        => _downloads.FetchAsync(url, ct, request: request);

    public ValueTask DisposeAsync() => _downloads.DisposeAsync();
}
