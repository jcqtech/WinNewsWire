using System.Diagnostics;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.HTMLMetadata;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Images;

public sealed record FeedImageSource(string FeedUrl, string? HomePageUrl = null, string? FaviconUrl = null, string? IconUrl = null);
/// <summary>Data is the requested representation; Path can identify its larger shared disk source.</summary>
public sealed record DownloadedImage(string Url, string Path, byte[] Data);
public sealed record FeedIconImage(FeedImageSource Feed, DownloadedImage Image);

public sealed class FeedImageDownloader : IAsyncDisposable
{
    private static Func<bool> _sharedBackgroundNetworkPolicy = () => NetworkMonitor.IsAvailable;
    private static readonly Lazy<FeedImageDownloader> SharedDownloader = new(() =>
        new(ImageDatabase.Shared, HtmlMetadataDownloader.Shared, AppConfig.CachesDirectory,
            networkAllowsUpdates: () => Volatile.Read(ref _sharedBackgroundNetworkPolicy)()));
    // Opening and maintaining the metadata databases must not happen on a UI caller's first image request.
    private static readonly Lazy<Task<FeedImageDownloader>> SharedInitialization = new(() => Task.Run(() => Shared));
    public static FeedImageDownloader Shared => SharedDownloader.Value;
    public static bool IsSharedCreated => SharedDownloader.IsValueCreated;
    public static Task<FeedImageDownloader> GetSharedAsync() => SharedInitialization.Value;

    public static void ConfigureSharedBackgroundNetworkPolicy(Func<bool> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Volatile.Write(ref _sharedBackgroundNetworkPolicy, policy);
    }

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
    private readonly ImageDatabase _database;
    private readonly HtmlMetadataDownloader _metadata;
    private readonly ImageResourceDownloader _images;
    private readonly bool _ownsDownloadedImages;
    public DownloadedImageCache DownloadedImages { get; }
    /// <summary>Raised on a worker when a changed source has a freshly rebuilt icon representation.</summary>
    public event EventHandler<DownloadedImage>? ImageDidBecomeAvailable;
    public event EventHandler<FeedIconImage>? FeedIconDidBecomeAvailable;
    public event EventHandler<FeedIconImage>? FaviconDidBecomeAvailable;
    private readonly object _gate = new();
    private sealed class OwnIconWait(string url)
    {
        internal string Url { get; } = url;
        internal DownloadedImage? Image { get; set; }
    }
    private readonly Dictionary<FeedImageSource, OwnIconWait> _waitingForOwnIcons = [];
    private readonly Dictionary<FeedImageSource, HashSet<string>> _waitingForFavicons = [];
    private readonly HashSet<FeedImageSource> _retryingFavicons = [];
    private readonly HashSet<FeedImageSource> _homePageIconFeeds = [];
    private readonly Dictionary<string, HtmlMetadataRecord> _homePageIconMetadata = [];
    private readonly HashSet<string> _checkedHomePageIcons = [];
    private readonly HashSet<string> _checkingHomePageIcons = [];
    private readonly Dictionary<string, HtmlMetadataRecord> _pendingHomePageMetadata = [];
    private readonly HashSet<string> _faviconHomePages = [];
    private readonly HashSet<string> _checkedFaviconHomePages = [];
    private readonly HashSet<string> _checkingFaviconHomePages = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _operations = [];
    private bool _stopping;
    private Task? _shutdown;

