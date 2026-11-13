using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Parsers;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

internal sealed record ImageResourceResult(DownloadedImage? Image, bool IsTransient = false);
internal sealed record ImageResourceConfiguration(int MaxPixelSize, ScaledImageFormat Format = ScaledImageFormat.Png,
    long MemoryCacheByteLimit = 0, bool RequiresSignificantDimensions = false,
    DownloadLimiter? DownloadLimiter = null, Func<bool>? NetworkAllowsDownloads = null,
    bool StoresSourceData = false, bool KeepsDiskCache = false);
internal sealed record ImageResourceRequest(ActivityOwner Owner, ActivityKind Kind, string? Detail,
    Func<bool>? NetworkAllowsDownloads);

internal sealed class ImageResourceDownloader : IAsyncDisposable
{
    private readonly ImageMetadataDatabase _database;
    private readonly BinaryDiskCache _diskCache;
    private readonly string? _legacyFolder;
    private readonly DownloadedImageCache? _sourceCache;
    private readonly ImageResourceConfiguration _configuration;
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
    private readonly Dictionary<string, LinkedListNode<DownloadedImage>> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<DownloadedImage> _cacheOrder = [];
    private long _cacheBytes;
    private bool _stopping;
    private Task? _shutdown;
    internal event EventHandler<string>? DownloadFailed;

