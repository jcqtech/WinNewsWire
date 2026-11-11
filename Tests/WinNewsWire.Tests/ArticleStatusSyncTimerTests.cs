using WinNewsWire.AppShared.Timer;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ArticleStatusSyncTimerTests
{
    [Fact]
    public void IdleAndActiveRunsUseTheNativeIntervalsFromTheStartOfTheRun()
    {
        var clock = new Clock();
        var schedule = new ArticleStatusSyncSchedule(ArticleStatusSyncTimer.NormalInterval, ArticleStatusSyncTimer.IdleInterval, clock);
        Assert.Equal(TimeSpan.FromMinutes(2), schedule.NextDelay);
        clock.Now += TimeSpan.FromMinutes(2);
        var version = schedule.Started();
        clock.Now += TimeSpan.FromSeconds(5);
        schedule.Completed(false, version);
        Assert.True(schedule.LastRunWasIdle);
        Assert.Equal(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(5), schedule.NextDelay);
        schedule.Completed(true, version);
        Assert.False(schedule.LastRunWasIdle);
        Assert.Equal(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(5), schedule.NextDelay);
    }

    [Theory]
    [InlineData(30, 90)]
    [InlineData(300, 120)]
    public void QueuedEditsExitIdleBackoffWithoutSchedulingAnOverdueFire(int elapsedSeconds, int expectedDelay)
    {
        var clock = new Clock();
        var schedule = new ArticleStatusSyncSchedule(ArticleStatusSyncTimer.NormalInterval, ArticleStatusSyncTimer.IdleInterval, clock);
        schedule.Completed(false, schedule.Started());
        clock.Now += TimeSpan.FromSeconds(elapsedSeconds);
        Assert.True(schedule.StatusesQueued());
        Assert.False(schedule.LastRunWasIdle);
        Assert.Equal(TimeSpan.FromSeconds(expectedDelay), schedule.NextDelay);
    }

    [Fact]
    public void EditsQueuedDuringTheRunPreventItsNoOpResultFromRearmingBackoff()
    {
        var schedule = new ArticleStatusSyncSchedule(ArticleStatusSyncTimer.NormalInterval, ArticleStatusSyncTimer.IdleInterval, new Clock());
        var version = schedule.Started();
        schedule.StatusesQueued();
        schedule.Completed(false, version);
        Assert.False(schedule.LastRunWasIdle);
    }

    [Fact]
    public async Task IdleTimerWakesForAnEditInsteadOfWaitingForTheLongInterval()
    {
        var second = Signal();
        int calls = 0;
        await using var timer = new ArticleStatusSyncTimer(_ =>
        {
            if (Interlocked.Increment(ref calls) > 1) second.TrySetResult();
            return Task.FromResult(false);
        }, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(1));
        timer.Start();
        await WaitUntil(() => timer.IsIdle);
        await Task.Delay(60);
        Assert.Equal(1, Volatile.Read(ref calls));
        timer.NotifyStatusesQueued();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.StopAsync();
        Assert.InRange(calls, 2, 3);
    }

    [Fact]
    public async Task AQueuedEditDoesNotCancelTheRunningSyncAndDoesNotGetLostInItsResult()
    {
        var first = Signal();
        var release = Signal();
        var second = Signal();
        CancellationToken runningToken = default;
        int calls = 0;
        await using var timer = new ArticleStatusSyncTimer(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                runningToken = ct;
                first.SetResult();
                await release.Task.WaitAsync(ct);
            }
            else second.TrySetResult();
            return false;
        }, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(1));
        timer.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timer.NotifyStatusesQueued();
        Assert.False(runningToken.IsCancellationRequested);
        release.SetResult();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.StopAsync();
    }

    [Fact]
    public async Task StopDrainsTheRunningSyncAndDisposalIsIdempotent()
    {
        var started = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        var timer = new ArticleStatusSyncTimer(async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.SetResult(); await cleanup.Task; }
            return false;
        }, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(1));
        timer.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = timer.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        cleanup.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(timer.Start);
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, cancellation.Token);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
