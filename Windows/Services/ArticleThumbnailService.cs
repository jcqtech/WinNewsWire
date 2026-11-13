using System;
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
    private const int SmallThumbnailMaxPixelSize = 256;
    private const int LargeThumbnailMaxPixelSize = 1200;
    private sealed record CachedThumbnail(string Url, ThumbnailMode Mode, SoftwareBitmapSource Source, long ByteCount);
    private sealed class InsignificantThumbnailDimensionsException(uint width, uint height)
        : Exception($"Thumbnail dimensions are too small or too narrow ({width}x{height}).");
    private readonly DispatcherQueue _dispatcher;
    private readonly Lazy<Task<(ArticleThumbnailFinder Finder, ThumbnailDownloader Downloads)>> _backend;
    private ArticleThumbnailFinder? _finder;
    private readonly long _cacheByteLimit;
    private readonly Func<bool> _networkAllowsDownloads;
    private readonly Dictionary<(string Url, ThumbnailMode Mode), LinkedListNode<CachedThumbnail>> _cache = [];
    private readonly LinkedList<CachedThumbnail> _cacheOrder = [];
    private readonly Dictionary<(string Url, ThumbnailMode Mode), Lazy<Task<SoftwareBitmapSource?>>> _pending = [];
    private readonly ThumbnailTaskQueue _decodeQueue = new(4, 60);
    private readonly object _gate = new();
    private readonly HashSet<Task> _operations = [];
    private readonly CancellationTokenSource _lifetime = new();
    private long _cacheBytes;
    private bool _stopping;
    private Task? _shutdown;

    internal event EventHandler<string>? ThumbnailAvailable;
    internal event EventHandler<ThumbnailUrlFailure>? ThumbnailDownloadFailed;
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
    internal Action? ProbeFindingUrls;
    internal Action? ProbeUrlsFound;
    internal Func<string, ThumbnailMode, CancellationToken, Task>? ProbeDecoding;
    internal int ProbeWaitingDecodeCount => _decodeQueue.WaitingCount;
    internal int ProbeDecodePrioritizations;
