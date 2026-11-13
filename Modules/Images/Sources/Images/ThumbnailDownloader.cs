using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

public sealed class ThumbnailDownloader : IAsyncDisposable
{
    private readonly ImageResourceDownloader _small;
    private readonly Lazy<ImageResourceDownloader> _large;
    private readonly DownloadedImageCache _downloadedImages;
    private readonly bool _ownsDownloadedImages;
    private readonly object _gate = new();
    private Task? _shutdown;
    /// <summary>Raised on a worker after persisting a non-transient failure; UI subscribers must dispatch.</summary>
    public event EventHandler<string>? DownloadFailed;

    public ThumbnailDownloader(ImageMetadataDatabase database, string cacheFolder, Func<bool> networkAllowsDownloads,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null,
        DownloadedImageCache? downloadedImages = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentNullException.ThrowIfNull(networkAllowsDownloads);
        _downloadedImages = downloadedImages ?? new(database, cacheFolder, downloader, clock, activityLog);
        _ownsDownloadedImages = downloadedImages is null;
        _downloadedImages.DownloadFailed += ResourceDownloadFailed;
        var small = new ImageResourceConfiguration(512, ScaledImageFormat.Jpeg, 32 * 1024 * 1024, true,
            NetworkAllowsDownloads: networkAllowsDownloads);
        _small = new(database, Path.Combine(cacheFolder, "Thumbnails"), small,
            ActivityOwner.ThumbnailDownloader, ActivityKind.DownloadThumbnail, downloader, clock, activityLog,
            sourceCache: _downloadedImages);
        _small.DownloadFailed += ResourceDownloadFailed;
        _large = new(() =>
        {
            var service = new ImageResourceDownloader(database, Path.Combine(cacheFolder, "LargeThumbnails"),
                small with { MaxPixelSize = 1200 }, ActivityOwner.ThumbnailDownloader, ActivityKind.DownloadThumbnail,
                downloader, clock, activityLog, sourceCache: _downloadedImages);
            service.DownloadFailed += ResourceDownloadFailed;
            return service;
        });
    }

    private void ResourceDownloadFailed(object? sender, string url) => DownloadFailed?.Invoke(this, url);

    public async Task<DownloadedImage?> ImageAsync(string url, ThumbnailMode mode,
        CancellationToken ct = default, string? activityDetail = null)
    {
        Task<ImageResourceResult> pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            var service = mode switch
            {
                ThumbnailMode.Small => _small,
                ThumbnailMode.Large => _large.Value,
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            pending = service.FetchAsync(url, ct, activityDetail);
        }
        return (await pending.ConfigureAwait(false)).Image;
    }

    public void EmptyCache()
    {
        _small.EmptyCache();
        if (_large.IsValueCreated) _large.Value.EmptyCache();
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _downloadedImages.DownloadFailed -= ResourceDownloadFailed;
            return new(_shutdown = Task.WhenAll(_small.DisposeAsync().AsTask(),
                _large.IsValueCreated ? _large.Value.DisposeAsync().AsTask() : Task.CompletedTask,
                _ownsDownloadedImages ? _downloadedImages.DisposeAsync().AsTask() : Task.CompletedTask));
        }
    }
}
