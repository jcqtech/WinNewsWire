using Xunit;
using Log = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.ActivityLog.Tests;

public sealed class ActivityLogTests
{
    private static ActivityOwner Owner => ActivityOwner.Account("account1", "Account One");

    [Fact]
    public void CreateActivityAssignsIncrementingIDs()
    {
        var log = new Log();
        var first = log.CreateActivity(ActivityOwner.FeedFinder, ActivityKind.FindFeed("https://example.com"));
        var second = log.CreateActivity(ActivityOwner.FeedFinder, ActivityKind.FindFeed("https://example.org"));
        Assert.Equal(first + 1, second);
        Assert.Equal(2, log.PendingActivities.Count);
    }

    [Fact]
    public void CreatedActivityIsPending()
    {
        var log = new Log();
        log.CreateActivity(ActivityOwner.FeedFinder, ActivityKind.FindFeed("https://example.com"));
        Assert.Equal(ActivityState.Pending, log.PendingActivities[0].State);
        Assert.Null(log.PendingActivities[0].StartDate);
        Assert.Empty(log.RunningActivities);
    }

    [Fact]
    public void DidStartByKind()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.SendArticleStatuses);
        log.DidStart(Owner, ActivityKind.SendArticleStatuses);
        Assert.Empty(log.PendingActivities);
        var activity = Assert.Single(log.RunningActivities);
        Assert.Equal(ActivityState.Running, activity.State);
        Assert.NotNull(activity.StartDate);
    }

    [Fact]
    public void DidCompleteByKind()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.SendArticleStatuses);
        log.DidStart(Owner, ActivityKind.SendArticleStatuses);
        log.DidComplete(Owner, ActivityKind.SendArticleStatuses);
        Assert.Empty(log.PendingActivities);
        Assert.Empty(log.RunningActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Completed, activity.State);
        Assert.NotNull(activity.EndDate);
    }

    [Fact]
    public void LogCompletedActivityCompletesWithoutDuration()
    {
        var log = new Log();
        log.LogCompletedActivity(Owner, ActivityKind.SendArticleStatuses, message: "Done");
        Assert.Empty(log.PendingActivities);
        Assert.Empty(log.RunningActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Completed, activity.State);
        Assert.Equal("Done", activity.CompletionMessage);
        Assert.NotNull(activity.EndDate);
        Assert.Null(activity.StartDate);
        Assert.Null(activity.FormattedDuration);
    }

    [Fact]
    public async Task LogActivityReturnsResultAndCompletesWithMessage()
    {
        var log = new Log();
        var result = await log.LogActivityAsync(Owner, ActivityKind.SendArticleStatuses,
            async () => { await Task.Yield(); return 42; }, successMessage: value => $"sent {value}");
        Assert.Equal(42, result);
        Assert.Empty(log.PendingActivities);
        Assert.Empty(log.RunningActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Completed, activity.State);
        Assert.Equal("sent 42", activity.CompletionMessage);
        Assert.NotNull(activity.StartDate);
        Assert.NotNull(activity.EndDate);
    }

    [Fact]
    public async Task LogActivityRecordsFailureAndRethrows()
    {
        var log = new Log();
        await Assert.ThrowsAsync<TestError>(() => log.LogActivityAsync(Owner, ActivityKind.RefreshFeedList,
            async () => { await Task.Yield(); throw new TestError(); }));
        Assert.Empty(log.RunningActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Failed, activity.State);
        Assert.NotNull(activity.Error);
    }

    [Fact]
    public async Task LogActivitySuppressesDurationWhenNotSignificant()
    {
        var log = new Log();
        await log.LogActivityAsync(Owner, ActivityKind.SendArticleStatuses, async () => await Task.Yield(), durationIsSignificant: () => false);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Completed, activity.State);
        Assert.NotNull(activity.StartDate);
        Assert.Null(activity.FormattedDuration);
    }

    [Fact]
    public void LogActivitySyncOverloadCompletes()
    {
        var log = new Log();
        var result = log.LogActivity(Owner, ActivityKind.VacuumDatabase, () => "done");
        Assert.Equal("done", result);
        Assert.Equal(ActivityState.Completed, Assert.Single(log.CompletedActivities).State);
    }

    [Fact]
    public void LogActivitySyncOverloadRecordsFailureAndRethrows()
    {
        var log = new Log();
        Assert.Throws<TestError>(() => log.LogActivity<int>(Owner, ActivityKind.VacuumDatabase, () => throw new TestError()));
        Assert.Empty(log.RunningActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Failed, activity.State);
        Assert.NotNull(activity.Error);
    }

    [Fact]
    public void DidFailByKind()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.RefreshFeedList);
        log.DidStart(Owner, ActivityKind.RefreshFeedList);
        log.DidFail(Owner, ActivityKind.RefreshFeedList, new TestError());
        Assert.Empty(log.RunningActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Failed, activity.State);
        Assert.NotNull(activity.Error);
        Assert.NotNull(activity.EndDate);
    }

    [Fact]
    public void DidStartByID()
    {
        var log = new Log();
        var id = log.CreateActivity(ActivityOwner.FeedImageDownloader, ActivityKind.DownloadFeedImage("Daring Fireball"));
        log.DidStart(id);
        Assert.Empty(log.PendingActivities);
        Assert.Equal(ActivityState.Running, Assert.Single(log.RunningActivities).State);
    }

    [Fact]
    public void DidCompleteByID()
    {
        var log = new Log();
        var id = log.CreateActivity(ActivityOwner.FeedImageDownloader, ActivityKind.DownloadFeedImage("Daring Fireball"));
        log.DidStart(id);
        log.DidComplete(id);
        Assert.Empty(log.RunningActivities);
        Assert.Equal(ActivityState.Completed, Assert.Single(log.CompletedActivities).State);
    }

    [Fact]
    public void DidFailByID()
    {
        var log = new Log();
        var id = log.CreateActivity(ActivityOwner.FeedFinder, ActivityKind.FindFeed("https://example.com"));
        log.DidStart(id);
        log.DidFail(id, new TestError());
        Assert.Empty(log.RunningActivities);
        Assert.Equal(ActivityState.Failed, Assert.Single(log.CompletedActivities).State);
    }

    [Fact]
    public void DidCompleteByKindStartsPendingActivity()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.SendArticleStatuses);
        log.DidComplete(Owner, ActivityKind.SendArticleStatuses);
        Assert.Empty(log.PendingActivities);
        Assert.Empty(log.RunningActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Completed, activity.State);
        Assert.Null(activity.StartDate);
        Assert.NotNull(activity.EndDate);
    }

    [Fact]
    public void DidFailByKindStartsPendingActivity()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.RefreshFeedList);
        log.DidFail(Owner, ActivityKind.RefreshFeedList, new TestError());
        Assert.Empty(log.PendingActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Failed, activity.State);
        Assert.NotNull(activity.Error);
    }

    [Fact]
    public void DidCompleteByIDStartsPendingActivity()
    {
        var log = new Log();
        var id = log.CreateActivity(ActivityOwner.FeedImageDownloader, ActivityKind.DownloadFeedImage("Daring Fireball"));
        log.DidComplete(id);
        Assert.Empty(log.PendingActivities);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.Equal(ActivityState.Completed, activity.State);
        Assert.Null(activity.StartDate);
    }

    [Fact]
    public void DidFailByIDStartsPendingActivity()
    {
        var log = new Log();
        var id = log.CreateActivity(ActivityOwner.FeedFinder, ActivityKind.FindFeed("https://example.com"));
        log.DidFail(id, new TestError());
        Assert.Empty(log.PendingActivities);
        Assert.Equal(ActivityState.Failed, Assert.Single(log.CompletedActivities).State);
    }

    [Fact]
    public void CompletedActivitiesTrimming()
    {
        var log = new Log();
        for (int i = 0; i < 510; i++)
        {
            var kind = ActivityKind.RefreshFeedContent("Feed " + i);
            log.CreateActivity(Owner, kind);
            log.DidStart(Owner, kind);
            log.DidComplete(Owner, kind);
        }
        Assert.Equal(500, log.CompletedActivities.Count);
        Assert.Equal(ActivityKind.RefreshFeedContent("Feed 10"), log.CompletedActivities[0].Kind);
        Assert.Equal(ActivityKind.RefreshFeedContent("Feed 509"), log.CompletedActivities[499].Kind);
    }

    [Fact]
    public void PendingActivitiesForOwner()
    {
        var log = new Log();
        var second = ActivityOwner.Account("account2", "Account Two");
        log.CreateActivity(Owner, ActivityKind.SendArticleStatuses);
        log.CreateActivity(second, ActivityKind.SendArticleStatuses);
        log.CreateActivity(Owner, ActivityKind.RefreshFeedList);
        Assert.Equal(2, log.PendingActivitiesFor(Owner).Count);
        Assert.Single(log.PendingActivitiesFor(second));
    }

    [Fact]
    public void MultipleKindsForSameOwner()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.SendArticleStatuses);
        log.CreateActivity(Owner, ActivityKind.RefreshArticleStatuses);
        log.CreateActivity(Owner, ActivityKind.RefreshFeedList);
        log.DidStart(Owner, ActivityKind.SendArticleStatuses);
        log.DidComplete(Owner, ActivityKind.SendArticleStatuses);
        Assert.Equal(2, log.PendingActivities.Count);
        Assert.Empty(log.RunningActivities);
        Assert.Single(log.CompletedActivities);
        log.DidStart(Owner, ActivityKind.RefreshArticleStatuses);
        log.DidStart(Owner, ActivityKind.RefreshFeedList);
        Assert.Empty(log.PendingActivities);
        Assert.Equal(2, log.RunningActivities.Count);
        Assert.All(log.RunningActivities, activity => Assert.Equal(ActivityState.Running, activity.State));
    }

    [Fact]
    public void DetailIsPreserved()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.SendArticleStatuses, "42 statuses");
        Assert.Equal("42 statuses", Assert.Single(log.PendingActivities).Detail);
    }

    [Fact]
    public void StartIfNeededStartsPendingWithoutTimestamp()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.RefreshFeedList);
        log.StartIfNeeded(Owner, ActivityKind.RefreshFeedList);
        Assert.Empty(log.PendingActivities);
        var activity = Assert.Single(log.RunningActivities);
        Assert.Equal(ActivityState.Running, activity.State);
        Assert.Null(activity.StartDate);
    }

    [Fact]
    public void StartIfNeededDoesNothingWhenAlreadyRunning()
    {
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.RefreshFeedList);
        log.DidStart(Owner, ActivityKind.RefreshFeedList);
        var start = log.RunningActivities[0].StartDate;
        log.StartIfNeeded(Owner, ActivityKind.RefreshFeedList);
        Assert.Equal(start, Assert.Single(log.RunningActivities).StartDate);
    }

    [Fact]
    public void NextTaskNumberStringIncrements()
    {
        var log = new Log();
        Assert.Equal("#1", log.NextTaskNumberString());
        Assert.Equal("#2", log.NextTaskNumberString());
        Assert.Equal("#3", log.NextTaskNumberString());
    }

    [Fact]
    public void KindDisplayNameForSimpleKind() => Assert.Equal("Refresh all", ActivityKind.RefreshAll.DisplayName());

    [Fact]
    public void KindDisplayNameUsesDetailForFeedContent()
    {
        var kind = ActivityKind.RefreshFeedContent("https://example.com/feed.json");
        Assert.Equal("Refreshing feed: My Feed", kind.DisplayName("My Feed"));
        Assert.Equal("Refreshing feed: https://example.com/feed.json", kind.DisplayName());
    }

    [Fact]
    public void KindDisplayNameForURLKind() => Assert.Equal("Finding feed https://example.com", ActivityKind.FindFeed("https://example.com").DisplayName());

    [Fact]
    public void FormattedDurationUnderTenSecondsHasTwoDecimals()
    {
        Assert.Equal("0.46s", Activity.FormatDuration(0.456));
        Assert.Equal("10.00s", Activity.FormatDuration(9.999));
    }

    [Fact]
    public void FormattedDurationUnderOneMinuteHasOneDecimal()
    {
        Assert.Equal("10.0s", Activity.FormatDuration(10));
        Assert.Equal("12.3s", Activity.FormatDuration(12.34));
    }

    [Fact]
    public void FormattedDurationOneMinuteOrMoreUsesMinutesAndSeconds()
    {
        Assert.Equal("1m 0s", Activity.FormatDuration(60));
        Assert.Equal("2m 15s", Activity.FormatDuration(135));
    }

    [Fact]
    public void AccountRenameDoesNotChangeOwnerIdentity()
    {
        var renamed = ActivityOwner.Account("account1", "Renamed");
        Assert.Equal(Owner, renamed);
        Assert.Equal(Owner.GetHashCode(), renamed.GetHashCode());
        Assert.NotEqual(Owner, ActivityOwner.Account("account2", "Account One"));
        var log = new Log();
        log.CreateActivity(Owner, ActivityKind.RefreshAll);
        log.DidStart(renamed, ActivityKind.RefreshAll);
        Assert.Single(log.RunningActivitiesFor(renamed));
    }

    [Fact]
    public async Task ConcurrentSameKindWorkUsesIndependentIDs()
    {
        var log = new Log();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => log.LogActivityAsync(Owner, ActivityKind.RefreshArticles,
            async () => { await Task.Yield(); return i; })));
        Assert.Equal(100, log.CompletedActivities.Count);
        Assert.Equal(100, log.CompletedActivities.Select(activity => activity.ID).Distinct().Count());
        Assert.Empty(log.PendingActivities);
        Assert.Empty(log.RunningActivities);
    }

    [Fact]
    public void CacheCompletionIsRepresentedWithoutAStartTimestamp()
    {
        var log = new Log();
        var id = log.CreateActivity(ActivityOwner.FaviconDownloader, ActivityKind.DownloadFavicon("https://example.com/icon"));
        log.DidComplete(id, "Cached", returnedFromCache: true);
        var activity = Assert.Single(log.CompletedActivities);
        Assert.True(activity.ReturnedFromCache);
        Assert.Null(activity.FormattedDuration);
    }

    private sealed class TestError : Exception;
}
