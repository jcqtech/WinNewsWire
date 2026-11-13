using System.Diagnostics;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

/// <summary>One original, full-size disk source per URL; display-size representations stay in their owners' memory caches.</summary>
public sealed class DownloadedImageCache : IAsyncDisposable
{
    private readonly ImageResourceDownloader _downloads;
    private readonly ImageDatabase _database;
    private readonly BinaryDiskCache _diskCache;
    private readonly TimeProvider _clock;
    private readonly ByteLimit _limit;
    private readonly ByteLimit _hardLimit;
    private readonly Downloader _downloader;
    private readonly ThumbnailTaskQueue _downloadQueue = new(4, 60);
    private readonly Func<bool> _networkAllowsUpdates;
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly Dictionary<string, int> _revalidatingImages = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _stopping;
    private Task? _shutdown;
    // Pruning and source read/save callbacks hold this gate; network requests stay concurrent.
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _accessDatesSaved = new(StringComparer.Ordinal);
    private readonly Task _initialCleanup;
    private long? _totalByteCount;
    private long _removalByteCount;
    internal int WaitingDownloadCount => _downloadQueue.WaitingCount;

    /// <summary>Raised on a worker after a validated server replacement is saved; UI consumers must dispatch.</summary>
    public event EventHandler<string>? DownloadedImageChanged;

    internal event EventHandler<string>? DownloadFailed
    {
        add => _downloads.DownloadFailed += value;
        remove => _downloads.DownloadFailed -= value;
    }

    public DownloadedImageCache(ImageDatabase database, string cacheFolder,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null,
        Func<bool>? networkAllowsUpdates = null)
        : this(database, cacheFolder, 300L * 1024 * 1024, 1024L * 1024 * 1024,
            downloader, clock, activityLog, networkAllowsUpdates)
    {
    }

