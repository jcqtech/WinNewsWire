using Microsoft.Data.Sqlite;
using WinNewsWire.Articles;
using WinNewsWire.Database;
using Xunit;

namespace WinNewsWire.ArticlesDatabase.Tests;

public sealed class NativeRetentionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "native-retention-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly ArticlesDatabaseStore _store;
    private readonly Clock _clock = new();
    public NativeRetentionTests() => _store = new ArticlesDatabaseStore(_path, "account");

    [Fact]
    public async Task PreserveHistoryDoesNotDeleteOldUnreadStarredOrUnsubscribedData()
    {
        await _store.UpdateArticlesAsync([
            Article("old-read", "removed", 500, true, false),
            Article("old-unread", "removed", 500, false, false),
            Article("starred", "removed", 500, true, true)
        ]);
        var before = await _store.FetchArticleCountsAsync(["removed"]);
        Assert.Equal(new ArticleCleanupResult(0, 0, true, true),
            await _store.CleanupDatabaseAtStartupAsync(["subscribed"], ArticleRetentionStyle.PreserveHistory, _clock));
        Assert.Equal(before, await _store.FetchArticleCountsAsync(["removed"]));
    }

    [Fact]
    public async Task SyncCleanupSeparatesBodyAndStatusLifetimesAndPreservesUnreadAndStarred()
    {
        await _store.UpdateArticlesAsync([
            Article("old-read", "feed", 200, true, false),
            Article("middle-read", "feed", 120, true, false),
            Article("old-unread", "feed", 200, false, false),
            Article("old-starred", "feed", 200, true, true),
            Article("boundary", "feed", 90, true, false)
        ]);
        var result = await _store.CleanupDatabaseAtStartupAsync(["feed"], ArticleRetentionStyle.SyncSystem, _clock);
        Assert.Equal(2, result.ArticlesDeleted);
        Assert.Equal(1, result.StatusesDeleted);
        Assert.Equal(new HashSet<string> { "old-unread", "old-starred", "boundary" },
            (await _store.FetchArticlesAsync("feed")).Select(article => article.ArticleID).ToHashSet());
        Assert.Equal(new HashSet<string> { "middle-read", "old-unread", "old-starred", "boundary" },
            await _store.FetchExistingStatusIDsAsync(["old-read", "middle-read", "old-unread", "old-starred", "boundary"]));
    }

    [Theory]
    [InlineData(ArticleRetentionStyle.FeedBased, 30, 2)]
    [InlineData(ArticleRetentionStyle.SyncSystem, 180, 1)]
    public async Task OrphanStatusRulesMatchTheSelectedNativeStyle(ArticleRetentionStyle style, int days, int removed)
    {
        await _store.UpdateArticlesAsync([
            Article("read", "feed", days + 1, true, false),
            Article("unread", "feed", days + 1, false, false),
            Article("starred", "feed", days + 1, true, true),
            Article("boundary", "feed", days, true, false)
        ]);
        _store.Queue.Run(connection => connection.Execute("DELETE FROM articles;"));
        var result = await _store.CleanupDatabaseAtStartupAsync([], style, _clock);
        Assert.Equal(removed, result.StatusesDeleted);
        var remaining = await _store.FetchExistingStatusIDsAsync(["read", "unread", "starred", "boundary"]);
        Assert.DoesNotContain("read", remaining);
        Assert.Contains("starred", remaining);
        Assert.Contains("boundary", remaining);
        Assert.Equal(style == ArticleRetentionStyle.SyncSystem, remaining.Contains("unread"));
    }

    [Fact]
    public async Task FeedBasedStartupKeepsSubscribedOldBodiesAndEmptySubscriptionSetsDoNotPurgeEverything()
    {
        await _store.UpdateArticlesAsync([Article("old", "feed", 500, true, false)]);
        Assert.Equal(0, (await _store.CleanupDatabaseAtStartupAsync([], ArticleRetentionStyle.FeedBased, _clock)).ArticlesDeleted);
        Assert.Equal(0, (await _store.CleanupDatabaseAtStartupAsync(["feed"], ArticleRetentionStyle.FeedBased, _clock)).ArticlesDeleted);
        Assert.Single(await _store.FetchArticlesAsync("feed"));
    }

    [Fact]
    public async Task ExplicitUnsubscriptionCleanupRemovesAllMatchingContentStatusesAndSearch()
    {
        await _store.UpdateArticlesAsync([
            Article("removed-starred", "removed", 1, false, true),
            Article("keep", "subscribed", 1, false, false)
        ]);
        var result = await _store.CleanupDatabaseAtStartupAsync(["subscribed"], ArticleRetentionStyle.FeedBased, _clock);
        Assert.Equal(1, result.ArticlesDeleted);
        Assert.Equal(1, result.StatusesDeleted);
        Assert.Empty(await _store.FetchArticlesAsync("removed"));
        Assert.Empty(await _store.FetchExistingStatusIDsAsync(["removed-starred"]));
        Assert.Equal("keep", Assert.Single(await _store.SearchAsync("Needle")).ArticleID);
    }

    [Fact]
    public async Task LargeDeletionSetsRespectSqlitesParameterLimit()
    {
        await _store.UpdateArticlesAsync(Enumerable.Range(0, 1001)
            .Select(index => Article("old-" + index, "removed-" + index, 1, true, false)));
        int previous = _store.Queue.Run(connection =>
            SQLitePCL.raw.sqlite3_limit(connection.Handle!, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, 500));
        try
        {
            var result = await _store.CleanupDatabaseAtStartupAsync(["subscribed"], ArticleRetentionStyle.FeedBased, _clock);
            Assert.Equal(1001, result.ArticlesDeleted);
            Assert.Equal(1001, result.StatusesDeleted);
            Assert.Empty(await _store.SearchAsync("Needle"));
        }
        finally
        {
            _store.Queue.Run(connection => SQLitePCL.raw.sqlite3_limit(connection.Handle!, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, previous));
        }
    }

    [Fact]
    public async Task AFailedCleanupRollsBackBodiesStatusesAndFullTextIndex()
    {
        await _store.UpdateArticlesAsync([Article("old", "removed", 500, true, true)]);
        _store.Queue.Run(connection => connection.Execute(
            "CREATE TRIGGER fail_status_delete BEFORE DELETE ON statuses BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;"));
        await Assert.ThrowsAsync<SqliteException>(() =>
            _store.CleanupDatabaseAtStartupAsync(["subscribed"], ArticleRetentionStyle.FeedBased, _clock));
        Assert.Single(await _store.FetchArticlesAsync("removed"));
        Assert.Single(await _store.FetchExistingStatusIDsAsync(["old"]));
        Assert.Equal("old", Assert.Single(await _store.SearchAsync("Needle")).ArticleID);
        _store.Queue.Run(connection => connection.Execute("DROP TRIGGER fail_status_delete;"));
        Assert.Equal(1, (await _store.CleanupDatabaseAtStartupAsync(["subscribed"], ArticleRetentionStyle.FeedBased, _clock)).ArticlesDeleted);
    }

    [Fact]
    public async Task CleanupRemovesOnlyUnreferencedAuthorsAndAttachments()
    {
        var sharedAuthor = Author.Create("shared-author", "Shared", null, null, null)!;
        var oldAuthor = Author.Create("old-author", "Old", null, null, null)!;
        var sharedAttachment = Attachment.Create("shared-attachment", "https://example.com/shared.mp3", "audio/mpeg", null, 10, 1)!;
        var oldAttachment = Attachment.Create("old-attachment", "https://example.com/old.mp3", "audio/mpeg", null, 10, 1)!;
        var now = _clock.GetUtcNow().UtcDateTime;
        await _store.UpdateArticlesAsync([
            new Article("account", "old", "feed", "old", "Needle", "Body", null, null, null, null, null, null,
                now.AddDays(-200), null, new HashSet<Author> { sharedAuthor, oldAuthor },
                new HashSet<Attachment> { sharedAttachment, oldAttachment }, new ArticleStatus("old", true, false, now.AddDays(-200))),
            new Article("account", "keep", "feed", "keep", "Needle", "Body", null, null, null, null, null, null,
                now, null, new HashSet<Author> { sharedAuthor }, new HashSet<Attachment> { sharedAttachment },
                new ArticleStatus("keep", false, false, now))
        ]);
        await _store.CleanupDatabaseAtStartupAsync(["feed"], ArticleRetentionStyle.SyncSystem, _clock);
        var kept = Assert.Single(await _store.FetchArticlesAsync("feed"));
        Assert.Equal("Shared", Assert.Single(kept.Authors!).Name);
        Assert.Equal("https://example.com/shared.mp3", Assert.Single(kept.Attachments!).URL);
        Assert.Equal(1L, _store.Queue.Run(connection => connection.Scalar<long>("SELECT COUNT(*) FROM authors;")));
        Assert.Equal(1L, _store.Queue.Run(connection => connection.Scalar<long>("SELECT COUNT(*) FROM attachments;")));
        Assert.Equal(1L, _store.Queue.Run(connection => connection.Scalar<long>("SELECT COUNT(*) FROM authorsLookup;")));
        Assert.Equal(1L, _store.Queue.Run(connection => connection.Scalar<long>("SELECT COUNT(*) FROM attachmentsLookup;")));
    }

    [Fact]
    public async Task NativeAgeCleanupYieldsAfterItsTwoSecondBudget()
    {
        await _store.UpdateArticlesAsync([
            Article("very-old", "feed", 500, true, false),
            Article("later-stage", "feed", 250, true, false)
        ]);
        var slow = new SlowClock(_clock.GetUtcNow());
        var result = await _store.CleanupDatabaseAtStartupAsync(["feed"], ArticleRetentionStyle.SyncSystem, slow);
        Assert.False(result.ReachedRecentCutoff);
        Assert.Equal(1, result.ArticlesDeleted);
        Assert.Equal("later-stage", Assert.Single(await _store.FetchArticlesAsync("feed")).ArticleID);
    }

    [Fact]
    public async Task CancellationAndInvalidStylesCannotDeleteData()
    {
        await _store.UpdateArticlesAsync([Article("old", "feed", 500, true, false)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.CleanupDatabaseAtStartupAsync(["feed"], ArticleRetentionStyle.SyncSystem, _clock, cancellation.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.CleanupDatabaseAtStartupAsync([], (ArticleRetentionStyle)99));
        Assert.Single(await _store.FetchArticlesAsync("feed"));
    }

    private Article Article(string id, string feed, int days, bool read, bool starred)
        => new("account", id, feed, id, "Needle", "<p>Body</p>", null, null, "https://example.com/" + id,
            null, null, null, _clock.GetUtcNow().UtcDateTime.AddDays(-days), null, null, null,
            new ArticleStatus(id, read, starred, _clock.GetUtcNow().UtcDateTime.AddDays(-days)));

    private class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class SlowClock(DateTimeOffset now) : TimeProvider
    {
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, 3);
    }
    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_path);
        File.Delete(_path + "-wal");
        File.Delete(_path + "-shm");
        File.Delete(_path + "-journal");
    }
}
