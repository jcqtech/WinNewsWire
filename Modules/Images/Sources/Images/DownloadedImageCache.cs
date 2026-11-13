using System.Diagnostics;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

/// <summary>One disk source per URL; display-size representations stay in their owners' memory caches.</summary>
public sealed class DownloadedImageCache : IAsyncDisposable
{
    public const int MaxPixelSize = 1200;
    private readonly ImageResourceDownloader _downloads;
    private readonly ImageMetadataDatabase _database;
    private readonly BinaryDiskCache _diskCache;
    private readonly TimeProvider _clock;
    private readonly ByteLimit _limit;
    private readonly ByteLimit _hardLimit;
    // Pruning and source read/save callbacks hold this gate; network requests stay concurrent.
    private readonly SemaphoreSlim _diskGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _accessDatesSaved = new(StringComparer.Ordinal);
    private readonly Task _initialCleanup;
    private long? _totalByteCount;
    private long _removalByteCount;

    internal event EventHandler<string>? DownloadFailed
    {
        add => _downloads.DownloadFailed += value;
        remove => _downloads.DownloadFailed -= value;
    }

    public DownloadedImageCache(ImageMetadataDatabase database, string cacheFolder,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null)
        : this(database, cacheFolder, 300L * 1024 * 1024, 1024L * 1024 * 1024, downloader, clock, activityLog)
    {
    }

    internal DownloadedImageCache(ImageMetadataDatabase database, string cacheFolder,
        long maximumByteCount, long hardMaximumByteCount,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumByteCount);
        if (hardMaximumByteCount < maximumByteCount) throw new ArgumentOutOfRangeException(nameof(hardMaximumByteCount));
        _database = database;
        _clock = clock ?? TimeProvider.System;
        _limit = new(maximumByteCount);
        _hardLimit = new(hardMaximumByteCount);
        _removalByteCount = maximumByteCount;
        _diskCache = new(Path.Combine(cacheFolder, "DownloadedImages"));
        _downloads = new(database, _diskCache.Folder,
            new(MaxPixelSize, ScaledImageFormat.Jpeg, DownloadLimiter: new DownloadLimiter(4), StoresSourceData: true),
            ActivityOwner.FeedImageDownloader, ActivityKind.DownloadFeedImage, downloader, clock, activityLog,
            sourceDiskGate: _diskGate, sourceWasRead: SaveAccessDateIfNeededAsync, sourceWasSaved: ImageWasSavedAsync);
        _initialCleanup = Task.Run(RemoveImagesOverSizeLimitAsync);
    }

    internal async Task<ImageResourceResult> FetchAsync(string url, ImageResourceRequest request, CancellationToken ct)
    {
        await _initialCleanup.WaitAsync(ct).ConfigureAwait(false);
        return await _downloads.FetchAsync(url, ct, request: request).ConfigureAwait(false);
    }

    private async Task SaveAccessDateIfNeededAsync(string url)
    {
        var now = _clock.GetUtcNow();
        if (_accessDatesSaved.TryGetValue(url, out var saved) && now - saved < TimeSpan.FromDays(1)) return;
        await _database.SaveDownloadedImageAccessDateAsync(url).ConfigureAwait(false);
        _accessDatesSaved[url] = now;
    }

    private Task ImageWasSavedAsync(string url, long byteCount)
    {
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
        var removal = PlanRemoval(records, dates, now, _limit, _hardLimit);
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
        IReadOnlyDictionary<string, DateTimeOffset> fileDates, DateTimeOffset now, ByteLimit limit, ByteLimit hardLimit)
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
        var keys = fileDates.Where(pair => !recordedKeys.Contains(pair.Key) && pair.Value < recentChange)
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        long bytes = keep.Sum(item => item.Record.ByteCount);
        void RemoveLeastRecentlyUsed(DateTimeOffset accessedBefore, ByteLimit target)
        {
            if (bytes <= target.MaximumByteCount) return;
            foreach (var (record, key) in keep)
            {
                if (bytes <= target.ByteCountAfterRemoval || record.LastAccessed >= accessedBefore) break;
                if (keys.Contains(key) || fileDates.TryGetValue(key, out var modified) && modified >= recentChange) continue;
                urls.Add(record.Url);
                keys.Add(key);
                bytes -= record.ByteCount;
            }
        }
        RemoveLeastRecentlyUsed(recentUse, limit);
        RemoveLeastRecentlyUsed(recentChange, hardLimit);
        return new(urls, keys, bytes);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _downloads.DisposeAsync().ConfigureAwait(false); }
        finally { await _initialCleanup.ConfigureAwait(false); }
    }
}
