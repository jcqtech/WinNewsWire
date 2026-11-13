using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class ThumbnailTaskQueueTests
{
    [Fact]
    public async Task BusyGroupsLeaveCapacityForEligibleGroupsAndPreserveNewestFirst()
    {
        var queue = new ThumbnailTaskQueue(2, 10, maximumRunningCountPerGroup: 1);
        using var slow = await queue.AcquireAsync(group: "slow");
        Assert.NotNull(slow);
        var olderSlow = queue.AcquireAsync(group: "slow").AsTask();
        Assert.False(olderSlow.IsCompleted);
        using var fast = await queue.AcquireAsync(group: "fast");
        Assert.NotNull(fast);
        var nextFast = queue.AcquireAsync(group: "fast", priority: ThumbnailPriority.Prefetch).AsTask();
        var newerSlow = queue.AcquireAsync(group: "slow").AsTask();
        fast.Dispose();
        using var fastAgain = await nextFast.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(fastAgain);
        Assert.False(newerSlow.IsCompleted);
        slow.Dispose();
        using var newer = await newerSlow.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(newer);
        Assert.False(olderSlow.IsCompleted);
        newer.Dispose();
        using var older = await olderSlow.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(older);
    }

    [Fact]
    public async Task ReaderBypassesGroupLimitButNormalWorkWaitsForTheGroupToHaveRoomAgain()
    {
        var queue = new ThumbnailTaskQueue(4, 10, maximumRunningCountPerGroup: 1);
        using var active = await queue.AcquireAsync(group: "host");
        Assert.NotNull(active);
        var reader = queue.AcquireAsync(key: "reader", group: "host", priority: ThumbnailPriority.Prefetch).AsTask();
        var other = queue.AcquireAsync(group: "host").AsTask();
        Assert.False(reader.IsCompleted);
        queue.StartNow("reader");
        using var readerLease = await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(readerLease);
        readerLease.Dispose();
        Assert.False(other.IsCompleted);
        active.Dispose();
        using var otherLease = await other.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(otherLease);
    }

    [Fact]
    public async Task ReaderPromotionStartsImmediatelyButNormalWorkWaitsUntilBelowTheLimitAgain()
    {
        var limiter = new ThumbnailTaskQueue(1);
        using var active = Assert.IsAssignableFrom<IDisposable>(await limiter.AcquireAsync());
        var article = limiter.AcquireAsync(key: "article", priority: ThumbnailPriority.Prefetch).AsTask();
        var other = limiter.AcquireAsync(key: "other").AsTask();
        limiter.StartNow("article");
        using var articleLease = Assert.IsAssignableFrom<IDisposable>(await article.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(other.IsCompleted);
        articleLease.Dispose();
        Assert.Equal(1, limiter.WaitingCount);
        Assert.False(other.IsCompleted);
        limiter.StartNow("article");
        limiter.StartNow("missing");
        active.Dispose();
        using var otherLease = Assert.IsAssignableFrom<IDisposable>(await other.WaitAsync(TimeSpan.FromSeconds(5)));
        var last = limiter.AcquireAsync().AsTask();
        Assert.False(last.IsCompleted);
        otherLease.Dispose();
        using var lastLease = Assert.IsAssignableFrom<IDisposable>(await last.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitingDownloadsStartMostRecentFirst()
    {
        var limiter = new ThumbnailTaskQueue(1);
        using var first = Assert.IsAssignableFrom<IDisposable>(await limiter.AcquireAsync());
        var older = limiter.AcquireAsync().AsTask();
        var newest = limiter.AcquireAsync().AsTask();
        Assert.False(older.IsCompleted);
        Assert.False(newest.IsCompleted);
        first.Dispose();
        using var newestLease = Assert.IsAssignableFrom<IDisposable>(await newest.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(older.IsCompleted);
        newestLease.Dispose();
        using var olderLease = await older.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CanceledWaiterDoesNotConsumeOrLeakADownloadSlot()
    {
        var limiter = new ThumbnailTaskQueue(1);
        using var first = Assert.IsAssignableFrom<IDisposable>(await limiter.AcquireAsync());
        var older = limiter.AcquireAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        var canceled = limiter.AcquireAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        first.Dispose();
        using var next = Assert.IsAssignableFrom<IDisposable>(await older.WaitAsync(TimeSpan.FromSeconds(5)));
        var after = limiter.AcquireAsync().AsTask();
        Assert.False(after.IsCompleted);
        next.Dispose();
        using var afterLease = await after.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FourSlotsStayBoundedEvenWhenALeaseIsDisposedTwice()
    {
        var limiter = new ThumbnailTaskQueue(4);
        var active = new List<IDisposable>();
        try
        {
            for (int index = 0; index < 4; index++) active.Add(Assert.IsAssignableFrom<IDisposable>(await limiter.AcquireAsync()));
            var fifth = limiter.AcquireAsync().AsTask();
            Assert.False(fifth.IsCompleted);
            active[0].Dispose();
            active[0].Dispose();
            using var fifthLease = await fifth.WaitAsync(TimeSpan.FromSeconds(5));
            var sixth = limiter.AcquireAsync().AsTask();
            Assert.False(sixth.IsCompleted);
            active[1].Dispose();
            using var sixthLease = await sixth.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (var lease in active) lease.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisibleDownloadsPrecedeNewerPrefetchesAndPromotedRowsMoveToTheFront(bool promote)
    {
        var limiter = new ThumbnailTaskQueue(1, 10);
        using var active = Assert.IsAssignableFrom<IDisposable>(await limiter.AcquireAsync());
        var olderPrefetch = limiter.AcquireAsync(key: "older", priority: ThumbnailPriority.Prefetch).AsTask();
        var visible = limiter.AcquireAsync(key: "visible").AsTask();
        var newerPrefetch = limiter.AcquireAsync(key: "newer", priority: ThumbnailPriority.Prefetch).AsTask();
        if (promote) limiter.Prioritize("older");
        active.Dispose();
        var first = promote ? olderPrefetch : visible;
        using var firstLease = Assert.IsAssignableFrom<IDisposable>(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(newerPrefetch.IsCompleted);
        firstLease.Dispose();
        var second = promote ? visible : newerPrefetch;
        using var secondLease = Assert.IsAssignableFrom<IDisposable>(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        var last = promote ? newerPrefetch : olderPrefetch;
        Assert.False(last.IsCompleted);
        secondLease.Dispose();
        using var lastLease = Assert.IsAssignableFrom<IDisposable>(await last.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task OverflowDropsPrefetchesBeforeEvenTheOldestVisibleDownload()
    {
        var limiter = new ThumbnailTaskQueue(1, 2);
        using var active = Assert.IsAssignableFrom<IDisposable>(await limiter.AcquireAsync());
        var olderVisible = limiter.AcquireAsync(key: "older-visible").AsTask();
        var olderPrefetch = limiter.AcquireAsync(key: "older-prefetch", priority: ThumbnailPriority.Prefetch).AsTask();
        var newerPrefetch = limiter.AcquireAsync(key: "newer-prefetch", priority: ThumbnailPriority.Prefetch).AsTask();
        Assert.Null(await olderPrefetch.WaitAsync(TimeSpan.FromSeconds(5)));
        var newerVisible = limiter.AcquireAsync(key: "newer-visible").AsTask();
        Assert.Null(await newerPrefetch.WaitAsync(TimeSpan.FromSeconds(5)));
        active.Dispose();
        using var newestLease = Assert.IsAssignableFrom<IDisposable>(await newerVisible.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(olderVisible.IsCompleted);
        newestLease.Dispose();
        using var oldestLease = Assert.IsAssignableFrom<IDisposable>(await olderVisible.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task OverflowDropsTheOldestWaiterWithoutLeakingOrReorderingRemainingSlots(int maximumWaiting)
    {
        var limiter = new ThumbnailTaskQueue(1, maximumWaiting);
        using var active = Assert.IsAssignableFrom<IDisposable>(await limiter.AcquireAsync());
        var oldest = limiter.AcquireAsync().AsTask();
        var retained = Enumerable.Range(0, maximumWaiting).Select(_ => limiter.AcquireAsync().AsTask()).ToArray();
        Assert.Null(await oldest.WaitAsync(TimeSpan.FromSeconds(5)));
        active.Dispose();
        for (int index = retained.Length - 1; index >= 0; index--)
        {
            using var lease = Assert.IsAssignableFrom<IDisposable>(await retained[index].WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.All(retained.Take(index), pending => Assert.False(pending.IsCompleted));
        }
        using var retried = Assert.IsAssignableFrom<IDisposable>(await limiter.AcquireAsync());
        using var cancellation = new CancellationTokenSource();
        var waitingAgain = limiter.AcquireAsync(cancellation.Token).AsTask();
        if (maximumWaiting == 0) Assert.Null(await waitingAgain);
        else
        {
            Assert.False(waitingAgain.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingAgain);
        }
    }
}
