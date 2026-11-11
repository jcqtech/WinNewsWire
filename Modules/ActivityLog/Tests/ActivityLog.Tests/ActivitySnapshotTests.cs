using Xunit;
using Log = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.ActivityLog.Tests;

public sealed class ActivitySnapshotTests
{
    [Fact]
    public void SnapshotsRetainTheirStateAfterTheUnderlyingActivityChanges()
    {
        var log = new Log();
        int id = log.CreateActivity(ActivityOwner.App, ActivityKind.RefreshAll, "detail");
        var pending = Assert.Single(log.Snapshot().Pending);
        log.DidStart(id);
        var running = Assert.Single(log.Snapshot().Running);
        log.DidComplete(id, "done");
        var completed = Assert.Single(log.Snapshot().Completed);
        Assert.Equal(ActivityState.Pending, pending.State);
        Assert.Null(pending.EndDate);
        Assert.Equal(ActivityState.Running, running.State);
        Assert.Null(running.EndDate);
        Assert.Equal(ActivityState.Completed, completed.State);
        Assert.Equal("done", completed.CompletionMessage);
        Assert.Equal(pending.ID, completed.ID);
        Assert.NotEqual(pending, running);
    }

    [Fact]
    public async Task ConcurrentTransitionsDoNotDuplicateOrLoseActivitiesInSnapshots()
    {
        var log = new Log();
        var ids = Enumerable.Range(0, 100).Select(_ => log.CreateActivity(ActivityOwner.App, ActivityKind.RefreshAll)).ToArray();
        var tasks = ids.Select(id => Task.Run(() => { log.DidStart(id); log.DidComplete(id); })).ToArray();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var snapshot = log.Snapshot();
            var entries = snapshot.Pending.Concat(snapshot.Running).Concat(snapshot.Completed).ToArray();
            Assert.Equal(100, entries.Length);
            Assert.Equal(100, entries.Select(activity => activity.ID).Distinct().Count());
            Assert.All(snapshot.Pending, activity => Assert.Equal(ActivityState.Pending, activity.State));
            Assert.All(snapshot.Running, activity => Assert.Equal(ActivityState.Running, activity.State));
            Assert.All(snapshot.Completed, activity => Assert.Equal(ActivityState.Completed, activity.State));
        }
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void ActivityKindExposesItsNativeCaseAndAssociatedUrl()
    {
        var kind = ActivityKind.RefreshFeedContent("https://example.com/feed");
        Assert.Equal("refreshFeedContent", kind.Identifier);
        Assert.Equal("https://example.com/feed", kind.Url);
        Assert.Equal("Refreshing feed: Custom title", kind.DisplayName("Custom title"));
        Assert.Null(ActivityKind.RefreshAll.Url);
        Assert.Equal("refreshAll", ActivityKind.RefreshAll.Identifier);
    }
}
