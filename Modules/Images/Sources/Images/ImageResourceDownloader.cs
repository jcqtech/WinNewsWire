using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

internal sealed record ImageResourceResult(DownloadedImage? Image, bool IsTransient = false);

internal sealed class ImageResourceDownloader : IAsyncDisposable
{
    private readonly ImageMetadataDatabase _database;
    private readonly BinaryDiskCache _diskCache;
    private readonly string? _legacyFolder;
    private readonly int _maxPixelSize;
    private readonly Downloader _downloader;
    private readonly TimeProvider _clock;
    private readonly ActivityLogger _log;
    private readonly ActivityOwner _owner;
    private readonly Func<string, ActivityKind> _kind;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Task<ImageResourceResult>> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _operations = [];
    private readonly Dictionary<string, DateTimeOffset> _transientAttempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DownloadedImage> _cache = new(StringComparer.Ordinal);
    private bool _stopping;
    private Task? _shutdown;

    internal ImageResourceDownloader(ImageMetadataDatabase database, string cacheFolder, int maxPixelSize,
        ActivityOwner owner, Func<string, ActivityKind> kind, Downloader? downloader = null,
        TimeProvider? clock = null, ActivityLogger? activityLog = null, string? legacyCacheFolder = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPixelSize);
        _database = database;
        _diskCache = new(cacheFolder);
        _legacyFolder = legacyCacheFolder;
        _maxPixelSize = maxPixelSize;
        _owner = owner;
        _kind = kind;
        _downloader = downloader ?? Downloader.Shared;
        _clock = clock ?? TimeProvider.System;
        _log = activityLog ?? ActivityLogger.Shared;
    }

    internal Task<ImageResourceResult> FetchAsync(string url, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var operation = FetchCoreAsync(url, cancellation.Token);
            _operations.Add(operation);
            _ = operation.ContinueWith(completed =>
            {
                cancellation.Dispose();
                lock (_gate) _operations.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return operation;
        }
    }

    private async Task<ImageResourceResult> FetchCoreAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Trace.TraceWarning("Skipping a non-web image URL.");
            return new(null);
        }
        if (_cache.TryGetValue(url, out var cached))
        {
            if (!File.Exists(cached.Path))
                await _diskCache.SetAsync(url.Md5Hex(), cached.Data, ct).ConfigureAwait(false);
            return new(cached);
        }
        if (_database.RecentlyFailed(url)) return new(null);
        Task<ImageResourceResult> pending;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_pending.TryGetValue(url, out pending!))
            {
                if (_transientAttempts.TryGetValue(url, out var attempted) && _clock.GetUtcNow() - attempted < TimeSpan.FromMinutes(30))
                    return new(null, IsTransient: true);
                pending = Task.Run(async () =>
                {
                    try { return await DownloadImageAsync(url, _lifetime.Token).ConfigureAwait(false); }
                    finally { lock (_gate) _pending.Remove(url); }
                });
                _pending.Add(url, pending);
            }
        }
        return await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    private byte[] ProcessImage(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Images require the Windows image decoder.");
        return WindowsImage.ScaledImageData(data, _maxPixelSize);
    }

    private async Task<ImageResourceResult> DownloadImageAsync(string url, CancellationToken ct)
    {
        string key = url.Md5Hex();
        string path = _diskCache.PathForKey(key);
        if (await ReadFromDiskAsync(url, key, ct).ConfigureAwait(false) is { } cached)
            return Cache(cached);
        int activity = _log.CreateActivity(_owner, _kind(url));
        _log.DidStart(activity);
        DownloadResult response;
        try { response = await _downloader.DownloadAsync(url, null, ct, UserAgentStyle.Browser).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested) { _log.DidFail(activity, error); throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            _log.DidFail(activity, error);
            lock (_gate) _transientAttempts[url] = _clock.GetUtcNow();
            return new(null, IsTransient: true);
        }
        if (!response.Success || response.Data is not { Length: > 0 } bytes)
        {
            bool transient = (int)response.StatusCode is >= 500 and <= 599;
            _log.DidFail(activity, new HttpRequestException("Image request returned HTTP " + (int)response.StatusCode
                + (response.Success ? " with no image bytes." : "."), null, response.StatusCode));
            if (transient) { lock (_gate) _transientAttempts[url] = _clock.GetUtcNow(); }
            else await _database.RecordFailureAsync(url, (int)response.StatusCode).ConfigureAwait(false);
            return new(null, transient);
        }
        byte[] processed;
        try
        {
            ct.ThrowIfCancellationRequested();
            processed = ProcessImage(bytes);
        }
        catch (InvalidDataException error)
        {
            _log.DidFail(activity, new InvalidDataException($"Could not decode image bytes (HTTP {(int)response.StatusCode}).", error));
            ct.ThrowIfCancellationRequested();
            await _database.RecordFailureAsync(url, (int)response.StatusCode).ConfigureAwait(false);
            return new(null);
        }
        catch (OperationCanceledException error) when (ct.IsCancellationRequested)
        {
            _log.DidFail(activity, error);
            throw;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            await _diskCache.SetAsync(key, processed, ct).ConfigureAwait(false);
            await _database.ClearFailureAsync(url).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
        {
            _log.DidFail(activity, error);
            throw;
        }
        _log.DidComplete(activity, ActivityLogger.DataSizeMessage(bytes.LongLength), returnedFromCache: response.ReturnedFromCache);
        return Cache(new(url, path, processed));
    }

    private async Task<DownloadedImage?> ReadFromDiskAsync(string url, string key, CancellationToken ct)
    {
        string destination = _diskCache.PathForKey(key);
        foreach (string path in LegacyCachePaths(url).Prepend(destination))
        {
            byte[]? data;
            byte[] processed;
            try
            {
                data = path == destination ? await _diskCache.GetAsync(key, ct).ConfigureAwait(false)
                    : await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                if (data is null) continue;
                processed = ProcessImage(data);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (InvalidDataException error)
            {
                Trace.TraceWarning($"Ignoring an undecodable cached image {path}: {error.Message}");
                if (path == destination) _diskCache.Remove(key);
                continue;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not read cached image {path}: {error.Message}");
                continue;
            }
            ct.ThrowIfCancellationRequested();
            if (path != destination || !ReferenceEquals(data, processed))
                await _diskCache.SetAsync(key, processed, ct).ConfigureAwait(false);
            return new(url, destination, processed);
        }
        return null;
    }

    private ImageResourceResult Cache(DownloadedImage image)
    {
        _cache[image.Url] = image;
        lock (_gate) _transientAttempts.Remove(image.Url);
        return new(image);
    }

    private IEnumerable<string> LegacyCachePaths(string url)
    {
        if (_legacyFolder is null) yield break;
        string extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".svg" or ".ico" or ".gif" or ".webp" or ".bmp")) extension = ".image";
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        yield return Path.Combine(_legacyFolder, key + (extension == ".svg" ? ".image" : extension));
        if (extension == ".svg") yield return Path.Combine(_legacyFolder, key + extension);
    }

    internal void EmptyCache() => _cache.Clear();

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
        finally { _cache.Clear(); _lifetime.Dispose(); }
    }
}
