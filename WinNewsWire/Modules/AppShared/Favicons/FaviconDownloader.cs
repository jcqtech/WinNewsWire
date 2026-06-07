using System.Collections.Concurrent;
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

    public SingleFaviconDownloader(string faviconURL)
    {
        FaviconURL = faviconURL;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(faviconURL)));
        CachePath = Path.Combine(AppConfig.FaviconsDirectory, key + Path.GetExtension(faviconURL));
    }

    public async Task<byte[]?> LoadAsync(CancellationToken ct = default)
    {
        if (Data is not null) return Data;
        if (File.Exists(CachePath))
        {
            try { Data = await File.ReadAllBytesAsync(CachePath, ct); return Data; } catch { }
        }
        try
        {
            var result = await Downloader.Shared.DownloadAsync(FaviconURL, null, ct);
            if (result.Data is not null && result.Data.Length > 0)
            {
                Data = result.Data;
                await File.WriteAllBytesAsync(CachePath, Data, ct);
                return Data;
            }
        }
        catch { }
        return null;
    }
}

/// <summary>Port of <c>FaviconDownloader</c> (simplified — no Notifications, no plist; just cache + download).</summary>
public sealed class FaviconDownloader
{
    public static FaviconDownloader Shared { get; } = new();
    private readonly ConcurrentDictionary<string, SingleFaviconDownloader> _cache = new();
    public event EventHandler<string>? FaviconDidBecomeAvailable;

    public async Task<byte[]?> FaviconAsync(Feed feed, CancellationToken ct = default)
    {
        var url = feed.FaviconUrl
                  ?? (feed.HomePageUrl is null ? null : GuessFaviconUrl(feed.HomePageUrl))
                  ?? GuessFaviconUrl(feed.Url);
        if (string.IsNullOrEmpty(url)) return null;
        var dl = _cache.GetOrAdd(url, u => new SingleFaviconDownloader(u));
        var data = await dl.LoadAsync(ct);
        if (data is not null) FaviconDidBecomeAvailable?.Invoke(this, url);
        return data;
    }

    /// <summary>Downloads (or re-uses cached) favicon for the feed and returns the file
    /// path on disk, suitable for binding to an <c>Image.Source</c>. Returns null if no
    /// favicon could be resolved.</summary>
    public async Task<string?> FaviconPathAsync(Feed feed, CancellationToken ct = default)
    {
        var url = feed.FaviconUrl
                  ?? (feed.HomePageUrl is null ? null : GuessFaviconUrl(feed.HomePageUrl))
                  ?? GuessFaviconUrl(feed.Url);
        if (string.IsNullOrEmpty(url)) return null;
        var dl = _cache.GetOrAdd(url, u => new SingleFaviconDownloader(u));
        var data = await dl.LoadAsync(ct);
        if (data is null || data.Length == 0) return null;
        FaviconDidBecomeAvailable?.Invoke(this, url);
        return dl.CachePath;
    }

    private static string? GuessFaviconUrl(string homePageUrl)
    {
        if (!Uri.TryCreate(homePageUrl, UriKind.Absolute, out var u)) return null;
        return $"{u.Scheme}://{u.Authority}/favicon.ico";
    }
}