    public FeedImageDownloader(ImageDatabase database, HtmlMetadataDownloader metadata, string cacheFolder,
        Downloader? downloader = null, TimeProvider? clock = null, ActivityLogger? activityLog = null, int? maxPixelSize = null,
        DownloadedImageCache? downloadedImages = null,
        Func<bool>? networkAllowsUpdates = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFolder);
        _database = database;
        _metadata = metadata;
        int pixelSize = maxPixelSize ?? DefaultMaxPixelSize();
        DownloadedImages = downloadedImages ?? new(database, cacheFolder, downloader, clock, activityLog, networkAllowsUpdates);
        _ownsDownloadedImages = downloadedImages is null;
        _images = new(database, Path.Combine(cacheFolder, "Images"), new(pixelSize),
            ActivityOwner.FeedImageDownloader, ActivityKind.DownloadFeedImage, downloader, clock, activityLog,
            sourceCache: DownloadedImages);
        DownloadedImages.DownloadedImageChanged += SourceChanged;
        _metadata.MetadataAvailable += MetadataAvailable;
        NetworkMonitor.ConnectivityChanged += NetworkStatusChanged;
    }

    internal void NetworkStatusChanged(object? sender, bool available)
    {
        if (!available) return;
        KeyValuePair<FeedImageSource, OwnIconWait>[] waiting;
        KeyValuePair<FeedImageSource, HashSet<string>>[] favicons;
        lock (_gate)
        {
            if (_stopping) return;
            waiting = _waitingForOwnIcons.ToArray();
            favicons = _waitingForFavicons.ToArray();
        }
        foreach (var (feed, urls) in favicons)
        {
            if (urls.All(DownloadedImages.IsWaitingToRetry)) continue;
            lock (_gate)
            {
                if (_stopping) return;
                if (!_waitingForFavicons.TryGetValue(feed, out var current) || !ReferenceEquals(current, urls)) continue;
                if (!_retryingFavicons.Add(feed)) continue;
                var task = RunOperationAsync(async ct =>
                {
                    try
                    {
                        var image = await FaviconCoreAsync(feed, ct).ConfigureAwait(false);
                        if (image is not null) FaviconDidBecomeAvailable?.Invoke(this, new(feed, image));
                        return image;
                    }
                    finally { lock (_gate) _retryingFavicons.Remove(feed); }
                }, CancellationToken.None);
                _ = task.ContinueWith(completed =>
                {
                    if (completed.IsFaulted)
                        Trace.TraceError($"Could not retry the favicon for {feed.FeedUrl}: {completed.Exception}");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        foreach (var (feed, ownIcon) in waiting)
        {
            if (DownloadedImages.IsWaitingToRetry(ownIcon.Url)) continue;
            lock (_gate)
            {
                if (_stopping) return;
                var task = RunOperationAsync(token => IconCoreAsync(feed, token), CancellationToken.None);
                _ = task.ContinueWith(completed =>
                {
                    if (completed.IsFaulted)
                        Trace.TraceError($"Could not retry the feed icon for {feed.FeedUrl}: {completed.Exception}");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private void SourceChanged(object? sender, string url)
    {
        lock (_gate)
        {
            if (_stopping) return;
            var task = RunOperationAsync(async ct =>
            {
                var result = await _images.RefreshAsync(url, ct).ConfigureAwait(false);
                if (result.Image is { } image) ImageDidBecomeAvailable?.Invoke(this, image);
                return result.Image;
            }, CancellationToken.None);
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted)
                    Trace.TraceError($"Could not rebuild a changed feed image at {url}: {completed.Exception}");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static int DefaultMaxPixelSize()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Feed images require the Windows image decoder.");
        return WindowsScreen.MaxIconPixelSize(WindowsScreen.MaxScreenScale());
    }

    public Task<DownloadedImage?> FaviconAsync(FeedImageSource feed, CancellationToken ct = default)
        => RunOperationAsync(token => FaviconCoreAsync(feed, token), ct);

    private async Task<DownloadedImage?> FaviconCoreAsync(FeedImageSource feed, CancellationToken ct)
    {
        var transientUrls = new HashSet<string>();
        var image = await FindFaviconAsync(feed, transientUrls, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_stopping)
            {
                if (transientUrls.Count > 0) _waitingForFavicons[feed] = transientUrls;
                else _waitingForFavicons.Remove(feed);
            }
        }
        return image;
    }

    private async Task<DownloadedImage?> FindFaviconAsync(FeedImageSource feed, HashSet<string> transientUrls, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (HtmlMetadataDownloader.ShouldSkip(feed.FeedUrl)) return null;
        string? homePage = HomePage(feed);
        string? failedDeclaredUrl = null;
        bool transient = false;
        if (feed.FaviconUrl is { Length: > 0 } declared)
        {
            var result = await FetchImageAsync(declared, favicon: true, ct, homePage).ConfigureAwait(false);
            if (result.IsTransient) transientUrls.Add(declared);
            if (result.Image is { } image)
            {
                if (homePage is not null) await _database.SaveHomePageFaviconAsync(homePage, declared).ConfigureAwait(false);
                return image;
            }
            failedDeclaredUrl = declared;
            transient = result.IsTransient;
        }
        if (homePage is null || _database.HomePageHasNoFavicon(homePage)) return null;
        if (_database.FaviconUrl(homePage) is { } known && known != failedDeclaredUrl)
        {
            var previous = await FetchImageAsync(known, favicon: true, ct, homePage).ConfigureAwait(false);
            if (previous.IsTransient) transientUrls.Add(known);
            if (previous.Image is not null)
            {
                CheckForNewFavicon(homePage, known);
                return previous.Image;
            }
            if (previous.IsTransient && !previous.DecodingFailed) return null;
            transient |= previous.IsTransient;
        }

        var metadata = await _metadata.GetMetadataAsync(homePage, ct).ConfigureAwait(false);
        var candidates = metadata?.UsableFaviconUrls() ?? [];
        var root = new Uri(new Uri(homePage), "/favicon.ico").AbsoluteUri;
        foreach (var candidate in candidates.Append(root))
        {
            var result = await FetchImageAsync(candidate, favicon: true, ct, homePage).ConfigureAwait(false);
            if (result.IsTransient) transientUrls.Add(candidate);
            if (result.Image is { } image)
            {
                await _database.SaveHomePageFaviconAsync(homePage, candidate).ConfigureAwait(false);
                return image;
            }
            transient |= result.IsTransient;
        }
        if (metadata is not null && !transient)
            await _database.SaveHomePageFaviconAsync(homePage, null).ConfigureAwait(false);
        return null;
    }

    public Task<DownloadedImage?> IconAsync(FeedImageSource feed, CancellationToken ct = default)
        => RunOperationAsync(token => IconCoreAsync(feed, token), ct);

    private async Task<DownloadedImage?> IconCoreAsync(FeedImageSource feed, CancellationToken ct,
        HtmlMetadataRecord? homePageMetadata = null, bool announceRecovery = true)
    {
        ct.ThrowIfCancellationRequested();
        if (ShouldSkipFeedIcon(feed.FeedUrl)) return null;
        string? iconUrl = OwnIconUrl(feed);
        OwnIconWait? ownIcon = null;
        if (iconUrl is { Length: > 0 })
        {
            var url = SanitizedIconUrl(iconUrl);
            var declared = await FetchImageAsync(url, favicon: false, ct).ConfigureAwait(false);
            if (declared.Image is { } image)
            {
                OwnIconWait? recovered;
                lock (_gate)
                {
                    if (_waitingForOwnIcons.Remove(feed, out recovered)) recovered.Image = image;
                }
                if (recovered is not null && announceRecovery) FeedIconDidBecomeAvailable?.Invoke(this, new(feed, image));
                return image;
            }
            bool waiting = DownloadedImages.IsWaitingToRetry(url);
            lock (_gate)
            {
                if (waiting)
                {
                    if (!_waitingForOwnIcons.TryGetValue(feed, out ownIcon))
                        _waitingForOwnIcons[feed] = ownIcon = new(url);
                }
                else _waitingForOwnIcons.Remove(feed);
            }
        }
        var homePage = HomePage(feed);
        if (homePage is not null && !ShouldSkipFeedIcon(homePage) && _database.HomePageIconUrl(homePage) is { } known)
        {
            lock (_gate) { if (!_stopping) _homePageIconFeeds.Add(feed); }
            var cached = await FetchImageAsync(known, favicon: false, ct).ConfigureAwait(false);
            if (cached.Image is not null)
            {
                if (iconUrl is not { Length: > 0 }) CheckForNewHomePageIcon(homePage, known);
                lock (_gate) return ownIcon?.Image ?? cached.Image;
            }
        }
        var fallback = await HomePageIconCoreAsync(feed, ct, homePageMetadata).ConfigureAwait(false);
        lock (_gate) return ownIcon?.Image ?? fallback;
    }

    private void MetadataAvailable(object? sender, HtmlMetadataRecord metadata)
    {
        metadata = _metadata.CachedMetadata(metadata.Url) ?? metadata;
        bool checkIcon;
        bool checkFavicon;
        lock (_gate)
        {
            if (_stopping) return;
            checkIcon = _homePageIconFeeds.Any(feed => HomePage(feed) == metadata.Url);
            if (checkIcon)
            {
                checkIcon = !_homePageIconMetadata.TryGetValue(metadata.Url, out var previous) || !ReferenceEquals(previous, metadata);
                _homePageIconMetadata[metadata.Url] = metadata;
            }
            checkFavicon = _faviconHomePages.Contains(metadata.Url);
            if (checkFavicon) _checkedFaviconHomePages.Remove(metadata.Url);
        }
        if (checkIcon)
            CheckForNewHomePageIcon(metadata.Url, _database.HomePageIconUrl(metadata.Url), metadata);
        if (checkFavicon && _database.FaviconUrl(metadata.Url) is { } favicon)
            CheckForNewFavicon(metadata.Url, favicon, metadata);
    }

    private void CheckForNewFavicon(string homePage, string saved, HtmlMetadataRecord? metadata = null)
    {
        lock (_gate)
        {
            if (_stopping) return;
            _faviconHomePages.Add(homePage);
            if (_checkingFaviconHomePages.Contains(homePage) || !_checkedFaviconHomePages.Add(homePage)) return;
            _checkingFaviconHomePages.Add(homePage);
            var task = RunOperationAsync(async ct =>
            {
                try
                {
                    var current = metadata ?? await _metadata.GetMetadataAsync(homePage, ct).ConfigureAwait(false);
                    if (current is null) return null;
                    var root = new Uri(new Uri(homePage), "/favicon.ico").AbsoluteUri;
                    foreach (var candidate in current.UsableFaviconUrls().Append(root).TakeWhile(url => url != saved))
                    {
                        var result = await FetchImageAsync(candidate, favicon: true, ct, homePage).ConfigureAwait(false);
                        if (result.Image is not { } image) continue;
                        if (_database.FaviconUrl(homePage) == saved)
                            await _database.SaveHomePageFaviconAsync(homePage, candidate).ConfigureAwait(false);
                        return image;
                    }
                    return null;
                }
                finally { lock (_gate) _checkingFaviconHomePages.Remove(homePage); }
            }, CancellationToken.None);
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted)
                    Trace.TraceError($"Could not check for a new favicon at {homePage}: {completed.Exception}");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void CheckForNewHomePageIcon(string homePage, string? saved, HtmlMetadataRecord? metadata = null)
    {
        lock (_gate)
        {
            if (_stopping) return;
            if (metadata is null && !_checkedHomePageIcons.Add(homePage)) return;
            _checkedHomePageIcons.Add(homePage);
            if (!_checkingHomePageIcons.Add(homePage))
            {
                if (metadata is not null) _pendingHomePageMetadata[homePage] = metadata;
                return;
            }
            var task = RunOperationAsync(async ct =>
            {
                try
                {
                    var current = metadata ??= await _metadata.GetMetadataAsync(homePage, ct).ConfigureAwait(false);
                    if (current is null) return null;
                    if (saved is not null)
                    {
                        if (current.BestHomePageIconUrl() is not { Length: > 0 } found) return null;
                        var url = SanitizedIconUrl(found);
                        if (url == saved) return null;
                        var result = await FetchImageAsync(url, favicon: false, ct).ConfigureAwait(false);
                        if (result.Image is not { } image || _database.HomePageIconUrl(homePage) != saved) return null;
                        await _database.SaveHomePageIconAsync(homePage, image.Url).ConfigureAwait(false);
                    }
                    FeedImageSource[] feeds;
                    lock (_gate) feeds = _homePageIconFeeds.Where(feed => HomePage(feed) == homePage).ToArray();
                    foreach (var feed in feeds)
                    {
                        var preferred = await IconCoreAsync(feed, ct, current, announceRecovery: false).ConfigureAwait(false);
                        if (preferred is not null) FeedIconDidBecomeAvailable?.Invoke(this, new(feed, preferred));
                    }
                    return null;
                }
                finally
                {
                    HtmlMetadataRecord? pending;
                    lock (_gate)
                    {
                        _checkingHomePageIcons.Remove(homePage);
                        _pendingHomePageMetadata.Remove(homePage, out pending);
                    }
                    if (pending is not null && !ReferenceEquals(pending, metadata))
                        CheckForNewHomePageIcon(homePage, _database.HomePageIconUrl(homePage), pending);
                }
            }, CancellationToken.None);
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted)
                    Trace.TraceError($"Could not check for a new homepage icon at {homePage}: {completed.Exception}");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task<DownloadedImage?> HomePageIconCoreAsync(FeedImageSource feed, CancellationToken ct,
        HtmlMetadataRecord? homePageMetadata = null)
    {
        var homePage = HomePage(feed);
        if (homePage is null || ShouldSkipFeedIcon(homePage)) return null;
        if (homePageMetadata is null && _database.HomePageHasNoIcon(homePage))
        {
            lock (_gate) { if (!_stopping) _homePageIconFeeds.Add(feed); }
            return null;
        }
        var metadata = homePageMetadata ?? await _metadata.GetMetadataAsync(homePage, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_stopping)
            {
                _homePageIconFeeds.Add(feed);
                if (metadata is not null) _homePageIconMetadata.TryAdd(homePage, metadata);
            }
        }
        if (metadata?.BestHomePageIconUrl() is not { Length: > 0 } found)
        {
            if (metadata is not null && _database.HomePageIconUrl(homePage) is null)
                await _database.SaveHomePageIconAsync(homePage, null).ConfigureAwait(false);
            return null;
        }
        var result = await FetchImageAsync(SanitizedIconUrl(found), favicon: false, ct).ConfigureAwait(false);
        if (result.Image is { } discovered
            && (_metadata.CachedMetadata(homePage) is not { } latest || latest.BestHomePageIconUrl() == found))
            await _database.SaveHomePageIconAsync(homePage, discovered.Url).ConfigureAwait(false);
        return result.Image;
    }

    public static string SanitizedIconUrl(string url)
        => url.Contains("/wp-content/uploads/", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.AbsoluteUri : url;

    private static string? OwnIconUrl(FeedImageSource feed)
        => SpecialCases.ContainsSpecialCase(feed.FeedUrl, IgnoredFeedIconHosts) ? null : feed.IconUrl;

    public static bool ShouldSkipFeedIcon(string url) => SpecialCases.ContainsSpecialCase(url,
        ["macsparky.com", "xkcd.com", SpecialCases.RachelByTheBayHostName, SpecialCases.OpenRSSOrgHostName]);

    private static string? HomePage(FeedImageSource feed)
    {
        if (feed.HomePageUrl is { } home && Uri.TryCreate(home.NormalizedUrl(), UriKind.Absolute, out var page)
            && page.Scheme is "http" or "https") return page.AbsoluteUri;
        return Uri.TryCreate(feed.FeedUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? new Uri(url, "/").AbsoluteUri : null;
    }

    private Task<ImageResourceResult> FetchImageAsync(string url, bool favicon, CancellationToken ct, string? activityDetail = null)
    {
        ct.ThrowIfCancellationRequested();
        return _images.FetchAsync(url, ct, activityDetail, favicon
            ? new(ActivityOwner.FaviconDownloader, ActivityKind.DownloadFavicon(url), activityDetail, null) : null);
    }

    public void EmptyCache()
    {
        _images.EmptyCache();
    }

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
            DownloadedImages.DownloadedImageChanged -= SourceChanged;
            _metadata.MetadataAvailable -= MetadataAvailable;
            NetworkMonitor.ConnectivityChanged -= NetworkStatusChanged;
            _waitingForOwnIcons.Clear();
            _waitingForFavicons.Clear();
            _homePageIconFeeds.Clear();
            _homePageIconMetadata.Clear();
            _checkedHomePageIcons.Clear();
            _pendingHomePageMetadata.Clear();
            _faviconHomePages.Clear();
            _checkedFaviconHomePages.Clear();
            _lifetime.Cancel();
            return new(_shutdown = DrainAsync(_operations.ToArray()));
        }
    }

    private async Task DrainAsync(Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks.Append(_images.DisposeAsync().AsTask())).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            try
            {
                if (_ownsDownloadedImages) await DownloadedImages.DisposeAsync().ConfigureAwait(false);
            }
            finally { _lifetime.Dispose(); }
        }
    }
}
