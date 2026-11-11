using System.Globalization;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Dinosaurs;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

[Collection("Account storage")]
public sealed class DinosaursViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dinosaurs-" + Guid.NewGuid().ToString("N"));
    private readonly List<Account.Account> _accounts = [];
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly Clock _clock = new();

    public DinosaursViewModelTests()
    {
        AppConfig.SetDataDirectoryOverride(_directory);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
    }

    [Fact]
    public async Task OnlySubscribedFeedsStrictlyOlderThanTheCalendarCutoffAreIncluded()
    {
        var account = CreateAccount("Active");
        var old = account.AddFeed("https://example.com/old", "Old", account.AddFolder("First"));
        account.AddFeed(old.Url, folder: account.AddFolder("Second"));
        old.LastResponseCode = 410;
        var boundary = account.AddFeed("https://example.com/boundary");
        var fresh = account.AddFeed("https://example.com/fresh");
        account.AddFeed("https://example.com/no-articles");
        var inactive = CreateAccount("Inactive");
        var hidden = inactive.AddFeed("https://example.com/hidden");
        inactive.IsActive = false;
        var cutoff = new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc);
        await account.Database.UpdateArticlesAsync([
            Article(account, old, "old", cutoff.AddSeconds(-1)),
            Article(account, boundary, "boundary", cutoff),
            Article(account, fresh, "fresh", cutoff.AddSeconds(1))
        ]);
        await inactive.Database.UpdateArticlesAsync([Article(inactive, hidden, "hidden", cutoff.AddYears(-1))]);
        var model = new DinosaursViewModel(() => _accounts, _clock) { MonthThreshold = 1 };
        await model.RefreshAsync();
        var row = Assert.Single(model.Rows);
        Assert.Same(old, row.Feed);
        Assert.Equal(410, row.LastResponseCode);
        Assert.Equal(old.Url + account.AccountID, row.Id);
        Assert.False(model.ShowAccountColumn);
        CreateAccount("Active but empty");
        await model.RefreshAsync();
        Assert.True(model.ShowAccountColumn);
        model.Clear();
        Assert.Empty(model.Rows);
    }

    [Theory]
    [InlineData(2024, 29)]
    [InlineData(2025, 28)]
    public void CalendarMonthsClampAtTheEndOfFebruary(int year, int day)
    {
        var cutoff = DinosaursViewModel.CutoffDate(new DateTimeOffset(year, 3, 31, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, 1);
        Assert.Equal(new DateTime(year, 2, day, 12, 0, 0, DateTimeKind.Utc), cutoff);
    }

    [Fact]
    public void CutoffsPreserveLocalCivilTimeAcrossOffsetChanges()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1), start, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Dinosaur fixture", TimeSpan.FromHours(-8), "Fixture", "Standard", "Daylight", [rule]);
        Assert.Equal(new DateTime(2025, 3, 9, 10, 30, 0, DateTimeKind.Utc),
            DinosaursViewModel.CutoffDate(new DateTimeOffset(2025, 4, 9, 9, 30, 0, TimeSpan.Zero), zone, 1));
        Assert.Equal(new DateTime(2025, 11, 2, 8, 30, 0, DateTimeKind.Utc),
            DinosaursViewModel.CutoffDate(new DateTimeOffset(2025, 12, 2, 9, 30, 0, TimeSpan.Zero), zone, 1));
    }

    [Fact]
    public void SortingUsesTheNativeNullableRulesAndUrlTiebreaker()
    {
        var account = CreateAccount("Account");
        var feed = account.AddFeed("https://example.com/a");
        var left = new DinosaurRow(feed, account, "Same", "Same", "https://example.com/a", null, null);
        var right = left with { FeedUrl = "https://example.com/b" };
        Assert.True(DinosaursViewModel.Compare(left, right, DinosaurSortKey.FeedName, true) < 0);
        Assert.True(DinosaursViewModel.Compare(left, right, DinosaurSortKey.AccountName, false) > 0);
        right = right with { LastArticleDate = DateTime.UnixEpoch, LastResponseCode = 200 };
        Assert.True(DinosaursViewModel.Compare(left, right, DinosaurSortKey.LastArticleDate, true) < 0);
        Assert.True(DinosaursViewModel.Compare(left, right, DinosaurSortKey.LastResponseCode, false) > 0);
        Assert.True(DinosaursViewModel.Compare(left with { FeedName = "Feed 10" }, right with { FeedName = "Feed 2" },
            DinosaurSortKey.FeedName, true) < 0);
    }

    [Fact]
    public async Task FailedRefreshKeepsThePreviousRowsAndRemovedRowsCannotBeDeleted()
    {
        var account = CreateAccount("Account");
        var feed = account.AddFeed("https://example.com/old");
        await account.Database.UpdateArticlesAsync([Article(account, feed, "old", DateTime.UnixEpoch)]);
        var model = new DinosaursViewModel(() => _accounts, _clock);
        await model.RefreshAsync();
        var previous = Assert.Single(model.Rows);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.RefreshAsync(cancelled.Token));
        Assert.Same(previous, Assert.Single(model.Rows));
        model.Clear();
        Assert.Throws<InvalidOperationException>(() => model.CreateDeletionCommand([previous]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(351)]
    public void ThresholdsOutsideTheNativeRangeAreRejected(int months)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DinosaursViewModel(() => _accounts, _clock).MonthThreshold = months);

    private Account.Account CreateAccount(string name)
    {
        var account = new Account.Account(Guid.NewGuid().ToString("N"), AccountType.OnMyMac, name, new LocalAccountDelegate());
        _accounts.Add(account);
        return account;
    }

    private Article Article(Account.Account account, Feed feed, string id, DateTime published)
        => new(account.AccountID, id, feed.FeedID, id, "Article", "<p>Body</p>", null, null, feed.Url, null, null, null,
            published, null, null, null, new ArticleStatus(id, false, false, _clock.GetUtcNow().UtcDateTime));

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    public void Dispose()
    {
        foreach (var account in _accounts) account.Dispose();
        AppConfig.SetDataDirectoryOverride(null);
        CultureInfo.CurrentCulture = _culture;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
