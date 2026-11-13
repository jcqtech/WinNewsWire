using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Networking.Connectivity;
using Windows.Storage.Streams;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Images;
using WinNewsWire.Parsers;
using WinNewsWire.Web;

namespace WinNewsWire.Services;

internal sealed class ArticleThumbnailService : IAsyncDisposable
{
    private const int SmallThumbnailMaxPixelSize = 512;
    private const int LargeThumbnailMaxPixelSize = 1200;
    private sealed record CachedThumbnail(string Url, ThumbnailMode Mode, SoftwareBitmapSource Source, long ByteCount);
    private sealed class InsignificantThumbnailDimensionsException(uint width, uint height)
        : Exception($"Thumbnail dimensions are too small or too narrow ({width}x{height}).");
    private readonly DispatcherQueue _dispatcher;
    private readonly Lazy<Task<(ArticleThumbnailFinder Finder, ThumbnailDownloader Downloads)>> _backend;
    private readonly long _cacheByteLimit;
    private readonly Func<bool> _networkAllowsDownloads;
    private readonly Func<bool> _networkAllowsPrefetch;
    private readonly Dictionary<(string Url, ThumbnailMode Mode), LinkedListNode<CachedThumbnail>> _cache = [];
    private readonly LinkedList<CachedThumbnail> _cacheOrder = [];
    private readonly ConcurrentDictionary<(string Url, ThumbnailMode Mode), Lazy<Task<SoftwareBitmapSource?>>> _pending = [];
    private readonly ConcurrentDictionary<string, Lazy<Task>> _discovering = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly HashSet<Task> _operations = [];
    private readonly CancellationTokenSource _lifetime = new();
    private long _cacheBytes;
    private bool _stopping;
    private Task? _shutdown;

    internal event EventHandler<string>? ThumbnailAvailable;
    internal event EventHandler<IReadOnlySet<string>>? ThumbnailUrlsAvailable;
    internal event EventHandler<string>? ThumbnailDownloadFailed;
    internal event EventHandler? NetworkStatusChanged;
    internal bool NetworkDownloadsAllowed { get; private set; }
#if PARITY_PROBE
    internal void ProbeNotifyNetworkStatusChanged() => OnNetworkStatusChanged(null);
    internal int ProbeDecodes;
    internal bool ProbeDecodedOnUIThread;
    internal long ProbeCacheBytes => _cacheBytes;
    internal (int Width, int Height) ProbeLastDecodedSize;
    internal int ProbeDecodeError;
    internal int ProbeUrlResolutions;
    internal readonly ConcurrentDictionary<string, DownloadPriority> ProbeDownloadPriorities = new(StringComparer.Ordinal);
#endif

