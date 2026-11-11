using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.HTMLMetadata;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

public sealed record FeedImageSource(string FeedUrl, string? HomePageUrl = null, string? FaviconUrl = null, string? IconUrl = null);
public sealed record DownloadedImage(string Url, string Path, byte[] Data);

public sealed class FeedImageDownloader : IAsyncDisposable
{
    private static readonly Lazy<FeedImageDownloader> SharedDownloader = new(() =>
        new(ImageMetadataDatabase.Shared, HtmlMetadataDownloader.Shared, AppConfig.CachesDirectory,
            legacyCacheFolder: Path.Combine(AppConfig.DataDirectory, "Favicons")));
    // Opening and maintaining the metadata databases must not happen on a UI caller's first image request.
    private static readonly Lazy<Task<FeedImageDownloader>> SharedInitialization = new(() => Task.Run(() => Shared));
    public static FeedImageDownloader Shared => SharedDownloader.Value;
    public static bool IsSharedCreated => SharedDownloader.IsValueCreated;
    public static Task<FeedImageDownloader> GetSharedAsync() => SharedInitialization.Value;

    public static async ValueTask DisposeSharedAsync()
    {
        if (SharedInitialization.IsValueCreated)
        {
            FeedImageDownloader service;
            try { service = await SharedInitialization.Value.ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
            {
                Trace.TraceError($"Image service initialization failed before shutdown: {error}");
                return;
            }
            await service.DisposeAsync().ConfigureAwait(false);
        }
        else if (IsSharedCreated)
            await Shared.DisposeAsync().ConfigureAwait(false);
    }

    private static readonly string[] IgnoredFeedIconHosts =
        ["propublica.org", "clubic.com", "comptoir-hardware.com", "cowcotland", "404media.co", "awfulannouncing.com", "michalzelazny.com"];
    private readonly ImageMetadataDatabase _database;
    private readonly HtmlMetadataDownloader _metadata;
    private readonly Downloader _downloader;
    private readonly ActivityLogger _log;
    private readonly BinaryDiskCache _faviconDiskCache;
    private readonly BinaryDiskCache _imageDiskCache;
    private readonly string _legacyFolder;
    private readonly int _maxPixelSize;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(string Url, bool Favicon), Task<ImageResult>> _pending = [];
    private readonly HashSet<Task> _operations = [];
    private readonly Dictionary<string, DateTimeOffset> _transientAttempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Url, bool Favicon), DownloadedImage> _cache = new();
    private bool _stopping;
    private Task? _shutdown;
    private sealed record ImageResult(DownloadedImage? Image, bool IsTransient = false);

