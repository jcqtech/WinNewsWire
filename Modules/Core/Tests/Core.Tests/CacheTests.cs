using WinNewsWire.Core;
using Xunit;

namespace Core.Tests;

public sealed class CacheTests
{
    [Fact]
    public void ExpirationMatchesTheNativeStrictBoundary()
    {
        var clock = new Clock();
        var cache = new Cache<Entry>(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1), clock);
        var entry = new Entry(clock.GetUtcNow());
        cache["key"] = entry;
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.Same(entry, cache["key"]);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.Null(cache["key"]);
    }

    [Fact]
    public void ReplacingARecordRestartsItsLifetime()
    {
        var clock = new Clock();
        var cache = new Cache<Entry>(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1), clock);
        cache["key"] = new(clock.GetUtcNow());
        clock.Now += TimeSpan.FromMinutes(2);
        var replacement = new Entry(clock.GetUtcNow());
        cache["key"] = replacement;
        clock.Now += TimeSpan.FromMinutes(2);
        cache.Cleanup();
        Assert.Same(replacement, cache["key"]);
    }

    [Fact]
    public void NilAssignmentAndRemoveAllDiscardRecords()
    {
        var cache = new Cache<Entry>(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1));
        cache["a"] = new(DateTimeOffset.UtcNow);
        cache["b"] = new(DateTimeOffset.UtcNow);
        cache["a"] = null;
        Assert.Null(cache["a"]);
        Assert.NotNull(cache["b"]);
        cache.RemoveAll();
        Assert.Null(cache["b"]);
    }

    [Fact]
    public void ConcurrentReadersAndWritersRetainIndependentKeys()
    {
        var cache = new Cache<Entry>(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(1));
        Parallel.For(0, 500, i =>
        {
            var entry = new Entry(DateTimeOffset.UtcNow);
            cache[i.ToString()] = entry;
            Assert.Same(entry, cache[i.ToString()]);
        });
    }

    private sealed record Entry(DateTimeOffset DateCreated) : ICacheRecord;
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