    internal ArticleThumbnailService(DispatcherQueue dispatcher,
        Func<(ArticleThumbnailFinder Finder, ThumbnailDownloader Downloads)>? createBackend = null,
        long cacheByteLimit = 128 * 1024 * 1024, Func<bool>? networkAllowsDownloads = null,
        Func<bool>? networkAllowsPrefetch = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheByteLimit);
        _dispatcher = dispatcher;
        VerifyAccess();
        _cacheByteLimit = cacheByteLimit;
        _networkAllowsDownloads = networkAllowsDownloads ?? NetworkAllowsDownloads;
        _networkAllowsPrefetch = networkAllowsPrefetch ?? (() => _networkAllowsDownloads()
            && global::Windows.System.Power.PowerManager.EnergySaverStatus != global::Windows.System.Power.EnergySaverStatus.On);
        NetworkDownloadsAllowed = _networkAllowsDownloads();
        _backend = new(() => Task.Run(() =>
        {
            var backend = (createBackend ?? CreateBackend)();
            backend.Finder.ThumbnailUrlsAvailable += FinderUrlsAvailable;
            backend.Downloads.DownloadFailed += BackendDownloadFailed;
            return backend;
        }));
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        NetworkMonitor.ConnectivityChanged += OnConnectivityChanged;
    }

    private void OnConnectivityChanged(object? sender, bool available) => OnNetworkStatusChanged(sender);

    private void OnNetworkStatusChanged(object? sender)
        => DispatchNotification(() =>
        {
            bool allowed = _networkAllowsDownloads();
            if (NetworkDownloadsAllowed == allowed) return;
            NetworkDownloadsAllowed = allowed;
            NetworkStatusChanged?.Invoke(this, EventArgs.Empty);
        });

    private static (ArticleThumbnailFinder, ThumbnailDownloader) CreateBackend()
    {
        var database = ImageDatabase.Shared;
        return (new(database), new(database, AppConfig.CachesDirectory, NetworkAllowsDownloads,
            downloadedImages: FeedImageDownloader.Shared.DownloadedImages));
    }

    internal static bool NetworkAllowsDownloads()
    {
        if (!NetworkMonitor.IsAvailable) return false;
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null) return false;
        var cost = profile.GetConnectionCost();
        return cost.NetworkCostType == NetworkCostType.Unrestricted
            && !cost.Roaming && !cost.OverDataLimit && !cost.ApproachingDataLimit;
    }

    internal Task<SoftwareBitmapSource?> ThumbnailAsync(Article article, ThumbnailMode mode, CancellationToken ct = default,
        string? resolvedUrl = null)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (mode is not (ThumbnailMode.Small or ThumbnailMode.Large)) throw new ArgumentOutOfRangeException(nameof(mode));
        var baseUrl = ArticleBaseUrl(article);
        return RunOperationAsync(token => FindImageAsync(article, mode, DownloadPriority.Visible, baseUrl, token, resolvedUrl), ct);
    }

    internal Task PrefetchThumbnailAsync(Article article, ThumbnailMode mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (mode is not (ThumbnailMode.Small or ThumbnailMode.Large)) throw new ArgumentOutOfRangeException(nameof(mode));
        var baseUrl = ArticleBaseUrl(article);
        bool allowed = _networkAllowsPrefetch();
        return RunOperationAsync(token => allowed
            ? FindImageAsync(article, mode, DownloadPriority.Prefetch, baseUrl, token)
            : Task.FromResult<SoftwareBitmapSource?>(null), ct);
    }

    internal Task<string?> ThumbnailUrlAsync(Article article, ThumbnailMode mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (mode is not (ThumbnailMode.Small or ThumbnailMode.Large)) throw new ArgumentOutOfRangeException(nameof(mode));
        var baseUrl = ArticleBaseUrl(article);
        return RunOperationAsync(token => ResolveUrlAsync(article, mode, baseUrl, token), ct);
    }

    internal Task<(byte[] Data, string MimeType)?> FullSizeImageAsync(string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return RunOperationAsync<(byte[] Data, string MimeType)?>(async token =>
        {
            var backend = await _backend.Value.ConfigureAwait(false);
            var source = await backend.Downloads.FullSizeImageAsync(url, token).ConfigureAwait(false);
            if (source is null) return null;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(source.Data.AsBuffer()).AsTask(token).ConfigureAwait(false);
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
            string mimeType = decoder.DecoderInformation.MimeTypes.FirstOrDefault(type => type.StartsWith("image/", StringComparison.Ordinal))
                ?? throw new InvalidDataException("The image decoder did not identify an image MIME type.");
            return (source.Data, mimeType);
        }, ct);
    }

    internal Task DiscoverAsync(IEnumerable<Article> articles, CancellationToken ct = default)
    {
        var snapshot = articles.ToArray();
        var baseUrls = snapshot.DistinctBy(article => article.ArticleID)
            .ToDictionary(article => article.ArticleID, ArticleBaseUrl, StringComparer.Ordinal);
        return RunOperationAsync(async token =>
        {
            var backend = await _backend.Value.ConfigureAwait(false);
            await backend.Finder.DiscoverThumbnailUrlsAsync(snapshot, token, article => baseUrls[article.ArticleID]).ConfigureAwait(false);
            return true;
        }, ct);
    }

    private Uri? ArticleBaseUrl(Article article)
    {
        VerifyAccess();
        if (article.BaseUrl() is { } baseUrl) return baseUrl;
        var account = AppRuntime.AppService.Shared.Accounts.Accounts.FirstOrDefault(candidate => candidate.AccountID == article.AccountID);
        var feed = account?.FlattenedFeeds().FirstOrDefault(candidate => candidate.FeedID == article.FeedID);
        return article.BaseUrl(feed?.HomePageUrl, feed?.Url);
    }

    private Task<T> RunOperationAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        VerifyAccess();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            ct.ThrowIfCancellationRequested();
            var operation = Task.Run(() => work(_lifetime.Token));
            _operations.Add(operation);
            _ = operation.ContinueWith(completed =>
            {
                lock (_gate) _operations.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return operation.WaitAsync(ct);
        }
    }

    private async Task<string?> ResolveUrlAsync(Article article, ThumbnailMode mode, Uri? baseUrl, CancellationToken ct)
    {
#if PARITY_PROBE
        Interlocked.Increment(ref ProbeUrlResolutions);
#endif
        var backend = await _backend.Value.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (backend.Finder.ThumbnailUrl(article, mode) is null)
        {
            var discovery = _discovering.GetOrAdd(article.ArticleID,
                _ => new(() => backend.Finder.DiscoverThumbnailUrlsAsync([article], ct, _ => baseUrl)));
            try { await discovery.Value.ConfigureAwait(false); }
            finally { _discovering.TryRemove(new KeyValuePair<string, Lazy<Task>>(article.ArticleID, discovery)); }
        }
        return backend.Finder.ThumbnailUrl(article, mode);
    }

    private async Task<SoftwareBitmapSource?> FindImageAsync(Article article, ThumbnailMode mode, DownloadPriority priority,
        Uri? baseUrl, CancellationToken ct, string? resolvedUrl = null)
    {
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var url = resolvedUrl ?? await ResolveUrlAsync(article, mode, baseUrl, ct).ConfigureAwait(false);
        while (url is not null && tried.Add(url))
        {
            var backend = await _backend.Value.ConfigureAwait(false);
            string imageUrl = url;
            var key = (imageUrl, mode);
            var pending = _pending.GetOrAdd(key, _ => new(() => LoadImageAsync(imageUrl, mode, article.Title,
                backend.Finder, backend.Downloads, priority, ct)));
            if (priority == DownloadPriority.Visible) backend.Downloads.PrioritizeDownload(url);
            try
            {
                if (await pending.Value.ConfigureAwait(false) is { } image) return image;
            }
            finally { _pending.TryRemove(new KeyValuePair<(string, ThumbnailMode), Lazy<Task<SoftwareBitmapSource?>>>(key, pending)); }
            url = await ResolveUrlAsync(article, mode, baseUrl, ct).ConfigureAwait(false);
        }
        return null;
    }

    private void FinderUrlsAvailable(object? sender, IReadOnlySet<string> ids)
        => DispatchNotification(() => ThumbnailUrlsAvailable?.Invoke(this, ids));

    private void BackendDownloadFailed(object? sender, string url)
        => DispatchNotification(() => ThumbnailDownloadFailed?.Invoke(this, url));

    private void DispatchNotification(Action notification)
    {
        lock (_gate)
            if (_stopping) return;
        if (!_dispatcher.TryEnqueue(() =>
        {
            lock (_gate)
                if (_stopping) return;
            notification();
        }))
            throw new InvalidOperationException("The thumbnail UI dispatcher is unavailable.");
    }

    private async Task<SoftwareBitmapSource?> LoadImageAsync(string url, ThumbnailMode mode, string? title,
        ArticleThumbnailFinder finder, ThumbnailDownloader downloads, DownloadPriority priority, CancellationToken ct)
    {
        var cached = await OnDispatcherAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!_cache.TryGetValue((url, mode), out var node)) return Task.FromResult<SoftwareBitmapSource?>(null);
            _cacheOrder.Remove(node);
            _cacheOrder.AddLast(node);
            return Task.FromResult<SoftwareBitmapSource?>(node.Value.Source);
        }).ConfigureAwait(false);
        if (cached is not null)
        {
            await downloads.ImageWasUsedAsync(url, ct).ConfigureAwait(false);
            return cached;
        }
