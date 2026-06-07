using System.Collections.Concurrent;

namespace WinNewsWire.Web;

/// <summary>
/// Port of <c>DownloadCache</c>. Short-lived in-memory cache for download responses.
/// TTL is 13 minutes with periodic cleanup every 2 minutes.
/// </summary>
internal sealed class DownloadCache : IDisposable
{
    public static DownloadCache Shared { get; } = new();

    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(13);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, DownloadCacheRecord> _cache = new();
    private readonly Timer _cleanupTimer;

    public DownloadCache()
    {
        _cleanupTimer = new Timer(_ => Cleanup(), null, CleanupInterval, CleanupInterval);
    }

    public DownloadCacheRecord? this[string key]
    {
        get
        {
            if (_cache.TryGetValue(key, out var record))
            {
                if (DateTime.UtcNow - record.DateCreated < TimeToLive)
                    return record;
                _cache.TryRemove(key, out _);
            }
            return null;
        }
    }

    public void Add(string urlString, byte[]? data, int statusCode, IReadOnlyDictionary<string, string>? responseHeaders)
    {
        var record = new DownloadCacheRecord(data, statusCode, responseHeaders);
        _cache[urlString] = record;
    }

    public void RemoveAll()
    {
        _cache.Clear();
    }

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow - TimeToLive;
        foreach (var kvp in _cache)
        {
            if (kvp.Value.DateCreated < cutoff)
                _cache.TryRemove(kvp.Key, out _);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}

/// <summary>Immutable snapshot of a cached download response (not an <c>HttpResponseMessage</c>).</summary>
internal sealed record DownloadCacheRecord(
    byte[]? Data,
    int StatusCode,
    IReadOnlyDictionary<string, string>? ResponseHeaders)
{
    public DateTime DateCreated { get; } = DateTime.UtcNow;
}