    public FeedImageDownloader(ImageMetadataDatabase database, HtmlMetadataDownloader metadata, string cacheFolder,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null, int? maxPixelSize = null,
        string? legacyCacheFolder = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        _database = database;
        _metadata = metadata;
        _faviconDiskCache = new(Path.Combine(cacheFolder, "Favicons"));
        _imageDiskCache = new(Path.Combine(cacheFolder, "Images"));
        _legacyFolder = Path.GetFullPath(legacyCacheFolder ?? cacheFolder);
        _downloader = downloader ?? Downloader.Shared;
        _clock = clock ?? TimeProvider.System;
        _log = activityLog ?? ActivityLogger.Shared;
        _maxPixelSize = maxPixelSize ?? DefaultMaxPixelSize();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_maxPixelSize);
    }

    private static int DefaultMaxPixelSize()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Feed images require the Windows image decoder.");
        return WindowsScreen.MaxIconPixelSize(WindowsScreen.MaxScreenScale());
    }

    private byte[] ProcessImage(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Feed images require the Windows image decoder.");
        return WindowsImage.ScaledImageData(data, _maxPixelSize);
    }

    public Task<DownloadedImage?> FaviconAsync(FeedImageSource feed, CancellationToken ct = default)
        => RunOperationAsync(token => FaviconCoreAsync(feed, token), ct);

    private async Task<DownloadedImage?> FaviconCoreAsync(FeedImageSource feed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (HtmlMetadataDownloader.ShouldSkip(feed.FeedUrl)) return null;
        string? homePage = HomePage(feed);
        if (feed.FaviconUrl is { Length: > 0 } declared)
        {
            var image = (await FetchImageAsync(declared, favicon: true, ct).ConfigureAwait(false)).Image;
            if (image is not null && homePage is not null) await _database.SaveHomePageFaviconAsync(homePage, declared).ConfigureAwait(false);
            return image;
        }
        if (homePage is null || _database.HomePageHasNoFavicon(homePage)) return null;
        if (_database.FaviconUrl(homePage) is { } known)
        {
            var previous = await FetchImageAsync(known, favicon: true, ct).ConfigureAwait(false);
            if (previous.Image is not null) return previous.Image;
            if (previous.IsTransient) return null;
        }

        var metadata = await _metadata.GetMetadataAsync(homePage, ct).ConfigureAwait(false);
        var candidates = metadata?.UsableFaviconUrls() ?? [];
        var root = new Uri(new Uri(homePage), "/favicon.ico").AbsoluteUri;
        bool transient = false;
        foreach (var candidate in candidates.Append(root))
        {
            var result = await FetchImageAsync(candidate, favicon: true, ct).ConfigureAwait(false);
            if (result.Image is { } image)
            {
                await _database.SaveHomePageFaviconAsync(homePage, candidate).ConfigureAwait(false);
                return image;
            }
            transient |= result.IsTransient;
        }
        if (metadata is not null && candidates.Count == 0 && !transient)
            await _database.SaveHomePageFaviconAsync(homePage, null).ConfigureAwait(false);
        return null;
    }

    public Task<DownloadedImage?> IconAsync(FeedImageSource feed, CancellationToken ct = default)
        => RunOperationAsync(token => IconCoreAsync(feed, token), ct);

    private async Task<DownloadedImage?> IconCoreAsync(FeedImageSource feed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ShouldSkipFeedIcon(feed.FeedUrl)) return null;
        string? iconUrl = SpecialCases.ContainsSpecialCase(feed.FeedUrl, IgnoredFeedIconHosts) ? null : feed.IconUrl;
        if (iconUrl is { Length: > 0 })
        {
            var declared = await FetchImageAsync(SanitizedIconUrl(iconUrl), favicon: false, ct).ConfigureAwait(false);
            if (declared.Image is { } image)
            {
                await _database.SaveFeedIconUrlAsync(feed.FeedUrl, image.Url).ConfigureAwait(false);
                return image;
            }
            if (declared.IsTransient) return null;
        }
        else if (_database.IconUrl(feed.FeedUrl) is { } known)
        {
            var cached = await FetchImageAsync(known, favicon: false, ct).ConfigureAwait(false);
            if (cached.Image is not null) return cached.Image;
            if (cached.IsTransient) return null;
        }
        var homePage = HomePage(feed);
        if (homePage is null || ShouldSkipFeedIcon(homePage)) return null;
        var metadata = await _metadata.GetMetadataAsync(homePage, ct).ConfigureAwait(false);
        if (metadata?.BestWebsiteIconUrl() is not { Length: > 0 } found) return null;
        var result = await FetchImageAsync(SanitizedIconUrl(found), favicon: false, ct).ConfigureAwait(false);
        if (result.Image is { } discovered)
            await _database.SaveFeedIconUrlAsync(feed.FeedUrl, discovered.Url).ConfigureAwait(false);
        return result.Image;
    }

    public static string SanitizedIconUrl(string url)
        => url.Contains("/wp-content/uploads/", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.AbsoluteUri : url;

    public static bool ShouldSkipFeedIcon(string url) => SpecialCases.ContainsSpecialCase(url,
        ["macsparky.com", "xkcd.com", SpecialCases.RachelByTheBayHostName, SpecialCases.OpenRSSOrgHostName]);

    private static string? HomePage(FeedImageSource feed)
    {
        if (feed.HomePageUrl is { } home && Uri.TryCreate(home.NormalizedUrl(), UriKind.Absolute, out var page)
            && page.Scheme is "http" or "https") return page.AbsoluteUri;
        return Uri.TryCreate(feed.FeedUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? new Uri(url, "/").AbsoluteUri : null;
    }

    private async Task<ImageResult> FetchImageAsync(string url, bool favicon, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Trace.TraceWarning("Skipping a non-web image URL.");
            return new(null);
        }
        var key = (url, favicon);
        if (_cache.TryGetValue(key, out var cached))
        {
            if (!File.Exists(cached.Path))
                await (favicon ? _faviconDiskCache : _imageDiskCache).SetAsync(url.Md5Hex(), cached.Data, ct).ConfigureAwait(false);
            return new(cached);
        }
        if (_database.RecentlyFailed(url)) return new(null);
        Task<ImageResult> pending;
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_pending.TryGetValue(key, out pending!))
            {
                if (_transientAttempts.TryGetValue(url, out var attempted) && _clock.GetUtcNow() - attempted < TimeSpan.FromMinutes(30))
                    return new(null, IsTransient: true);
                pending = Task.Run(async () =>
                {
                    try { return await DownloadImageAsync(url, favicon, _lifetime.Token).ConfigureAwait(false); }
                    finally { lock (_gate) _pending.Remove(key); }
                });
                _pending.Add(key, pending);
            }
        }
        return await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<ImageResult> DownloadImageAsync(string url, bool favicon, CancellationToken ct)
    {
        var diskCache = favicon ? _faviconDiskCache : _imageDiskCache;
        string key = url.Md5Hex();
        string path = diskCache.PathForKey(key);
        if (await ReadFromDiskAsync(url, diskCache, key, ct).ConfigureAwait(false) is { } cached)
            return Cache(cached, favicon);
        var owner = favicon ? ActivityOwner.FaviconDownloader : ActivityOwner.FeedImageDownloader;
        var kind = favicon ? ActivityKind.DownloadFavicon(url) : ActivityKind.DownloadFeedImage(url);
        int activity = _log.CreateActivity(owner, kind);
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
            await diskCache.SetAsync(key, processed, ct).ConfigureAwait(false);
            await _database.ClearFailureAsync(url).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException)
        {
            _log.DidFail(activity, error);
            throw;
        }
        _log.DidComplete(activity, ActivityLogger.DataSizeMessage(bytes.LongLength), returnedFromCache: response.ReturnedFromCache);
        return Cache(new(url, path, processed), favicon);
    }

    private async Task<DownloadedImage?> ReadFromDiskAsync(string url, BinaryDiskCache diskCache, string key, CancellationToken ct)
    {
        string destination = diskCache.PathForKey(key);
        foreach (string path in LegacyCachePaths(url).Prepend(destination))
        {
            byte[]? data;
            byte[] processed;
            try
            {
                data = path == destination ? await diskCache.GetAsync(key, ct).ConfigureAwait(false)
                    : await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                if (data is null) continue;
                processed = ProcessImage(data);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (InvalidDataException error)
            {
                Trace.TraceWarning($"Ignoring an undecodable cached image {path}: {error.Message}");
                if (path == destination) diskCache.Remove(key);
                continue;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not read cached image {path}: {error.Message}");
                continue;
            }
            ct.ThrowIfCancellationRequested();
            if (path != destination || !ReferenceEquals(data, processed))
                await diskCache.SetAsync(key, processed, ct).ConfigureAwait(false);
            return new(url, destination, processed);
        }
        return null;
    }

    private ImageResult Cache(DownloadedImage image, bool favicon)
    {
        _cache[(image.Url, favicon)] = image;
        lock (_gate) _transientAttempts.Remove(image.Url);
        return new(image);
    }

    private IEnumerable<string> LegacyCachePaths(string url)
    {
        string extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".svg" or ".ico" or ".gif" or ".webp" or ".bmp")) extension = ".image";
        string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));
        yield return Path.Combine(_legacyFolder, key + (extension == ".svg" ? ".image" : extension));
        if (extension == ".svg") yield return Path.Combine(_legacyFolder, key + extension);
    }

    public void EmptyCache() => _cache.Clear();

    private Task<DownloadedImage?> RunOperationAsync(Func<CancellationToken, Task<DownloadedImage?>> work, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var task = Task.Run(async () =>
            {
                try { return await work(cancellation.Token).ConfigureAwait(false); }
                finally { cancellation.Dispose(); }
            });
            _operations.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_gate) _operations.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
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
        finally { _cache.Clear(); _lifetime.Dispose(); }
    }
}
