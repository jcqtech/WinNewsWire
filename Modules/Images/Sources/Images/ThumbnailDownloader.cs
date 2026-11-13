using WinNewsWire.ActivityLog;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

public sealed class ThumbnailDownloader : IAsyncDisposable
{
    private readonly ImageMetadataDatabase _database;
    private readonly Func<bool> _networkAllowsDownloads;
    private readonly DownloadedImageCache _downloadedImages;
    private readonly bool _ownsDownloadedImages;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _operations = [];
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
        _database = database;
        _networkAllowsDownloads = networkAllowsDownloads;
        _downloadedImages = downloadedImages ?? new(database, cacheFolder, downloader, clock, activityLog);
        _ownsDownloadedImages = downloadedImages is null;
        _downloadedImages.DownloadFailed += ResourceDownloadFailed;
    }

    private void ResourceDownloadFailed(object? sender, string url) => DownloadFailed?.Invoke(this, url);

    public Task<DownloadedImage?> SourceAsync(string url,
        CancellationToken ct = default, string? activityDetail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return RunOperationAsync(async token =>
        {
            if (_database.RecentlyFailed(url) || _database.RecentlyLackedSignificantDimensions(url)) return null;
            var request = new ImageResourceRequest(ActivityOwner.ThumbnailDownloader, ActivityKind.DownloadThumbnail(url),
                activityDetail, _networkAllowsDownloads);
            return (await _downloadedImages.FetchAsync(url, request, token).ConfigureAwait(false)).Image;
        }, ct);
    }

    public Task RejectInsignificantDimensionsAsync(string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return RunOperationAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            await _database.RecordInsignificantDimensionsAsync(url).ConfigureAwait(false);
            DownloadFailed?.Invoke(this, url);
            return true;
        }, ct);
    }

    private Task<T> RunOperationAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var operation = Task.Run(async () =>
            {
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    return await work(cancellation.Token).ConfigureAwait(false);
                }
                finally { cancellation.Dispose(); }
            });
            _operations.Add(operation);
            _ = operation.ContinueWith(completed =>
            {
                lock (_gate) _operations.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return operation;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _downloadedImages.DownloadFailed -= ResourceDownloadFailed;
            _lifetime.Cancel();
            return new(_shutdown = DrainAsync(_operations.ToArray()));
        }
    }

    private async Task DrainAsync(Task[] operations)
    {
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            try
            {
                if (_ownsDownloadedImages) await _downloadedImages.DisposeAsync().ConfigureAwait(false);
            }
            finally { _lifetime.Dispose(); }
        }
    }
}