#if PARITY_PROBE
        ProbeDownloadPriorities[url] = priority;
#endif
        var downloaded = await downloads.SourceAsync(url, ct, title, priority).ConfigureAwait(false);
        if (downloaded is null) return null;
        SoftwareBitmap decoded;
        try { decoded = await DecodeAsync(downloaded.Data, mode, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is InsignificantThumbnailDimensionsException || IsImageDecodingError(error))
        {
            Trace.TraceWarning($"Could not decode thumbnail {url}: {error.Message}");
            finder.RecordDecodingFailure(url);
            DispatchNotification(() => ThumbnailDownloadFailed?.Invoke(this, url));
            return null;
        }
        using var bitmap = decoded;
        long byteCount;
        using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
        {
            var plane = buffer.GetPlaneDescription(0);
            byteCount = checked(Math.Abs((long)plane.Stride) * plane.Height);
        }
        return await OnDispatcherAsync(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);
            VerifyAccess();
            ct.ThrowIfCancellationRequested();
            if (byteCount <= _cacheByteLimit)
            {
                while (_cacheBytes + byteCount > _cacheByteLimit && _cacheOrder.First is { } oldest)
                {
                    _cacheOrder.RemoveFirst();
                    _cache.Remove((oldest.Value.Url, oldest.Value.Mode));
                    _cacheBytes -= oldest.Value.ByteCount;
                }
                _cache.Add((url, mode), _cacheOrder.AddLast(new CachedThumbnail(url, mode, source, byteCount)));
                _cacheBytes += byteCount;
            }
            ThumbnailAvailable?.Invoke(this, url);
            return source;
        }).ConfigureAwait(false);
    }

    private async Task<SoftwareBitmap> DecodeAsync(byte[] data, ThumbnailMode mode, CancellationToken ct)
    {
#if PARITY_PROBE
        Interlocked.Increment(ref ProbeDecodes);
        if (_dispatcher.HasThreadAccess) ProbeDecodedOnUIThread = true;
        int errorCode = Interlocked.Exchange(ref ProbeDecodeError, 0);
        if (errorCode != 0) throw new COMException("Injected native thumbnail decoder failure.", errorCode);
#endif
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer()).AsTask(ct).ConfigureAwait(false);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        BitmapFrame? frame = null;
        for (uint index = 0; index < decoder.FrameCount; index++)
        {
            try
            {
                var candidate = await decoder.GetFrameAsync(index).AsTask(ct).ConfigureAwait(false);
                if (frame is null || Math.Max(candidate.PixelWidth, candidate.PixelHeight) > Math.Max(frame.PixelWidth, frame.PixelHeight))
                    frame = candidate;
            }
            catch (Exception error) when (IsImageDecodingError(error))
            {
                Trace.TraceWarning($"Could not read thumbnail frame {index}: {error.Message}");
            }
        }
        if (frame is null) throw new InvalidDataException("The image has no readable frames.");
        if (!SignificantImageFilter.HasSignificantDimensions(frame.PixelWidth, frame.PixelHeight))
            throw new InsignificantThumbnailDimensionsException(frame.PixelWidth, frame.PixelHeight);
        int maxPixelSize = mode == ThumbnailMode.Small ? SmallThumbnailMaxPixelSize : LargeThumbnailMaxPixelSize;
        double scale = Math.Min(1, (double)maxPixelSize / Math.Max(frame.PixelWidth, frame.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(frame.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(frame.PixelHeight * scale))
        };
        var bitmap = await frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(ct).ConfigureAwait(false);
#if PARITY_PROBE
        ProbeLastDecodedSize = (bitmap.PixelWidth, bitmap.PixelHeight);
#endif
        return bitmap;
    }

    private static bool IsImageDecodingError(Exception error)
        => error is InvalidDataException || (unchecked((uint)error.HResult) & 0xffff0000) == 0x88980000
            || error.HResult == unchecked((int)0x80004005);

    private Task<T> OnDispatcherAsync<T>(Func<Task<T>> work)
    {
        if (_dispatcher.HasThreadAccess) return work();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await work()); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }))
            completion.TrySetException(new InvalidOperationException("The thumbnail UI dispatcher is unavailable."));
        return completion.Task;
    }

    internal void EmptyCache()
    {
        VerifyAccess();
        _cache.Clear();
        _cacheOrder.Clear();
        _cacheBytes = 0;
    }

    private void VerifyAccess()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Thumbnail presentation requires its UI dispatcher.");
    }

    public ValueTask DisposeAsync()
    {
        VerifyAccess();
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown);
            _stopping = true;
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            NetworkMonitor.ConnectivityChanged -= OnConnectivityChanged;
            _lifetime.Cancel();
            return new(_shutdown = DrainAsync(_operations.ToArray()));
        }
    }

    private async Task DrainAsync(Task[] operations)
    {
        try
        {
            try { await Task.WhenAll(operations); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        finally
        {
            try
            {
                if (_backend.IsValueCreated)
                {
                    var backend = await _backend.Value;
                    backend.Finder.ThumbnailUrlsAvailable -= FinderUrlsAvailable;
                    backend.Downloads.DownloadFailed -= BackendDownloadFailed;
                    await backend.Downloads.DisposeAsync();
                }
            }
            finally
            {
                EmptyCache();
                _lifetime.Dispose();
            }
        }
    }
}