    internal ImageResourceDownloader(ImageMetadataDatabase database, string cacheFolder, ImageResourceConfiguration configuration,
        ActivityOwner owner, Func<string, ActivityKind> kind, Downloader? downloader = null,
        TimeProvider? clock = null, ActivityLogger? activityLog = null, string? legacyCacheFolder = null,
        DownloadedImageCache? sourceCache = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuration.MaxPixelSize);
        ArgumentOutOfRangeException.ThrowIfNegative(configuration.MemoryCacheByteLimit);
        _database = database;
        _diskCache = new(cacheFolder);
        _legacyFolder = legacyCacheFolder;
        _sourceCache = sourceCache;
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
        if (CachedImage(url) is { } cached)
        {
            if ((_sourceCache is null || _configuration.KeepsDiskCache) && !File.Exists(cached.Path))
                await _diskCache.SetAsync(url.Md5Hex(), cached.Data, ct).ConfigureAwait(false);
            return new(cached);
        }
        if (_database.RecentlyFailed(url)) return new(null);
        if (_configuration.RequiresSignificantDimensions && _database.RecentlyLackedSignificantDimensions(url)) return new(null);
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
                    try { return await DownloadImageAsync(url, _lifetime.Token, request).ConfigureAwait(false); }
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
        if (_configuration.RequiresSignificantDimensions)
        {
            var dimensions = WindowsImage.LargestFramePixelSize(data);
            if (!SignificantImageFilter.HasSignificantDimensions(dimensions.Width, dimensions.Height))
                throw new InsignificantDimensionsException(dimensions.Width, dimensions.Height);
        }
        return WindowsImage.ScaledImageData(data, _configuration.MaxPixelSize, _configuration.Format);
    }

    private async Task<ImageResourceResult> DownloadImageAsync(string url, CancellationToken ct, ImageResourceRequest request)
    {
        if (_sourceCache is not null && !_configuration.KeepsDiskCache)
            return await ImageFromSourceAsync(url, request, ct).ConfigureAwait(false);
        string key = url.Md5Hex();
        string path = _diskCache.PathForKey(key);
        if (await ReadFromDiskAsync(url, key, ct).ConfigureAwait(false) is { } cached)
            return Cache(cached);
        if (_configuration.RequiresSignificantDimensions && _database.RecentlyLackedSignificantDimensions(url)) return new(null);
        if (_sourceCache is not null)
        {
            var result = await ImageFromSourceAsync(url, request, ct).ConfigureAwait(false);
            if (result.Image is not { } image) return result;
            await _diskCache.SetAsync(key, image.Data, ct).ConfigureAwait(false);
            return Cache(image with { Path = path });
        }
        if (request.NetworkAllowsDownloads?.Invoke() == false) return new(null, IsTransient: true);
        using var lease = _configuration.DownloadLimiter is { } limiter
            && (!_configuration.StoresSourceData || request.NetworkAllowsDownloads is not null)
            ? await limiter.AcquireAsync(ct).ConfigureAwait(false) : null;
        ct.ThrowIfCancellationRequested();
        if (request.NetworkAllowsDownloads?.Invoke() == false) return new(null, IsTransient: true);
        int activity = _log.CreateActivity(request.Owner, request.Kind, request.Detail);
        _log.DidStart(activity);
        DownloadResult response;
        try
        {
            response = await _downloader.DownloadAsync(url, null, ct, UserAgentStyle.Browser,
                cachesResponse: !_configuration.StoresSourceData).ConfigureAwait(false);
        }
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
            else
            {
                await _database.RecordFailureAsync(url, (int)response.StatusCode).ConfigureAwait(false);
                DownloadFailed?.Invoke(this, url);
            }
            return new(null, transient);
        }
        byte[] processed;
        try
        {
            ct.ThrowIfCancellationRequested();
            processed = ProcessImage(bytes);
        }
        catch (InsignificantDimensionsException error)
        {
            _log.DidFail(activity, error);
            ct.ThrowIfCancellationRequested();
            await _database.RecordInsignificantDimensionsAsync(url).ConfigureAwait(false);
            DownloadFailed?.Invoke(this, url);
            return new(null);
        }
        catch (InvalidDataException error)
        {
            _log.DidFail(activity, new InvalidDataException($"Could not decode image bytes (HTTP {(int)response.StatusCode}).", error));
            ct.ThrowIfCancellationRequested();
            await _database.RecordFailureAsync(url, (int)response.StatusCode).ConfigureAwait(false);
            DownloadFailed?.Invoke(this, url);
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

    private async Task<ImageResourceResult> ImageFromSourceAsync(string url, ImageResourceRequest request, CancellationToken ct)
    {
        var result = await _sourceCache!.FetchAsync(url, request, ct).ConfigureAwait(false);
        if (result.Image is not { } source) return result;
        try
        {
            ct.ThrowIfCancellationRequested();
            var image = source with { Data = ProcessImage(source.Data) };
            ct.ThrowIfCancellationRequested();
            return _configuration.KeepsDiskCache ? new(image) : Cache(image);
        }
        catch (InsignificantDimensionsException error)
        {
            Trace.TraceWarning($"Rejecting thumbnail {url}: {error.Message}");
            ct.ThrowIfCancellationRequested();
            await _database.RecordInsignificantDimensionsAsync(url).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            Trace.TraceWarning($"Could not decode stored image {url}: {error.Message}");
            ct.ThrowIfCancellationRequested();
            await _database.RecordFailureAsync(url, null).ConfigureAwait(false);
        }
        DownloadFailed?.Invoke(this, url);
        return new(null);
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
                if (data is not { Length: > 0 })
                {
                    if (path == destination && data is not null) _diskCache.Remove(key);
                    continue;
                }
                processed = ProcessImage(data);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (InsignificantDimensionsException error)
            {
                Trace.TraceWarning($"Ignoring an unsuitable cached thumbnail {path}: {error.Message}");
                if (path == destination) _diskCache.Remove(key);
                await _database.RecordInsignificantDimensionsAsync(url).ConfigureAwait(false);
                DownloadFailed?.Invoke(this, url);
                return null;
            }
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
        lock (_gate)
        {
            _transientAttempts.Remove(image.Url);
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

    private IEnumerable<string> LegacyCachePaths(string url)
    {
        if (_legacyFolder is null) yield break;
        string extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".svg" or ".ico" or ".gif" or ".webp" or ".bmp")) extension = ".image";
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        yield return Path.Combine(_legacyFolder, key + (extension == ".svg" ? ".image" : extension));
        if (extension == ".svg") yield return Path.Combine(_legacyFolder, key + extension);
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

    private sealed class InsignificantDimensionsException(uint width, uint height)
        : Exception($"Image too small or too narrow ({width}x{height}).");
}
