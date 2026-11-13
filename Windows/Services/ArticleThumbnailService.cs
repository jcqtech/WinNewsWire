using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
using WinNewsWire.Web;

namespace WinNewsWire.Services;

internal sealed class ArticleThumbnailService : IAsyncDisposable
{
    private sealed record CachedThumbnail(string Url, ThumbnailMode Mode, SoftwareBitmapSource Source, long ByteCount);
    private readonly DispatcherQueue _dispatcher;
    private readonly Lazy<Task<(ArticleThumbnailFinder Finder, ThumbnailDownloader Downloads)>> _backend;
    private readonly long _cacheByteLimit;
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
#if PARITY_PROBE
    internal int ProbeDecodes;
    internal bool ProbeDecodedOnUIThread;
    internal long ProbeCacheBytes => _cacheBytes;
#endif

    internal ArticleThumbnailService(DispatcherQueue dispatcher,
        Func<(ArticleThumbnailFinder Finder, ThumbnailDownloader Downloads)>? createBackend = null,
        long cacheByteLimit = 128 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cacheByteLimit);
        _dispatcher = dispatcher;
        VerifyAccess();
        _cacheByteLimit = cacheByteLimit;
        _backend = new(() => Task.Run(createBackend ?? CreateBackend));
    }

    private static (ArticleThumbnailFinder, ThumbnailDownloader) CreateBackend()
    {
        var database = ImageMetadataDatabase.Shared;
        return (new(database), new(database, AppConfig.CachesDirectory, NetworkAllowsDownloads));
    }

    private static bool NetworkAllowsDownloads()
    {
        if (!NetworkMonitor.IsAvailable) return false;
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null) return false;
        var cost = profile.GetConnectionCost();
        return cost.NetworkCostType == NetworkCostType.Unrestricted
            && !cost.Roaming && !cost.OverDataLimit && !cost.ApproachingDataLimit;
    }

    internal Task<SoftwareBitmapSource?> ThumbnailAsync(Article article, ThumbnailMode mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (mode is not (ThumbnailMode.Small or ThumbnailMode.Large)) throw new ArgumentOutOfRangeException(nameof(mode));
        VerifyAccess();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            ct.ThrowIfCancellationRequested();
            var operation = Task.Run(() => FindImageAsync(article, mode, _lifetime.Token));
            _operations.Add(operation);
            _ = operation.ContinueWith(completed =>
            {
                lock (_gate) _operations.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return operation.WaitAsync(ct);
        }
    }

    private async Task<SoftwareBitmapSource?> FindImageAsync(Article article, ThumbnailMode mode, CancellationToken ct)
    {
        var backend = await _backend.Value.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (backend.Finder.ThumbnailUrl(article, mode) is null)
        {
            var discovery = _discovering.GetOrAdd(article.ArticleID,
                _ => new(() => backend.Finder.DiscoverThumbnailUrlsAsync([article], ct)));
            try { await discovery.Value.ConfigureAwait(false); }
            finally { _discovering.TryRemove(new KeyValuePair<string, Lazy<Task>>(article.ArticleID, discovery)); }
        }
        if (backend.Finder.ThumbnailUrl(article, mode) is not { } url) return null;
        var key = (url, mode);
        var pending = _pending.GetOrAdd(key, _ => new(() => LoadImageAsync(url, mode, article.Title, backend.Downloads, ct)));
        try { return await pending.Value.ConfigureAwait(false); }
        finally { _pending.TryRemove(new KeyValuePair<(string, ThumbnailMode), Lazy<Task<SoftwareBitmapSource?>>>(key, pending)); }
    }

    private async Task<SoftwareBitmapSource?> LoadImageAsync(string url, ThumbnailMode mode, string? title,
        ThumbnailDownloader downloads, CancellationToken ct)
    {
        var cached = await OnDispatcherAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!_cache.TryGetValue((url, mode), out var node)) return Task.FromResult<SoftwareBitmapSource?>(null);
            _cacheOrder.Remove(node);
            _cacheOrder.AddLast(node);
            return Task.FromResult<SoftwareBitmapSource?>(node.Value.Source);
        }).ConfigureAwait(false);
        if (cached is not null) return cached;
        var downloaded = await downloads.ImageAsync(url, mode, ct, title).ConfigureAwait(false);
        if (downloaded is null) return null;
        using var bitmap = await DecodeAsync(downloaded.Data, ct).ConfigureAwait(false);
        long byteCount = checked((long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
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

    private async Task<SoftwareBitmap> DecodeAsync(byte[] data, CancellationToken ct)
    {
#if PARITY_PROBE
        Interlocked.Increment(ref ProbeDecodes);
        if (_dispatcher.HasThreadAccess) ProbeDecodedOnUIThread = true;
#endif
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(data.AsBuffer()).AsTask(ct).ConfigureAwait(false);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(ct).ConfigureAwait(false);
    }

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
        if (_backend.IsValueCreated && _backend.Value.IsCompletedSuccessfully)
            _backend.Value.Result.Downloads.EmptyCache();
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
