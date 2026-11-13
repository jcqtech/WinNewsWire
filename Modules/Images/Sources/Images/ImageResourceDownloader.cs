using System.Diagnostics;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

internal sealed record ImageResourceResult(DownloadedImage? Image, bool IsTransient = false, long? RejectedByteCount = null,
    bool DecodingFailed = false);
internal sealed record ImageResourceConfiguration(int MaxPixelSize,
    long MemoryCacheByteLimit = 0, ThumbnailTaskQueue? DownloadQueue = null,
    Func<bool>? NetworkAllowsDownloads = null,
    bool StoresSourceData = false);
internal sealed record ImageResourceRequest(ActivityOwner Owner, ActivityKind Kind, string? Detail,
    Func<bool>? NetworkAllowsDownloads, bool ChecksForUpdates = false, bool UsesDownloadQueue = false);

internal sealed class ImageResourceDownloader : IAsyncDisposable
{
    private readonly ImageDatabase _database;
    private readonly BinaryDiskCache _diskCache;
    private readonly DownloadedImageCache? _sourceCache;
    private readonly ImageResourceConfiguration _configuration;
    private readonly Downloader _downloader;
    private readonly TimeProvider _clock;
    private readonly ActivityLogger _log;
    private readonly ActivityOwner _owner;
    private readonly Func<string, ActivityKind> _kind;
    private readonly SemaphoreSlim? _sourceDiskGate;
    private readonly Func<string, ImageResourceRequest, Task>? _sourceWasRead;
    private readonly Func<string, Task>? _sourceWasSaved;
    private string ScaledCopySuffix => FormattableString.Invariant($"{_configuration.MaxPixelSize}-Png");
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Task<ImageResourceResult>> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unlimitedPending = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _operations = [];
    private static readonly TimeSpan RetryWait = TimeSpan.FromMinutes(37);
    private readonly Dictionary<string, DateTimeOffset> _retryDates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _hostRetryDates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkedListNode<DownloadedImage>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<DownloadedImage> _cacheOrder = [];
    private long _cacheBytes;
    private bool _stopping;
    private Task? _shutdown;
    internal event EventHandler<string>? DownloadFailed;

    internal HashSet<string> CopyPendingUrls()
    {
        lock (_gate) return _pending.Keys.ToHashSet(StringComparer.Ordinal);
    }

