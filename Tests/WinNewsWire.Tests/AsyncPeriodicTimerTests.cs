using WinNewsWire.AppShared.Timer;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class AsyncPeriodicTimerTests
{
    [Fact]
    public async Task RestartCancelsTheOldTickWithoutOverlappingItsReplacement()
    {
        var firstStarted = Signal();
        var nextStarted = Signal();
        int calls = 0, active = 0, maximum = 0;
        var errors = new List<Exception>();
        await using var timer = new AsyncPeriodicTimer(async ct =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            try
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStarted.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                else nextStarted.TrySetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        }, errors.Add);
        timer.Start(TimeSpan.FromMilliseconds(1));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timer.Start(TimeSpan.FromMilliseconds(1));
        await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maximum);
        Assert.Equal(0, active);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task StopWaitsForCleanupAndDisposalRejectsRestart()
    {
        var started = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        var timer = new AsyncPeriodicTimer(async ct =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally
            {
                cancelled.SetResult();
                await cleanup.Task;
            }
        }, error => throw new InvalidOperationException("Unexpected timer error", error));
        timer.Start(TimeSpan.FromMilliseconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = timer.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopping.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => timer.Start(TimeSpan.FromSeconds(1)));
        cleanup.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.DisposeAsync();
    }

    [Fact]
    public async Task FailedTicksAreReportedAndLaterTicksStillRun()
    {
        var succeeded = Signal();
        var errors = new List<Exception>();
        int calls = 0;
        await using var timer = new AsyncPeriodicTimer(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("temporary failure");
            succeeded.TrySetResult();
            return Task.CompletedTask;
        }, errors.Add);
        timer.Start(TimeSpan.FromMilliseconds(1));
        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(Assert.Single(errors));
    }

    [Fact]
    public async Task ManualIntervalHasNoWorkAndInvalidIntervalsAreRejected()
    {
        int calls = 0;
        await using var timer = new AsyncPeriodicTimer(_ => { calls++; return Task.CompletedTask; }, _ => { });
        timer.Start(null);
        await timer.StopAsync();
        Assert.Equal(0, calls);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Start(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Start(TimeSpan.FromTicks(-1)));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
