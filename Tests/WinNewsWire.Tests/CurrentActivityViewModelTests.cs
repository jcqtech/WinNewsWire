using WinNewsWire.ActivityLog;
using WinNewsWire.AppShared.CurrentActivity;
using Xunit;
using Log = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.AppShared.Tests;

public sealed class CurrentActivityViewModelTests
{
    [Fact]
    public void RunningThenPendingThenRecentCompletionsMatchNativeOrdering()
    {
        var clock = new Clock();
        var log = new Log(clock);
        int pending = log.CreateActivity(ActivityOwner.App, ActivityKind.RefreshAll);
        int running = log.CreateActivity(ActivityOwner.App, ActivityKind.RefreshArticles);
        log.DidStart(running);
        int done = log.CreateActivity(ActivityOwner.App, ActivityKind.VacuumDatabase);
        log.DidComplete(done);
        using var model = new CurrentActivityViewModel(log, action => action(), clock);
        model.Start();
        Assert.Equal(new[] { running, pending, done }, model.DisplayedActivities.Select(activity => activity.ID));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { running, pending }, model.DisplayedActivities.Select(activity => activity.ID));
    }

    [Fact]
    public void HalfSecondUpdatesObserveStateChangesWithoutChangingIdentity()
    {
        var clock = new Clock();
        var log = new Log(clock);
        int id = log.CreateActivity(ActivityOwner.App, ActivityKind.RefreshAll);
        using var model = new CurrentActivityViewModel(log, action => action(), clock);
        int changes = 0;
        model.DisplayedActivitiesDidChange += (_, _) => changes++;
        model.Start();
        log.DidStart(id);
        Assert.Equal(ActivityState.Pending, Assert.Single(model.DisplayedActivities).State);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal(1, changes);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(ActivityState.Running, Assert.Single(model.DisplayedActivities).State);
        Assert.Equal(2, changes);
        log.DidFail(id, new IOException("Fixture failure"));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(ActivityState.Failed, Assert.Single(model.DisplayedActivities).State);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(model.DisplayedActivities);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Empty(model.DisplayedActivities);
        Assert.Equal(0, clock.ActiveTimers);
        log.CreateActivity(ActivityOwner.App, ActivityKind.RefreshAll);
        Assert.Single(model.DisplayedActivities);
        Assert.Equal(1, clock.ActiveTimers);
    }

    [Fact]
    public void StopInvalidatesQueuedCallbacksAndDisposalRejectsRestart()
    {
        var clock = new Clock();
        var log = new Log(clock);
        var queued = new Queue<Action>();
        var model = new CurrentActivityViewModel(log, queued.Enqueue, clock);
        log.CreateActivity(ActivityOwner.App, ActivityKind.RefreshAll);
        model.Start();
        model.Stop();
        while (queued.TryDequeue(out var callback)) callback();
        Assert.Empty(model.DisplayedActivities);
        Assert.Equal(0, clock.ActiveTimers);
        model.Start();
        while (queued.TryDequeue(out var callback)) callback();
        Assert.Single(model.DisplayedActivities);
        model.Dispose();
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Throws<ObjectDisposedException>(model.Start);
    }

    [Fact]
    public void EmptyStartDoesNotLeaveAPollingTimerRunning()
    {
        var clock = new Clock();
        using var model = new CurrentActivityViewModel(new Log(clock), action => action(), clock);
        model.Start();
        Assert.Empty(model.DisplayedActivities);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public void FeedTitlesAndAssociatedUrlsRemainSeparate()
    {
        var kind = ActivityKind.RefreshFeedContent("https://example.com/feed");
        var named = new Activity(1, ActivityOwner.App, kind, "A feed").Snapshot();
        var unnamed = new Activity(2, ActivityOwner.App, kind).Snapshot();
        Assert.Equal(new ActivityDisplayText("A feed", "https://example.com/feed"), CurrentActivityViewModel.DisplayText(named));
        Assert.Equal(new ActivityDisplayText("https://example.com/feed", null), CurrentActivityViewModel.DisplayText(unnamed));
        var ordinary = new Activity(3, ActivityOwner.App, ActivityKind.RefreshArticles, "page 2").Snapshot();
        Assert.Equal(new ActivityDisplayText("Refreshing articles", "page 2"), CurrentActivityViewModel.DisplayText(ordinary));
    }

    [Fact]
    public void UrlActivitiesUseTheirNativeLabels()
    {
        const string url = "https://example.com";
        foreach (var (kind, title) in new[]
        {
            (ActivityKind.FindFeed(url), "Finding feed"),
            (ActivityKind.FetchFeedCandidate(url), "Fetching"),
            (ActivityKind.DownloadFeedImage(url), "Downloading image"),
            (ActivityKind.DownloadFavicon(url), "Downloading favicon"),
            (ActivityKind.DownloadAvatar(url), "Downloading avatar"),
            (ActivityKind.DownloadHtmlMetadata(url), "Downloading metadata"),
            (ActivityKind.DownloadThumbnail(url), "Downloading thumbnail")
        })
            Assert.Equal(new ActivityDisplayText(title, url),
                CurrentActivityViewModel.DisplayText(new Activity(1, ActivityOwner.App, kind, "not the URL").Snapshot()));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public int ActiveTimers => _timers.Count(timer => timer.Next is not null);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            var target = _now + duration;
            while (_timers.Where(timer => timer.Next is not null && timer.Next <= target)
                .MinBy(timer => timer.Next) is { } due)
            {
                _now = due.Next!.Value;
                due.Fire();
            }
            _now = target;
        }
        private sealed class ManualTimer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Next { get; private set; }
            private TimeSpan _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                Next = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }
            public void Fire()
            {
                Next = _period > TimeSpan.Zero ? clock._now + _period : null;
                callback(state);
            }
            public void Dispose() => Next = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