    internal ImageResourceDownloader(ImageDatabase database, string cacheFolder, ImageResourceConfiguration configuration,
        ActivityOwner owner, Func<string, ActivityKind> kind, Downloader? downloader = null,
        TimeProvider? clock = null, ActivityLogger? activityLog = null,
        DownloadedImageCache? sourceCache = null, SemaphoreSlim? sourceDiskGate = null,
        Func<string, ImageResourceRequest, Task>? sourceWasRead = null, Func<string, Task>? sourceWasSaved = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.MaxPixelSize);
        ArgumentOutOfRangeException.ThrowIfNegative(configuration.MemoryCacheByteLimit);
        _database = database;
        _diskCache = new(cacheFolder);
        _sourceCache = sourceCache;
        _sourceDiskGate = sourceDiskGate;
        _sourceWasRead = sourceWasRead;
        _sourceWasSaved = sourceWasSaved;
        _configuration = configuration;
        _owner = owner;
        _kind = kind;
        _downloader = downloader ?? Downloader.Shared;
        _clock = clock ?? TimeProvider.System;
        _log = activityLog ?? ActivityLogger.Shared;
    }

    internal Task<ImageResourceResult> FetchAsync(string url, CancellationToken ct, string? activityDetail = null,
        ImageResourceRequest? request = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var operation = FetchCoreAsync(url, cancellation.Token,
                request ?? new(_owner, _kind(url), activityDetail, _configuration.NetworkAllowsDownloads));
            _operations.Add(operation);
            _ = operation.ContinueWith(completed =>
            {
                cancellation.Dispose();
                lock (_gate) _operations.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return operation;
        }
    }

    private async Task<ImageResourceResult> FetchCoreAsync(string url, CancellationToken ct, ImageResourceRequest request)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Trace.TraceWarning("Skipping a non-web image URL.");
            return new(null);
        }
        if (CachedImage(url) is { } cached && (_sourceCache is null || File.Exists(cached.Path)))
        {
            if (_sourceCache is not null)
                await _sourceCache.ImageWasUsedAsync(url, checksForUpdates: true, ct).ConfigureAwait(false);
            if (_sourceCache is null && !File.Exists(cached.Path))
                await _diskCache.SetAsync(url.Md5Hex(), cached.Data, ct).ConfigureAwait(false);
            return new(cached);
        }
        if (_database.RecentlyFailed(url)) return new(null);
        Task<ImageResourceResult> pending;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_configuration.StoresSourceData && !request.UsesDownloadQueue) _unlimitedPending.Add(url);
            if (!_pending.TryGetValue(url, out pending!))
            {
                pending = Task.Run(async () =>
                {
                    try { return await DownloadImageAsync(url, _lifetime.Token, request).ConfigureAwait(false); }
                    finally
                    {
                        lock (_gate)
                        {
                            _pending.Remove(url);
                            _unlimitedPending.Remove(url);
                        }
                    }
                });
                _pending.Add(url, pending);
            }
        }
        return await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    private byte[] ProcessImage(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Images require the Windows image decoder.");
        if (_configuration.StoresSourceData)
        {
            _ = WindowsImage.LargestFramePixelSize(data);
            return data;
        }
        return WindowsImage.ScaledImageData(data, _configuration.MaxPixelSize);
    }

    private async Task<ImageResourceResult> DownloadImageAsync(string url, CancellationToken ct, ImageResourceRequest request)
    {
        if (_sourceCache is not null)
            return await ImageFromSourceAsync(url, request, ct).ConfigureAwait(false);
        string key = url.Md5Hex();
        string path = _diskCache.PathForKey(key);
        if (_sourceDiskGate is not null) await _sourceDiskGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await ReadFromDiskAsync(url, key, ct).ConfigureAwait(false) is { } cached)
            {
                if (_sourceWasRead is not null) await _sourceWasRead(url, request).ConfigureAwait(false);
                return Cache(cached);
            }
        }
        finally { _sourceDiskGate?.Release(); }
        if (IsWaitingToRetry(url)) return new(null, IsTransient: true);
        if (request.NetworkAllowsDownloads?.Invoke() == false) return new(null, IsTransient: true);
        var limiter = !_configuration.StoresSourceData || request.UsesDownloadQueue
            ? _configuration.DownloadQueue : null;
        using var lease = limiter is not null
            ? await limiter.AcquireAsync(ct, url, group: new Uri(url).IdnHost).ConfigureAwait(false) : null;
        if (limiter is not null && lease is null) return new(null, IsTransient: true);
        ct.ThrowIfCancellationRequested();
        if (lease?.StartedNow != true)
        {
            if (request.NetworkAllowsDownloads?.Invoke() == false)
                return new(null, IsTransient: true);
            if (IsWaitingToRetry(url)) return new(null, IsTransient: true);
        }
        int? activity = request.Owner.Equals(ActivityOwner.ThumbnailDownloader)
            ? null : _log.CreateActivity(request.Owner, request.Kind, request.Detail);
        if (activity is { } startedActivity) _log.DidStart(startedActivity);
        void LogFailure(Exception error)
            => _log.DidFail(activity ?? _log.CreateActivity(request.Owner, request.Kind, request.Detail), error);
        DownloadResult response;
        try
        {
            response = await _downloader.DownloadAsync(url, null, ct, UserAgentStyle.Browser,
                cachesResponse: !_configuration.StoresSourceData).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested) { LogFailure(error); throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            LogFailure(error);
            NoteTransientFailure(url);
            return new(null, IsTransient: true);
        }
        if (!response.Success || response.Data is not { Length: > 0 } bytes)
        {
            bool transient = IsTransientStatus((int)response.StatusCode);
            LogFailure(new HttpRequestException("Image request returned HTTP " + (int)response.StatusCode
                + (response.Success ? " with no image bytes." : "."), null, response.StatusCode));
            if (transient) NoteTransientFailure(url, response.RetryAfter, (int)response.StatusCode);
            else
            {
                ClearRetryDelay(url);
                await _database.RecordFailureAsync(url).ConfigureAwait(false);
                DownloadFailed?.Invoke(this, url);
            }
            return new(null, transient);
        }
        bool oversized;
        lock (_gate)
            oversized = _configuration.StoresSourceData && request.UsesDownloadQueue
                && bytes.LongLength > 25L * 1024 * 1024 && !_unlimitedPending.Contains(url);
        if (oversized)
        {
            LogFailure(new InvalidDataException($"Image too large for a timeline download ({bytes.LongLength} bytes)."));
            ClearRetryDelay(url);
            _database.RecordThumbnailSizeRejection(url);
            DownloadFailed?.Invoke(this, url);
            return new(null, RejectedByteCount: bytes.LongLength);
        }
        byte[] processed;
        try
        {
            ct.ThrowIfCancellationRequested();
            processed = ProcessImage(bytes);
        }
        catch (InvalidDataException error)
        {
            LogFailure(new InvalidDataException($"Could not decode image bytes (HTTP {(int)response.StatusCode}).", error));
            ct.ThrowIfCancellationRequested();
            if (_configuration.StoresSourceData)
            {
                NoteTransientFailure(url, statusCode: (int)response.StatusCode);
                return new(null, IsTransient: true, DecodingFailed: true);
            }
            ClearRetryDelay(url);
            await _database.RecordFailureAsync(url).ConfigureAwait(false);
            DownloadFailed?.Invoke(this, url);
            return new(null);
        }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested)
        {
            LogFailure(error);
            throw;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            await StoreImageAsync(url, processed, response, request.ChecksForUpdates, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            LogFailure(error);
            NoteTransientFailure(url, statusCode: (int)response.StatusCode);
            return new(null, IsTransient: true);
        }
        catch (OperationCanceledException error)
        {
            LogFailure(error);
            throw;
        }
        if (activity is { } completedActivity)
            _log.DidComplete(completedActivity, ActivityLogger.DataSizeMessage(bytes.LongLength), returnedFromCache: response.ReturnedFromCache);
        ClearRetryDelay(url);
        return Cache(new(url, path, processed));
    }

    internal Task StoreUpdatedSourceAsync(string url, byte[] data, DownloadResult response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return StoreImageAsync(url, ProcessImage(data), response, true, ct);
    }

    private async Task StoreImageAsync(string url, byte[] processed, DownloadResult response, bool isIcon, CancellationToken ct)
    {
        if (_sourceDiskGate is not null) await _sourceDiskGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _diskCache.SetAsync(url.Md5Hex(), processed, ct).ConfigureAwait(false);
            if (_configuration.StoresSourceData)
                foreach (var derived in Directory.EnumerateFiles(_diskCache.Folder, url.Md5Hex() + "-*"))
                    _diskCache.Remove(Path.GetFileName(derived));
            if (_configuration.StoresSourceData && OperatingSystem.IsWindows())
                await _database.SaveDownloadedImageAsync(new(url,
                    WindowsImage.LargestFramePixelSize(processed), response.ConditionalGet,
                    DownloadedImageCache.CalculateNextCheck(response.CacheMaxAge,
                        response.ConditionalGet is not null, _clock.GetUtcNow()), isIcon)).ConfigureAwait(false);
            await _database.ClearFailureAsync(url).ConfigureAwait(false);
            if (_sourceWasSaved is not null) await _sourceWasSaved(url).ConfigureAwait(false);
        }
        finally { _sourceDiskGate?.Release(); }
    }

    private async Task<ImageResourceResult> ImageFromSourceAsync(string url, ImageResourceRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Images require the Windows image decoder.");
        var scaled = await _sourceCache!.DerivedImageDataAsync(url, ScaledCopySuffix, ct).ConfigureAwait(false);
        if (scaled is not null)
        {
            try
            {
                _ = WindowsImage.LargestFramePixelSize(scaled);
                await _sourceCache.ImageWasUsedAsync(url, checksForUpdates: true, ct).ConfigureAwait(false);
                return Cache(new(url, _sourceCache.SourcePath(url), scaled));
            }
            catch (InvalidDataException error)
            {
                Trace.TraceWarning($"Could not decode scaled icon {url}; rebuilding from its original: {error}");
                await _sourceCache.DeleteDerivedImageDataAsync(url, ScaledCopySuffix, ct).ConfigureAwait(false);
            }
        }
        var result = await _sourceCache!.FetchAsync(url, request with { ChecksForUpdates = true }, ct).ConfigureAwait(false);
        if (result.Image is not { } source) return result;
        await _sourceCache.ImageWasUsedAsync(url, checksForUpdates: true, ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var image = source with { Data = ProcessImage(source.Data) };
            ct.ThrowIfCancellationRequested();
            if (!image.Data.AsSpan().SequenceEqual(source.Data))
                await _sourceCache.SaveDerivedImageDataAsync(url, ScaledCopySuffix, image.Data, ct).ConfigureAwait(false);
            return Cache(image);
        }
        catch (InvalidDataException error)
        {
            Trace.TraceWarning($"Could not decode stored image {url}: {error.Message}");
            ct.ThrowIfCancellationRequested();
            await _database.RecordFailureAsync(url).ConfigureAwait(false);
        }
        DownloadFailed?.Invoke(this, url);
        return new(null);
    }

    private async Task<DownloadedImage?> ReadFromDiskAsync(string url, string key, CancellationToken ct)
    {
        string destination = _diskCache.PathForKey(key);
        byte[]? data;
        byte[] processed;
        try
        {
            data = await _diskCache.GetAsync(key, ct).ConfigureAwait(false);
            if (data is not { Length: > 0 })
            {
                if (data is not null) _diskCache.Remove(key);
                return null;
            }
            processed = ProcessImage(data);
        }
        catch (InvalidDataException error)
        {
            Trace.TraceWarning($"Ignoring an undecodable cached image {destination}: {error.Message}");
            _diskCache.Remove(key);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not read cached image {destination}: {error.Message}");
            return null;
        }
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(data, processed))
            await _diskCache.SetAsync(key, processed, ct).ConfigureAwait(false);
        return new(url, destination, processed);
    }

    private ImageResourceResult Cache(DownloadedImage image)
    {
        lock (_gate)
        {
            _retryDates.Remove(image.Url);
            if (_configuration.StoresSourceData) return new(image);
            if (_cache.Remove(image.Url, out var previous))
            {
                _cacheOrder.Remove(previous);
                _cacheBytes -= previous.Value.Data.LongLength;
            }
            long limit = _configuration.MemoryCacheByteLimit;
            if (limit > 0 && image.Data.LongLength > limit) return new(image);
            while (limit > 0 && _cacheBytes + image.Data.LongLength > limit && _cacheOrder.First is { } oldest)
            {
                _cacheOrder.RemoveFirst();
                _cache.Remove(oldest.Value.Url);
                _cacheBytes -= oldest.Value.Data.LongLength;
            }
            _cache.Add(image.Url, _cacheOrder.AddLast(image));
            _cacheBytes += image.Data.LongLength;
        }
        return new(image);
    }

    private DownloadedImage? CachedImage(string url)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(url, out var node)) return null;
            _cacheOrder.Remove(node);
            _cacheOrder.AddLast(node);
            return node.Value;
        }
    }

    internal static bool IsTransientStatus(int statusCode) => statusCode is >= 500 and <= 599 or 408 or 429;

    internal bool IsWaitingToRetry(string url)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            return _retryDates.TryGetValue(url, out var retry) && retry > now
                || _hostRetryDates.TryGetValue(new Uri(url).IdnHost, out var hostRetry) && hostRetry > now;
        }
    }

    internal void NoteTransientFailure(string url, TimeSpan? retryAfter = null, int? statusCode = null)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var delay = RetryWait;
            if (retryAfter is { } serverDelay && serverDelay > delay)
            {
                if (serverDelay <= DateTimeOffset.MaxValue - now) delay = serverDelay;
                else Trace.TraceWarning($"Ignoring an unrepresentable image Retry-After interval at {url}.");
            }
            var next = now + delay;
            DateTimeOffset NextDate(DateTimeOffset previous) => previous > next ? previous : next;
            _retryDates.TryGetValue(url, out var previous);
            _retryDates[url] = NextDate(previous);
            if (statusCode is null or 429 or 503)
            {
                string host = new Uri(url).IdnHost;
                _hostRetryDates.TryGetValue(host, out var previousHost);
                _hostRetryDates[host] = NextDate(previousHost);
            }
        }
    }

    private void ClearRetryDelay(string url)
    {
        lock (_gate)
        {
            _retryDates.Remove(url);
            _hostRetryDates.Remove(new Uri(url).IdnHost);
        }
    }

    internal void ResetRetryDelays()
    {
        lock (_gate)
        {
            var latestAutomaticRetry = _clock.GetUtcNow() + RetryWait;
            KeepLongWaits(_retryDates);
            KeepLongWaits(_hostRetryDates);
            void KeepLongWaits(Dictionary<string, DateTimeOffset> dates)
            {
                foreach (var url in dates.Where(pair => pair.Value <= latestAutomaticRetry).Select(pair => pair.Key).ToArray())
                    dates.Remove(url);
            }
        }
    }

    internal void EmptyCache()
    {
        lock (_gate)
        {
            _cache.Clear();
            _cacheOrder.Clear();
            _cacheBytes = 0;
        }
    }

    internal async Task<ImageResourceResult> RefreshAsync(string url, CancellationToken ct)
    {
        Task<ImageResourceResult>? pending;
        lock (_gate) _pending.TryGetValue(url, out pending);
        // An older decode must finish before invalidation, or it can cache stale pixels again.
        if (pending is not null) await pending.WaitAsync(ct).ConfigureAwait(false);
        if (_sourceCache is not null)
            await _sourceCache.DeleteDerivedImageDataAsync(url, ScaledCopySuffix, ct).ConfigureAwait(false);
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_cache.Remove(url, out var previous))
            {
                _cacheOrder.Remove(previous);
                _cacheBytes -= previous.Value.Data.LongLength;
            }
        }
        return await FetchAsync(url, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _stopping = true;
            _lifetime.Cancel();
            return new(_shutdown = DrainAsync(_pending.Values.Cast<Task>().Concat(_operations).ToArray()));
        }
    }

    private async Task DrainAsync(Task[] tasks)
    {
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { EmptyCache(); _lifetime.Dispose(); }
    }
}
