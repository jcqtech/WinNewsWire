using WinNewsWire.Core;

namespace WinNewsWire.Web;

internal sealed class DownloadCache
{
    public static DownloadCache Shared { get; } = new();
    private readonly Cache<DownloadCacheRecord> _cache;
    private readonly TimeProvider _clock;

    public DownloadCache(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _cache = new(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1), _clock);
    }

    public DownloadResult? this[string key] => _cache[key]?.Result;
    public void Add(string key, DownloadResult result) => _cache[key] = new(result, _clock.GetUtcNow());
    public void RemoveAll() => _cache.RemoveAll();

    private sealed record DownloadCacheRecord(DownloadResult Result, DateTimeOffset DateCreated) : ICacheRecord;
}
