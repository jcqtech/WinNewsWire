using WinNewsWire.Articles;
using WinNewsWire.Database;
using Xunit;

namespace WinNewsWire.ArticlesDatabase.Tests;

public sealed class LastUpdateDatesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "last-update-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly ArticlesDatabaseStore _store;

    public LastUpdateDatesTests() => _store = new ArticlesDatabaseStore(_path, "account");

    [Fact]
    public async Task EmptyAndUnmatchedStatusRowsDoNotInventFeedDates()
    {
        Assert.Empty(await _store.FetchLastUpdateDatesAsync());
        await _store.CreateStatusesIfNeededAsync(["status-without-content"]);
        _store.Queue.Run(connection => connection.Execute(
            "INSERT INTO articles(articleID,feedID,uniqueID) VALUES('content-without-status','unmatched','unique');"));
        Assert.Empty(await _store.FetchLastUpdateDatesAsync());
    }

    [Fact]
    public async Task PublishedThenModifiedThenArrivalIsChosenBeforeTakingTheMaximum()
    {
        var january = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.UpdateArticlesAsync([
            Make("published", "feed", january, january.AddMonths(8), january.AddMonths(9)),
            Make("later-published", "feed", january.AddMonths(2), null, january.AddMonths(10)),
            Make("modified", "modified-feed", null, january.AddMonths(1), january.AddMonths(11)),
            Make("arrived", "arrived-feed", null, null, january.AddMonths(3))
        ]);
        var dates = await _store.FetchLastUpdateDatesAsync();
        Assert.Equal(3, dates.Count);
        Assert.Equal(january.AddMonths(2), dates["feed"]);
        Assert.Equal(january.AddMonths(1), dates["modified-feed"]);
        Assert.Equal(january.AddMonths(3), dates["arrived-feed"]);
    }

    [Fact]
    public async Task FractionalPreEpochDatesAndCaseDistinctFeedIdsSurviveReopening()
    {
        var date = DateTime.UnixEpoch.AddSeconds(-123.875);
        await _store.UpdateArticlesAsync([
            Make("lower", "feed", date, null, date.AddYears(1)),
            Make("upper", "Feed", date.AddSeconds(1), null, date.AddYears(1))
        ]);
        _store.Dispose();
        using var reopened = new ArticlesDatabaseStore(_path, "account");
        var dates = await reopened.FetchLastUpdateDatesAsync();
        Assert.Equal(2, dates.Count);
        Assert.Equal(date, dates["feed"]);
        Assert.Equal(date.AddSeconds(1), dates["Feed"]);
        Assert.All(dates.Values, value => Assert.Equal(DateTimeKind.Utc, value.Kind));
    }

    [Fact]
    public async Task UpdatesAndRemovedContentAreReflectedWithoutStaleResults()
    {
        var first = DateTime.UnixEpoch.AddDays(20000);
        await _store.UpdateArticlesAsync([Make("article", "feed", first, null, first)]);
        Assert.Equal(first, (await _store.FetchLastUpdateDatesAsync())["feed"]);
        await _store.UpdateArticlesAsync([Make("article", "feed", first.AddDays(1), null, first)]);
        Assert.Equal(first.AddDays(1), (await _store.FetchLastUpdateDatesAsync())["feed"]);
        _store.Queue.Run(connection => connection.Execute("DELETE FROM articles WHERE articleID='article';"));
        Assert.Empty(await _store.FetchLastUpdateDatesAsync());
    }

    [Fact]
    public async Task LargeFeedSetsDoNotDependOnSqliteParameterLimits()
    {
        var date = DateTime.UnixEpoch.AddDays(20000);
        await _store.UpdateArticlesAsync(Enumerable.Range(0, 1001)
            .Select(index => Make("article-" + index, "feed-" + index, date.AddSeconds(index), null, date)));
        int previous = _store.Queue.Run(connection =>
            SQLitePCL.raw.sqlite3_limit(connection.Handle!, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, 500));
        try
        {
            var dates = await _store.FetchLastUpdateDatesAsync();
            Assert.Equal(1001, dates.Count);
            for (int index = 0; index < 1001; index++) Assert.Equal(date.AddSeconds(index), dates["feed-" + index]);
        }
        finally
        {
            _store.Queue.Run(connection => SQLitePCL.raw.sqlite3_limit(connection.Handle!,
                SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, previous));
        }
    }

    [Fact]
    public async Task CancellationAndDatabaseFailuresDoNotReturnEmptySuccessResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.FetchLastUpdateDatesAsync(cancellation.Token));
        _store.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _store.FetchLastUpdateDatesAsync());
    }

    private static Article Make(string id, string feed, DateTime? published, DateTime? modified, DateTime arrived)
        => new("account", id, feed, id, "Title", "<p>Body</p>", null, null, "https://example.com/" + id,
            null, null, null, published, modified, null, null, new ArticleStatus(id, false, false, arrived));

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
        File.Delete(_path + "-journal");
    }
}
