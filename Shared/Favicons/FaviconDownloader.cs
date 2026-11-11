using System.Diagnostics;
using WinNewsWire.Account;
using WinNewsWire.Core;
using WinNewsWire.Web;

namespace WinNewsWire.AppShared.Favicons;

/// <summary>Port of <c>SingleFaviconDownloader</c>. Downloads one favicon URL to disk and caches it.</summary>
public sealed class SingleFaviconDownloader
{
    public string FaviconURL { get; }
    public string CachePath { get; }
    public byte[]? Data { get; private set; }
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Downloader _downloader;

    public SingleFaviconDownloader(string faviconURL, string? cacheFolder = null, Downloader? downloader = null)
    {
        if (!Uri.TryCreate(faviconURL, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("A favicon URL must use HTTP or HTTPS.", nameof(faviconURL));
        FaviconURL = faviconURL;
        _downloader = downloader ?? Downloader.Shared;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(faviconURL)));
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".svg" or ".ico" or ".gif" or ".webp" or ".bmp")) extension = ".image";
        CachePath = Path.Combine(cacheFolder ?? AppConfig.FaviconsDirectory, key + extension);
    }

    public async Task<byte[]?> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Data is not null) return Data;
            if (File.Exists(CachePath))
            {
                try
                {
                    var cached = await File.ReadAllBytesAsync(CachePath, ct).ConfigureAwait(false);
                    if (cached.Length > 0) { Data = cached; return Data; }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning($"Could not read cached favicon {CachePath}: {error.Message}");
                }
            }
            var result = await _downloader.DownloadAsync(FaviconURL, null, ct, UserAgentStyle.Browser).ConfigureAwait(false);
            if (!result.Success) throw new HttpRequestException($"Favicon download returned HTTP {(int)result.StatusCode}.");
            if (result.Data is not { Length: > 0 }) throw new InvalidDataException("The favicon response is empty.");
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await FileUtilities.WriteAllBytesAtomicallyAsync(CachePath, result.Data, ct).ConfigureAwait(false);
            Data = result.Data;
            return Data;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            Trace.TraceWarning($"Could not load favicon {FaviconURL}: {error.Message}");
            return null;
        }
        finally { _loadGate.Release(); }
    }
}

/// <summary>Windows feed-model adapter for the native Images/HTMLMetadata download pipeline.</summary>
public sealed class FaviconDownloader
{
    public static FaviconDownloader Shared { get; } = new();
    public event EventHandler<string>? FaviconDidBecomeAvailable;
    public void EmptyCache()
    {
        if (Images.FeedImageDownloader.IsSharedCreated) Images.FeedImageDownloader.Shared.EmptyCache();
    }

    public async Task<byte[]?> FaviconAsync(Feed feed, CancellationToken ct = default)
    {
        var image = await GetImageAsync(feed, preferFeedIcon: false, ct);
        return image?.Data;
    }

    /// <summary>Downloads (or re-uses cached) favicon for the feed and returns the file
    /// path on disk, suitable for binding to an <c>Image.Source</c>. Returns null if no
    /// favicon could be resolved.</summary>
    public async Task<string?> FaviconPathAsync(Feed feed, CancellationToken ct = default)
    {
        var image = await GetImageAsync(feed, preferFeedIcon: false, ct);
        return image?.Path;
    }

    public async Task<string?> ImagePathAsync(Feed feed, CancellationToken ct = default)
    {
        var image = await GetImageAsync(feed, preferFeedIcon: true, ct);
        return image?.Path;
    }

    private async Task<Images.DownloadedImage?> GetImageAsync(Feed feed, bool preferFeedIcon, CancellationToken ct)
    {
        var source = new Images.FeedImageSource(feed.Url, feed.HomePageUrl, feed.FaviconUrl, feed.IconUrl);
        try
        {
            ct.ThrowIfCancellationRequested();
            var service = await Images.FeedImageDownloader.GetSharedAsync().WaitAsync(ct);
            var image = preferFeedIcon ? await service.IconAsync(source, ct) : null;
            image ??= await service.FaviconAsync(source, ct);
            if (image is not null) FaviconDidBecomeAvailable?.Invoke(this, image.Url);
            return image;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
            or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or ArgumentException)
        {
            Trace.TraceWarning($"Could not load a feed image for {feed.Url}: {error}");
            return null;
        }
    }
}