    internal DownloadedImageCache(ImageDatabase database, string cacheFolder,
        long maximumByteCount, long hardMaximumByteCount,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null,
        Func<bool>? networkAllowsUpdates = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumByteCount);
        if (hardMaximumByteCount < maximumByteCount) throw new ArgumentOutOfRangeException(nameof(hardMaximumByteCount));
        _database = database;
        _clock = clock ?? TimeProvider.System;
        _downloader = downloader ?? Downloader.Shared;
        _networkAllowsUpdates = networkAllowsUpdates ?? (() => NetworkMonitor.IsAvailable);
        _limit = new(maximumByteCount);
        _hardLimit = new(hardMaximumByteCount);
        _removalByteCount = maximumByteCount;
        _diskCache = new(Path.Combine(cacheFolder, "DownloadedImages"));
        _downloads = new(database, _diskCache.Folder,
            new(int.MaxValue, DownloadQueue: _downloadQueue, StoresSourceData: true),
            ActivityOwner.FeedImageDownloader, ActivityKind.DownloadFeedImage, _downloader, clock, activityLog,
            sourceDiskGate: _diskGate, sourceWasRead: SaveAccessDateIfNeededAsync, sourceWasSaved: ImageWasSavedAsync);
        NetworkMonitor.ConnectivityChanged += NetworkStatusChanged;
        _initialCleanup = Task.Run(RemoveImagesOverSizeLimitAsync);
    }

    internal void NetworkStatusChanged(object? sender, bool available) => _downloads.ResetRetryDelays();
    internal bool ReturnedNonImage(string url) => _downloads.ReturnedNonImage(url);

    internal async Task<ImageResourceResult> FetchAsync(string url, ImageResourceRequest request, CancellationToken ct)
    {
        lock (_backgroundGate) ObjectDisposedException.ThrowIf(_stopping, this);
        await _initialCleanup.WaitAsync(ct).ConfigureAwait(false);
        if (request.DownloadPriority == ThumbnailPriority.Visible) PrioritizeDownload(url);
        else if (request.DownloadPriority is null) _downloadQueue.StartNow(url);
        var result = await _downloads.FetchAsync(url, ct, request: request).ConfigureAwait(false);
        // A reader may join just after a limited operation decides to reject its response.
        if (request.DownloadPriority is null && result.RejectedByteCount is not null)
            return await _downloads.FetchAsync(url, ct, request: request).ConfigureAwait(false);
        return result;
    }

    private void ReleaseRevalidation(string url)
    {
        lock (_backgroundGate)
        {
            int remaining = _revalidatingImages[url] - 1;
            if (remaining == 0) _revalidatingImages.Remove(url);
            else _revalidatingImages[url] = remaining;
        }
    }

    internal void PrioritizeDownload(string url) => _downloadQueue.Prioritize(url);

    internal Task ImageWasUsedAsync(string url, bool checksForUpdates, CancellationToken ct)
        => RunDiskOperationAsync(async _ =>
        {
            await SaveAccessDateIfNeededAsync(url, checksForUpdates).ConfigureAwait(false);
            return true;
        }, ct);

    internal static string DerivedDiskKey(string url, string suffix) => url.Md5Hex() + "-" + suffix;
    internal string SourcePath(string url) => _diskCache.PathForKey(url.Md5Hex());

    private static string BaseDiskKey(string key)
    {
        int separator = key.IndexOf('-');
        return separator >= 0 ? key[..separator] : key;
    }

    internal Task<byte[]?> DerivedImageDataAsync(string url, string suffix, CancellationToken ct = default)
        => RunDiskOperationAsync(token => File.Exists(_diskCache.PathForKey(url.Md5Hex()))
            ? _diskCache.GetAsync(DerivedDiskKey(url, suffix), token) : Task.FromResult<byte[]?>(null), ct);

    internal Task SaveDerivedImageDataAsync(string url, string suffix, byte[] data, CancellationToken ct = default)
        => RunDiskOperationAsync(async token =>
        {
            if (File.Exists(_diskCache.PathForKey(url.Md5Hex())))
                await _diskCache.SetAsync(DerivedDiskKey(url, suffix), data, token).ConfigureAwait(false);
            return true;
        }, ct);

    internal Task DeleteDerivedImageDataAsync(string url, string suffix, CancellationToken ct = default)
        => RunDiskOperationAsync(_ =>
        {
            _diskCache.Remove(DerivedDiskKey(url, suffix));
            return Task.FromResult(true);
        }, ct);

    private Task<T> RunDiskOperationAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        lock (_backgroundGate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var task = Task.Run(async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                await _initialCleanup.WaitAsync(cancellation.Token).ConfigureAwait(false);
                await _diskGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try { return await work(cancellation.Token).ConfigureAwait(false); }
                finally { _diskGate.Release(); }
            });
            _backgroundTasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted) Trace.TraceError($"Image cache maintenance failed: {completed.Exception}");
                lock (_backgroundGate) _backgroundTasks.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private Task SaveAccessDateIfNeededAsync(string url, ImageResourceRequest request)
        => SaveAccessDateIfNeededAsync(url, request.ChecksForUpdates);

    private async Task SaveAccessDateIfNeededAsync(string url, bool checksForUpdates)
    {
        var now = _clock.GetUtcNow();
        if (_accessDatesSaved.TryGetValue(url, out var saved) && now - saved < TimeSpan.FromDays(1)) return;
        await _database.SaveDownloadedImageAccessDateAsync(url).ConfigureAwait(false);
        if (_accessDatesSaved.Count >= 10_000) _accessDatesSaved.Clear();
        _accessDatesSaved[url] = now;
        if (checksForUpdates) ScheduleRevalidation(url);
    }

    private void ScheduleRevalidation(string url)
    {
        lock (_backgroundGate)
        {
            if (_stopping) return;
            var task = Task.Run(() => RevalidateIfDueAsync(url, _lifetime.Token));
            _backgroundTasks.Add(task);
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted) Trace.TraceError($"Image revalidation failed: {completed.Exception}");
                lock (_backgroundGate) _backgroundTasks.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal Task WaitForRevalidationsAsync()
    {
        lock (_backgroundGate) return Task.WhenAll(_backgroundTasks.ToArray());
    }

    private async Task RevalidateIfDueAsync(string url, CancellationToken ct)
    {
        bool inUse = false;
        try
        {
            var validation = await _database.FetchDownloadedImageValidationAsync(url).ConfigureAwait(false);
            if (validation is null || validation.NextCheck > _clock.GetUtcNow()
                || !_networkAllowsUpdates() || _downloads.IsWaitingToRetry(url)) return;
            ct.ThrowIfCancellationRequested();
            lock (_backgroundGate) _revalidatingImages[url] = _revalidatingImages.GetValueOrDefault(url) + 1;
            inUse = true;
            var response = await _downloader.DownloadAsync(url, validation.ConditionalGet, ct,
                UserAgentStyle.Browser, cachesResponse: false).ConfigureAwait(false);
            if (response.Success && response.Data is { Length: > 0 } data)
            {
                try { await _downloads.StoreUpdatedSourceAsync(url, data, response, ct).ConfigureAwait(false); }
                catch (InvalidDataException error)
                {
                    Trace.TraceWarning($"Image revalidation for {url} returned unreadable data; keeping the cached image: {error}");
                    await _database.SaveDownloadedImageNextCheckAsync(url,
                        CalculateNextCheck(response.CacheMaxAge, validation.ConditionalGet is not null, _clock.GetUtcNow()))
                        .ConfigureAwait(false);
                    return;
                }
                Trace.TraceInformation($"Replaced changed downloaded image at {url}.");
                DownloadedImageChanged?.Invoke(this, url);
                return;
            }
            if (ImageResourceDownloader.IsTransientStatus((int)response.StatusCode))
                _downloads.NoteTransientFailure(url, response.RetryAfter, (int)response.StatusCode);
            if (!response.NotModified)
                Trace.TraceWarning($"Image revalidation for {url} returned HTTP {(int)response.StatusCode}; keeping the cached image.");
            await _database.SaveDownloadedImageNextCheckAsync(url,
                CalculateNextCheck(response.CacheMaxAge, validation.ConditionalGet is not null, _clock.GetUtcNow()))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
            or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
        {
            Trace.TraceWarning($"Image revalidation for {url} failed; keeping the cached image: {error}");
        }
        finally { if (inUse) ReleaseRevalidation(url); }
    }

    internal static DateTimeOffset CalculateNextCheck(TimeSpan? maxAge, bool hasValidators, DateTimeOffset now)
    {
        var interval = !hasValidators ? TimeSpan.FromDays(30) : maxAge is { } age
            ? TimeSpan.FromTicks(Math.Clamp(age.Ticks, TimeSpan.FromDays(3).Ticks, TimeSpan.FromDays(30).Ticks))
            : TimeSpan.FromDays(8);
        return now + TimeSpan.FromTicks((long)(interval.Ticks * (0.8 + Random.Shared.NextDouble() * 0.4)));
    }

    private Task ImageWasSavedAsync(string url, long byteCount)
    {
        if (_accessDatesSaved.Count >= 10_000) _accessDatesSaved.Clear();
        _accessDatesSaved[url] = _clock.GetUtcNow();
        if (_totalByteCount is { } total) _totalByteCount = checked(total + byteCount);
        return _totalByteCount is null || _totalByteCount > _removalByteCount
            ? RemoveImagesOverSizeLimitCoreAsync() : Task.CompletedTask;
    }

    internal async Task RemoveImagesOverSizeLimitAsync()
    {
        await _diskGate.WaitAsync().ConfigureAwait(false);
        try { await RemoveImagesOverSizeLimitCoreAsync().ConfigureAwait(false); }
        finally { _diskGate.Release(); }
    }

    private async Task RemoveImagesOverSizeLimitCoreAsync()
    {
        _totalByteCount = null;
        var now = _clock.GetUtcNow();
        var records = await _database.FetchDownloadedImageUsageByLeastRecentAccessAsync().ConfigureAwait(false);
        var dates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var directory = new DirectoryInfo(_diskCache.Folder);
        FileAttributes? attributes = null;
        try { attributes = File.GetAttributes(directory.FullName); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        if (attributes is { } existingAttributes)
        {
            if ((existingAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Image cache cleanup will not traverse a filesystem link: " + directory.FullName);
            foreach (var file in directory.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Image cache cleanup will not traverse a filesystem link: " + file.FullName);
                dates[file.Name] = file.LastWriteTimeUtc;
            }
        }
        var urlsInUse = _downloads.CopyPendingUrls();
        lock (_backgroundGate) urlsInUse.UnionWith(_revalidatingImages.Keys);
        var removal = PlanRemoval(records, dates, now, _limit, _hardLimit, urlsInUse);
        foreach (var key in removal.Keys) _diskCache.Remove(key);
        await _database.DeleteDownloadedImagesAsync(removal.Urls).ConfigureAwait(false);
        foreach (var url in removal.Urls) _accessDatesSaved.Remove(url);
        _totalByteCount = removal.RemainingByteCount;
        _removalByteCount = Math.Max(_limit.MaximumByteCount,
            checked(removal.RemainingByteCount + _limit.MaximumByteCount / 10));
        if (removal.Keys.Count > 0 || removal.Urls.Count > 0)
            Trace.TraceInformation($"Removed {removal.Keys.Count} downloaded image files and {removal.Urls.Count} records; {removal.RemainingByteCount} bytes remain.");
    }

    internal readonly record struct ByteLimit(long MaximumByteCount)
    {
        internal long ByteCountAfterRemoval => MaximumByteCount / 5 * 4;
    }

    internal sealed record ImageRemoval(IReadOnlyList<string> Urls, IReadOnlySet<string> Keys, long RemainingByteCount);

    internal static ImageRemoval PlanRemoval(IReadOnlyList<DownloadedImageUsage> records,
        IReadOnlyDictionary<string, DateTimeOffset> fileDates, DateTimeOffset now, ByteLimit limit, ByteLimit hardLimit,
        IReadOnlySet<string>? urlsInUse = null)
    {
        var recentChange = now - TimeSpan.FromMinutes(1);
        var recentUse = now - TimeSpan.FromDays(3);
        var keep = new List<(DownloadedImageUsage Record, string Key)>();
        var recordedKeys = new HashSet<string>(StringComparer.Ordinal);
        var urls = new List<string>();
        foreach (var record in records)
        {
            string key = record.Url.Md5Hex();
            recordedKeys.Add(key);
            if (!fileDates.ContainsKey(key) && record.LastAccessed < recentChange) urls.Add(record.Url);
            else keep.Add((record, key));
        }
        var keys = fileDates.Where(pair => !recordedKeys.Contains(BaseDiskKey(pair.Key)) && pair.Value < recentChange)
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        long bytes = keep.Sum(item => item.Record.ByteCount);
        void RemoveLeastRecentlyUsed(DateTimeOffset accessedBefore, ByteLimit target)
        {
            if (bytes <= target.MaximumByteCount) return;
            foreach (var (record, key) in keep)
            {
                if (bytes <= target.ByteCountAfterRemoval || record.LastAccessed >= accessedBefore) break;
                if (keys.Contains(key) || urlsInUse?.Contains(record.Url) == true
                    || fileDates.TryGetValue(key, out var modified) && modified >= recentChange) continue;
                urls.Add(record.Url);
                keys.Add(key);
                bytes -= record.ByteCount;
            }
        }
        RemoveLeastRecentlyUsed(recentUse, limit);
        RemoveLeastRecentlyUsed(recentChange, hardLimit);
        var removedKeys = keys.Concat(urls.Select(url => url.Md5Hex())).ToHashSet(StringComparer.Ordinal);
        keys.UnionWith(fileDates.Keys.Where(key => BaseDiskKey(key) != key && removedKeys.Contains(BaseDiskKey(key))));
        return new(urls, keys, bytes);
    }

    public ValueTask DisposeAsync()
    {
        lock (_backgroundGate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _stopping = true;
            NetworkMonitor.ConnectivityChanged -= NetworkStatusChanged;
            _lifetime.Cancel();
            return new(_shutdown = DrainAsync(_backgroundTasks.ToArray()));
        }
    }

    private async Task DrainAsync(Task[] backgroundTasks)
    {
        try { await _downloads.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await Task.WhenAll(backgroundTasks.Append(_initialCleanup)).ConfigureAwait(false); }
            finally { _lifetime.Dispose(); }
        }
    }
}
