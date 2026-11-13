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
    private readonly object _gate = new();
    private Task? _shutdown;

    public ThumbnailDownloader(ImageMetadataDatabase database, string cacheFolder, Func<bool> networkAllowsDownloads,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentNullException.ThrowIfNull(networkAllowsDownloads);
        var limiter = new DownloadLimiter(4);
        var small = new ImageResourceConfiguration(512, ScaledImageFormat.Jpeg, 32 * 1024 * 1024, true,
            limiter, networkAllowsDownloads);
        _small = new(database, Path.Combine(cacheFolder, "Thumbnails"), small,
            ActivityOwner.ThumbnailDownloader, ActivityKind.DownloadThumbnail, downloader, clock, activityLog);
        _large = new(() => new(database, Path.Combine(cacheFolder, "LargeThumbnails"), small with { MaxPixelSize = 1200 },
            ActivityOwner.ThumbnailDownloader, ActivityKind.DownloadThumbnail, downloader, clock, activityLog));
    }

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
            return new(_shutdown ??= Task.WhenAll(_small.DisposeAsync().AsTask(),
                _large.IsValueCreated ? _large.Value.DisposeAsync().AsTask() : Task.CompletedTask));
    }
}
