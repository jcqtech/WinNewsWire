using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class DownloadLimiterTests
{
    [Fact]
    public async Task WaitingDownloadsStartMostRecentFirst()
    {
        var limiter = new DownloadLimiter(1);
        using var first = await limiter.AcquireAsync();
        var older = limiter.AcquireAsync().AsTask();
        var newest = limiter.AcquireAsync().AsTask();
        Assert.False(older.IsCompleted);
        Assert.False(newest.IsCompleted);
        first.Dispose();
        using var newestLease = await newest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(older.IsCompleted);
        newestLease.Dispose();
        using var olderLease = await older.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CanceledWaiterDoesNotConsumeOrLeakADownloadSlot()
    {
        var limiter = new DownloadLimiter(1);
        using var first = await limiter.AcquireAsync();
        var older = limiter.AcquireAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        var canceled = limiter.AcquireAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        first.Dispose();
        using var next = await older.WaitAsync(TimeSpan.FromSeconds(5));
        var after = limiter.AcquireAsync().AsTask();
        Assert.False(after.IsCompleted);
        next.Dispose();
        using var afterLease = await after.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FourSlotsStayBoundedEvenWhenALeaseIsDisposedTwice()
    {
        var limiter = new DownloadLimiter(4);
        var active = new List<IDisposable>();
        try
        {
            for (int index = 0; index < 4; index++) active.Add(await limiter.AcquireAsync());
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
}
