using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Account.Tests;

public class CombinedRefreshProgressTests
{
    [Fact]
    public void OverlappingAccountsShareProgressAndANewRefreshStartsFresh()
    {
        var combined = new CombinedRefreshProgress();
        using (var first = combined.BeginRefresh("first"))
        using (var second = combined.BeginRefresh("second"))
        {
            first.Report(new ProgressInfo(2, 1, 3));
            second.Report(new ProgressInfo(4, 0, 4));
            Assert.Equal(new ProgressInfo(6, 1, 7), combined.Snapshot);
            first.Dispose();
            Assert.Equal(new ProgressInfo(4, 3, 7), combined.Snapshot);
        }
        Assert.True(combined.IsComplete);
        using var next = combined.BeginRefresh("second");
        next.Report(new ProgressInfo(1, 0, 1));
        Assert.Equal(new ProgressInfo(1, 0, 1), combined.Snapshot);
        Assert.False(combined.IsComplete);
    }

    [Fact]
    public void OldSessionReportsCannotOverwriteANewRefresh()
    {
        var combined = new CombinedRefreshProgress();
        var old = combined.BeginRefresh("account");
        old.Report(new ProgressInfo(5, 0, 5));
        combined.Reset();
        using var current = combined.BeginRefresh("account");
        current.Report(new ProgressInfo(2, 0, 2));
        old.Report(new ProgressInfo(0, 5, 5));
        old.Dispose();
        Assert.Equal(new ProgressInfo(2, 0, 2), combined.Snapshot);
        Assert.False(combined.IsComplete);
    }

    [Fact]
    public async Task ConcurrentAccountReportsRemainConsistentAndComplete()
    {
        var combined = new CombinedRefreshProgress();
        var reporters = Enumerable.Range(0, 16).Select(index => combined.BeginRefresh(index.ToString())).ToArray();
        var snapshots = new System.Collections.Concurrent.ConcurrentQueue<ProgressInfo>();
        combined.ProgressChanged += (_, _) => snapshots.Enqueue(combined.Snapshot);
        await Task.WhenAll(reporters.Select(reporter => Task.Run(() =>
        {
            for (int completed = 0; completed <= 100; completed++)
                reporter.Report(new ProgressInfo(100 - completed, completed, 100));
            reporter.Dispose();
        })));
        Assert.Equal(new ProgressInfo(0, 1600, 1600), combined.Snapshot);
        Assert.All(snapshots, snapshot =>
            Assert.Equal(snapshot.NumberOfTasks, snapshot.NumberCompleted + snapshot.NumberRemaining));
        Assert.True(combined.IsComplete);
    }

    [Fact]
    public async Task ProgressHandlersRunOutsideTheStateLock()
    {
        var combined = new CombinedRefreshProgress();
        using var reporter = combined.BeginRefresh("account");
        var observed = new TaskCompletionSource<ProgressInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        combined.ProgressChanged += (_, _) =>
        {
            var read = Task.Run(() => combined.Snapshot);
            if (!read.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("A progress callback held the aggregation lock.");
            observed.TrySetResult(read.Result);
        };
        reporter.Report(new ProgressInfo(1, 1, 2));
        Assert.Equal(new ProgressInfo(1, 1, 2), await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
