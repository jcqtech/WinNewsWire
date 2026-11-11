namespace WinNewsWire.Core;

public interface ICacheRecord
{
    DateTimeOffset DateCreated { get; }
}

public sealed class Cache<TRecord> where TRecord : class, ICacheRecord
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TRecord> _records = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private DateTimeOffset _lastCleanup;
    public TimeSpan TimeToLive { get; }
    public TimeSpan TimeBetweenCleanups { get; }

    public Cache(TimeSpan timeToLive, TimeSpan timeBetweenCleanups, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeToLive, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeBetweenCleanups, TimeSpan.Zero);
        TimeToLive = timeToLive;
        TimeBetweenCleanups = timeBetweenCleanups;
        _clock = clock ?? TimeProvider.System;
        _lastCleanup = _clock.GetUtcNow();
    }

    public TRecord? this[string key]
    {
        get
        {
            lock (_gate)
            {
                var now = _clock.GetUtcNow();
                CleanupIfNeeded(now);
                if (!_records.TryGetValue(key, out var value)) return null;
                if (now - value.DateCreated <= TimeToLive) return value;
                _records.Remove(key);
                return null;
            }
        }
        set
        {
            lock (_gate)
            {
                if (value is null) _records.Remove(key);
                else _records[key] = value;
            }
        }
    }

    public void Cleanup()
    {
        lock (_gate) CleanupIfNeeded(_clock.GetUtcNow());
    }
    public void RemoveAll()
    {
        lock (_gate) _records.Clear();
    }
    private void CleanupIfNeeded(DateTimeOffset now)
    {
        if (now - _lastCleanup <= TimeBetweenCleanups) return;
        foreach (var key in _records.Where(pair => now - pair.Value.DateCreated > TimeToLive).Select(pair => pair.Key).ToArray())
            _records.Remove(key);
        _lastCleanup = now;
    }
}