#endif

    internal ArticleThumbnailService(DispatcherQueue dispatcher,
        Func<(ArticleThumbnailFinder Finder, ThumbnailDownloader Downloads)>? createBackend = null,
        long cacheByteLimit = 128 * 1024 * 1024, Func<bool>? networkAllowsDownloads = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheByteLimit);
        _dispatcher = dispatcher;
        VerifyAccess();
        _cacheByteLimit = cacheByteLimit;
        _networkAllowsDownloads = networkAllowsDownloads ?? NetworkAllowsDownloads;
        NetworkDownloadsAllowed = _networkAllowsDownloads();
        _backend = new(() => Task.Run(() =>
        {
            var backend = (createBackend ?? CreateBackend)();
            _finder = backend.Finder;
            backend.Finder.ThumbnailDownloadFailed += FinderDownloadFailed;
            backend.Downloads.DownloadFailed += BackendDownloadFailed;
            backend.Downloads.ImageDecodingFailed += BackendImageDecodingFailed;
            return backend;
        }));
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        NetworkMonitor.ConnectivityChanged += OnConnectivityChanged;
    }

    private void OnConnectivityChanged(object? sender, bool available) => OnNetworkStatusChanged(sender);

    private void OnNetworkStatusChanged(object? sender)
        => DispatchNotification(() =>
        {
            _finder?.ResetDecodingFailures();
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
        return RunOperationAsync(token => FindImageAsync(article, mode, baseUrl, token, resolvedUrl), ct);
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

    internal Task FindAsync(IEnumerable<Article> articles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = articles.ToArray();
        var baseUrls = snapshot.DistinctBy(article => article.ArticleID)
            .ToDictionary(article => article.ArticleID, ArticleBaseUrl, StringComparer.Ordinal);
        return RunOperationAsync(async token =>
        {
            var backend = await _backend.Value.ConfigureAwait(false);
#if PARITY_PROBE
            ProbeFindingUrls?.Invoke();
#endif
            await backend.Finder.FindThumbnailUrlsAsync(snapshot, token, article => baseUrls[article.ArticleID]).ConfigureAwait(false);
#if PARITY_PROBE
            ProbeUrlsFound?.Invoke();
#endif
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
        return backend.Finder.ThumbnailUrl(article, mode, baseUrl);
    }

    private async Task<SoftwareBitmapSource?> FindImageAsync(Article article, ThumbnailMode mode,
        Uri? baseUrl, CancellationToken ct, string? resolvedUrl = null)
    {
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var url = resolvedUrl ?? await ResolveUrlAsync(article, mode, baseUrl, ct).ConfigureAwait(false);
        while (url is not null && tried.Add(url))
        {
            var backend = await _backend.Value.ConfigureAwait(false);
            string imageUrl = url;
            var key = (imageUrl, mode);
            Lazy<Task<SoftwareBitmapSource?>> pending;
            lock (_gate)
            {
                if (_pending.TryGetValue(key, out var existing)) pending = existing;
                else
                {
                    pending = new(() => LoadImageAsync(imageUrl, mode, article.Title, backend.Finder, backend.Downloads, ct));
                    _pending.Add(key, pending);
                }
                _decodeQueue.Prioritize(DecodeKey(imageUrl, mode));
#if PARITY_PROBE
                Interlocked.Increment(ref ProbeDecodePrioritizations);
#endif
            }
            backend.Downloads.PrioritizeDownload(url);
            try
            {
                if (await pending.Value.ConfigureAwait(false) is { } image) return image;
            }
            finally
            {
                lock (_gate)
                    if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(key);
            }
            url = await ResolveUrlAsync(article, mode, baseUrl, ct).ConfigureAwait(false);
        }
        return null;
    }

    private void BackendDownloadFailed(object? sender, string url)
        => (_finder ?? throw new InvalidOperationException("The thumbnail finder is not initialized.")).ThumbnailUrlDidFail(url);

    private void BackendImageDecodingFailed(object? sender, string url)
        => (_finder ?? throw new InvalidOperationException("The thumbnail finder is not initialized.")).RecordDecodingFailure(url);

    private void FinderDownloadFailed(object? sender, ThumbnailUrlFailure failure)
        => DispatchNotification(() => ThumbnailDownloadFailed?.Invoke(this, failure));

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

    private static string DecodeKey(string url, ThumbnailMode mode) => $"{(int)mode}:{url}";

    private async Task<SoftwareBitmapSource?> LoadImageAsync(string url, ThumbnailMode mode, string? title,
        ArticleThumbnailFinder finder, ThumbnailDownloader downloads, CancellationToken ct)
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
        var downloaded = await downloads.SourceAsync(url, ct, title, _networkAllowsDownloads)
            .ConfigureAwait(false);
        if (downloaded is null) return null;
        ValueTask<ThumbnailTaskQueue.Lease?> admission;
        lock (_gate) admission = _decodeQueue.AcquireAsync(ct, DecodeKey(url, mode));
        using var lease = await admission.ConfigureAwait(false);
        if (lease is null) return null;
#if PARITY_PROBE
        if (ProbeDecoding is { } decoding) await decoding(url, mode, ct).ConfigureAwait(false);
#endif
        SoftwareBitmap decoded;
        try { decoded = await DecodeAsync(downloaded.Data, mode, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is InsignificantThumbnailDimensionsException || IsImageDecodingError(error))
        {
            Trace.TraceWarning($"Could not decode thumbnail {url}: {error.Message}");
            finder.RecordDecodingFailure(url);
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
        if (mode == ThumbnailMode.Large)
            scale = Math.Min(scale, 180 * WindowsScreen.MaxScreenScale() / frame.OrientedPixelHeight);
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
        _finder?.EmptyCache();
    }

    internal void EmptyHtmlCache()
    {
        VerifyAccess();
        _finder?.EmptyCache();
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
                    backend.Finder.ThumbnailDownloadFailed -= FinderDownloadFailed;
                    backend.Downloads.DownloadFailed -= BackendDownloadFailed;
                    backend.Downloads.ImageDecodingFailed -= BackendImageDecodingFailed;
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
