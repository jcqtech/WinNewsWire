using Microsoft.Data.Sqlite;
using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.ArticlesDatabase.Tests;

public sealed class ArticleCacheTests : IDisposable
{
    private readonly ArticlesDatabaseStore _database = new(":memory:", "account");

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public async Task SetQueriesSortInSqlOnlyWhenSelectingALimitedSubset(int? limit)
    {
        var now = DateTime.UtcNow;
        Article Item(string id, DateTime date) => new("account", id, "feed", id, id,
            "needle " + new string('x', 64 * 1024), null, null, null, null, null, null, date, null, null, null,
            new ArticleStatus(id, false, true, now));
        await _database.UpdateArticlesAsync([Item("old", now.AddHours(-2)), Item("recent", now.AddHours(-1))]);
        var statements = new List<string>();
        SQLitePCL.strdelegate_trace trace = (_, sql) => statements.Add(sql);
        _database.Queue.Run(connection => SQLitePCL.raw.sqlite3_trace(connection.Handle, trace, null));
        try
        {
            var results = new[]
            {
                await _database.FetchArticlesAsync(["feed"], limit),
                await _database.FetchUnreadArticlesAsync(["feed"], limit),
                await _database.FetchStarredAsync(["feed"], limit),
                await _database.FetchTodayArticlesAsync(["feed"], limit),
                await _database.SearchAsync("needle", ["feed"], limit)
            };
            foreach (var result in results)
            {
                if (limit is null) Assert.Equal(2, result.Count);
                else Assert.Equal("recent", Assert.Single(result).ArticleID);
            }
            var selectingArticles = statements.Where(sql => sql.TrimStart().StartsWith("SELECT a.articleID,", StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.Equal(5, selectingArticles.Length);
            Assert.All(selectingArticles, sql => Assert.Equal(limit is not null, sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            _database.Queue.Run(connection => SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace?)null, null));
        }
    }

    [Fact]
    public async Task ExternalChangesInvalidateOnlyAffectedRowsAndPreserveVisibleStatuses()
    {
        string path = Path.Combine(Path.GetTempPath(), "external-articles-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var reader = new ArticlesDatabaseStore(path, "account");
            using var writer = new ArticlesDatabaseStore(path, "account");
            var oldAuthor = Author.Create("shared", "Old", null, null, null)!;
            Article WithAuthor(string id, string body, Author? author) => new("account", id, "feed", id, "Title", body,
                null, null, null, null, null, null, DateTime.UnixEpoch, null,
                author is null ? null : new HashSet<Author> { author }, null, new ArticleStatus(id, false, false, DateTime.UnixEpoch));
            await reader.UpdateArticlesAsync([WithAuthor("one", "Old body", oldAuthor),
                WithAuthor("two", "Related", oldAuthor), WithAuthor("three", "Unrelated", null)]);
            var before = (await reader.FetchArticlesAsync("feed")).ToDictionary(article => article.ArticleID);
            await reader.MarkAsync(["one"], ArticleStatus.Key.Read, true);
            await writer.UpdateArticlesAsync([WithAuthor("one", "New body", Author.Create("shared", "New", null, null, null))]);
            var changed = Assert.Single(await reader.ApplyExternalChangesAsync(["one"]));
            var after = (await reader.FetchArticlesAsync("feed")).ToDictionary(article => article.ArticleID);
            Assert.Same(changed, after["one"]);
            Assert.Equal("New body", changed.ContentHtml);
            Assert.Same(before["one"].Status, changed.Status);
            Assert.True(changed.Status.Read);
            Assert.NotSame(before["two"], after["two"]);
            Assert.Equal("New", Assert.Single(after["two"].Authors!).Name);
            Assert.Same(before["three"], after["three"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MemoryPressureTrimsUnreferencedRowsWithoutDuplicatingLiveArticleBodies()
    {
        await _database.UpdateAsync([ParsedItemFixture.Create("one", "feed", DateTime.UtcNow),
            ParsedItemFixture.Create("two", "feed", DateTime.UtcNow)], "feed");
        var before = (await _database.FetchArticlesAsync("feed")).ToDictionary(article => article.UniqueID);
        _database.TrimArticleCache(new HashSet<string> { before["one"].ArticleID });
        var after = (await _database.FetchArticlesAsync("feed")).ToDictionary(article => article.UniqueID);
        Assert.Same(before["one"], after["one"]);
        Assert.NotSame(before["two"], after["two"]);
        Assert.Same(before["two"].Status, after["two"].Status);
    }

    [Fact]
    public async Task RepeatedQueriesReuseTheArticleAndBody()
    {
        await _database.UpdateArticlesAsync([Article("one", new string('x', 1_000_000))]);
        var first = Assert.Single(await _database.FetchArticlesAsync("feed"));
        var second = Assert.Single(await _database.FetchArticlesByIDsAsync(["id"]));
        Assert.Same(first, second);
        Assert.Same(first.ContentHtml, second.ContentHtml);
        Assert.Same(first, Assert.Single(await _database.FetchUnreadArticlesAsync(["feed"])));
    }

    [Fact]
    public async Task CommittedUpdatesReplaceCachedContentButKeepCanonicalStatus()
    {
        await _database.UpdateArticlesAsync([Article("one", "old")]);
        var first = Assert.Single(await _database.FetchArticlesAsync("feed"));
        await _database.MarkAsync(["id"], ArticleStatus.Key.Read, true);
        await _database.UpdateArticlesAsync([Article("two", "new")]);
        var second = Assert.Single(await _database.FetchArticlesAsync("feed"));
        Assert.NotSame(first, second);
        Assert.Equal("two", second.Title);
        Assert.Equal("new", second.ContentHtml);
        Assert.True(second.Status.Read);
        Assert.Same(first.Status, second.Status);
    }

    [Fact]
    public async Task ParsedUpdatesAndFeedRemappingInvalidateCachedRows()
    {
        await _database.UpdateAsync([ParsedItemFixture.Create("item", "feed", DateTime.UtcNow)], "feed");
        var first = Assert.Single(await _database.FetchArticlesAsync("feed"));
        await _database.UpdateAsync([ParsedItemFixture.Create("item", "feed", DateTime.UtcNow, "Changed")], "feed");
        var second = Assert.Single(await _database.FetchArticlesAsync("feed"));
        Assert.Equal("Changed", second.Title);
        Assert.NotSame(first, second);
        await _database.RemapFeedIDsAsync(new Dictionary<string, string> { ["feed"] = "moved" });
        Assert.Empty(await _database.FetchArticlesAsync("feed"));
        Assert.Equal("moved", Assert.Single(await _database.FetchArticlesAsync("moved")).FeedID);
    }

    [Fact]
    public async Task FailedWriteDoesNotPublishUncommittedCachedValues()
    {
        await _database.UpdateArticlesAsync([Article("one", "old")]);
        var first = Assert.Single(await _database.FetchArticlesAsync("feed"));
        _database.Queue.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_update BEFORE UPDATE ON articles BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
            command.ExecuteNonQuery();
        });
        await Assert.ThrowsAsync<SqliteException>(() => _database.UpdateArticlesAsync([Article("two", "new")]));
        var second = Assert.Single(await _database.FetchArticlesAsync("feed"));
        Assert.Same(first, second);
        Assert.Equal("old", second.ContentHtml);
    }

    [Fact]
    public async Task EmptyCachesDropsArticleInstancesWithoutLosingStoredContent()
    {
        await _database.UpdateArticlesAsync([Article("one", "body")]);
        var first = Assert.Single(await _database.FetchArticlesAsync("feed"));
        _database.EmptyCaches();
        var second = Assert.Single(await _database.FetchArticlesAsync("feed"));
        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Same(second, Assert.Single(await _database.FetchArticlesAsync("feed")));
    }

    [Fact]
    public async Task UpdatingSharedAuthorMetadataInvalidatesOtherCachedReferences()
    {
        var oldAuthor = Author.Create("shared", "Old name", null, null, null)!;
        Article WithAuthor(string id, Author author) => new("account", id, "feed", id, "Title", "Body", null, null,
            null, null, null, null, DateTime.UnixEpoch, null, new HashSet<Author> { author }, null,
            new ArticleStatus(id, false, false, DateTime.UnixEpoch));
        await _database.UpdateArticlesAsync([WithAuthor("one", oldAuthor), WithAuthor("two", oldAuthor)]);
        var before = (await _database.FetchArticlesAsync("feed")).Single(article => article.ArticleID == "two");
        await _database.UpdateArticlesAsync([WithAuthor("one", Author.Create("shared", "New name", null, null, null)!)]);
        var after = (await _database.FetchArticlesAsync("feed")).Single(article => article.ArticleID == "two");
        Assert.NotSame(before, after);
        Assert.Equal("New name", Assert.Single(after.Authors!).Name);
    }

    [Fact]
    public async Task MemoryPressureEvictionPreservesStatusesHeldByVisibleArticles()
    {
        await _database.UpdateArticlesAsync([Article("one", "body")]);
        var visible = Assert.Single(await _database.FetchArticlesAsync("feed"));
        _database.EmptyArticleCache();
        await _database.MarkAsync(["id"], ArticleStatus.Key.Read, true);
        Assert.True(visible.Status.Read);
        var reloaded = Assert.Single(await _database.FetchArticlesAsync("feed"));
        Assert.NotSame(visible, reloaded);
        Assert.Same(visible.Status, reloaded.Status);
    }

    private static Article Article(string title, string body)
        => new("account", "id", "feed", "unique", title, body, null, null, "https://example.com/article",
            null, null, null, DateTime.UnixEpoch, null, null, null,
            new ArticleStatus("id", false, false, DateTime.UnixEpoch));

    public void Dispose() => _database.Dispose();
}
