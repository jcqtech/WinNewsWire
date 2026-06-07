using Microsoft.Data.Sqlite;
using WinNewsWire.Articles;
using WinNewsWire.ArticlesDatabase;
using Xunit;

namespace WinNewsWire.Tests;

public class ArticlesDatabaseStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ArticlesDatabaseStore _store;
    private const string AccountID = "test-account";

    public ArticlesDatabaseStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"artdb-{Guid.NewGuid():N}.sqlite");
        _store = new ArticlesDatabaseStore(_dbPath, AccountID);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private static Article MakeArticle(
        string articleID, string feedID, string uniqueID,
        string? title = "Test Title", string? contentHtml = "<p>body</p>",
        string? contentText = "body text", bool read = false, bool starred = false,
        DateTime? datePublished = null, HashSet<Author>? authors = null,
        DateTime? dateArrived = null)
    {
        var status = new ArticleStatus(articleID, read, starred, dateArrived ?? DateTime.UtcNow);
        return new Article(AccountID, articleID, feedID, uniqueID,
            title, contentHtml, contentText, null,
            "https://example.com/" + uniqueID, null, "summary", null,
            datePublished ?? DateTime.UtcNow, null, authors, null, status);
    }

    [Fact]
    public async Task InsertArticlesAndFetchBack()
    {
        var a1 = MakeArticle("art1", "feed1", "u1", title: "First Article");
        var a2 = MakeArticle("art2", "feed1", "u2", title: "Second Article");
        await _store.UpdateArticlesAsync(new[] { a1, a2 });

        var fetched = await _store.FetchArticlesAsync("feed1");

        Assert.Equal(2, fetched.Count);
        Assert.Contains(fetched, a => a.ArticleID == "art1" && a.Title == "First Article");
        Assert.Contains(fetched, a => a.ArticleID == "art2" && a.Title == "Second Article");
    }

    [Fact]
    public async Task UpdateExistingArticles_ChangeTitleAndContent()
    {
        var original = MakeArticle("art1", "feed1", "u1", title: "Original", contentHtml: "<p>old</p>");
        await _store.UpdateArticlesAsync(new[] { original });

        var updated = MakeArticle("art1", "feed1", "u1", title: "Updated", contentHtml: "<p>new</p>");
        await _store.UpdateArticlesAsync(new[] { updated });

        var fetched = await _store.FetchArticlesAsync("feed1");
        Assert.Single(fetched);
        var art = fetched.First();
        Assert.Equal("Updated", art.Title);
        Assert.Equal("<p>new</p>", art.ContentHtml);
    }

    [Fact]
    public async Task MarkArticlesAsRead()
    {
        var a = MakeArticle("art1", "feed1", "u1", read: false);
        await _store.UpdateArticlesAsync(new[] { a });
        Assert.Equal(1, await _store.UnreadCountAsync(new[] { "feed1" }));

        await _store.MarkAsync(new[] { "art1" }, ArticleStatus.Key.Read, true);

        Assert.Equal(0, await _store.UnreadCountAsync(new[] { "feed1" }));
    }

    [Fact]
    public async Task MarkArticlesAsUnread()
    {
        var a = MakeArticle("art1", "feed1", "u1", read: false);
        await _store.UpdateArticlesAsync(new[] { a });

        await _store.MarkAsync(new[] { "art1" }, ArticleStatus.Key.Read, true);
        Assert.Equal(0, await _store.UnreadCountAsync(new[] { "feed1" }));

        await _store.MarkAsync(new[] { "art1" }, ArticleStatus.Key.Read, false);
        Assert.Equal(1, await _store.UnreadCountAsync(new[] { "feed1" }));
    }

    [Fact]
    public async Task MarkArticlesAsStarred()
    {
        var a = MakeArticle("art1", "feed1", "u1");
        await _store.UpdateArticlesAsync(new[] { a });

        await _store.MarkAsync(new[] { "art1" }, ArticleStatus.Key.Starred, true);

        var starred = await _store.FetchStarredAsync(new[] { "feed1" });
        Assert.Single(starred);
        Assert.Equal("art1", starred.First().ArticleID);
    }

    [Fact]
    public async Task MarkArticlesAsUnstarred()
    {
        var a = MakeArticle("art1", "feed1", "u1");
        await _store.UpdateArticlesAsync(new[] { a });
        await _store.MarkAsync(new[] { "art1" }, ArticleStatus.Key.Starred, true);

        await _store.MarkAsync(new[] { "art1" }, ArticleStatus.Key.Starred, false);

        var starred = await _store.FetchStarredAsync(new[] { "feed1" });
        Assert.Empty(starred);
    }

    [Fact]
    public async Task FetchUnreadCountsPerFeed()
    {
        await _store.UpdateArticlesAsync(new[]
        {
            MakeArticle("a1", "feed1", "u1"),
            MakeArticle("a2", "feed1", "u2"),
            MakeArticle("a3", "feed2", "u3"),
        });

        var counts = await _store.UnreadCountsByFeedAsync(new[] { "feed1", "feed2" });

        Assert.Equal(2, counts["feed1"]);
        Assert.Equal(1, counts["feed2"]);
    }

    [Fact]
    public async Task SearchByTitleAndBody()
    {
        var a = MakeArticle("art1", "feed1", "u1", title: "Searchable Title", contentText: "interesting body text");
        await _store.UpdateArticlesAsync(new[] { a });

        // Populate FTS table manually (UpdateArticlesAsync doesn't populate it)
        _store.Queue.Run(c =>
        {
            using var insert = c.CreateCommand();
            insert.CommandText = "INSERT INTO search (title, body) VALUES (@t, @b);";
            insert.Parameters.AddWithValue("@t", "Searchable Title");
            insert.Parameters.AddWithValue("@b", "interesting body text");
            insert.ExecuteNonQuery();

            var rowid = c.CreateCommand();
            rowid.CommandText = "SELECT last_insert_rowid();";
            var rid = (long)rowid.ExecuteScalar()!;

            using var upd = c.CreateCommand();
            upd.CommandText = "UPDATE articles SET searchRowID = @r WHERE articleID = @a;";
            upd.Parameters.AddWithValue("@r", rid);
            upd.Parameters.AddWithValue("@a", "art1");
            upd.ExecuteNonQuery();
        });

        var byTitle = await _store.SearchAsync("Searchable");
        Assert.Single(byTitle);
        Assert.Equal("art1", byTitle.First().ArticleID);

        var byBody = await _store.SearchAsync("interesting");
        Assert.Single(byBody);
        Assert.Equal("art1", byBody.First().ArticleID);
    }

    [Fact]
    public async Task FetchStarredArticles_AcrossFeeds()
    {
        await _store.UpdateArticlesAsync(new[]
        {
            MakeArticle("a1", "feed1", "u1"),
            MakeArticle("a2", "feed1", "u2"),
            MakeArticle("a3", "feed2", "u3"),
        });
        await _store.MarkAsync(new[] { "a1", "a3" }, ArticleStatus.Key.Starred, true);

        var starred = await _store.FetchStarredAsync(new[] { "feed1", "feed2" });
        Assert.Equal(2, starred.Count);
        Assert.Contains(starred, a => a.ArticleID == "a1");
        Assert.Contains(starred, a => a.ArticleID == "a3");
    }

    [Fact]
    public async Task DuplicateArticleIDs_UpdateNotDuplicate()
    {
        var first = MakeArticle("art1", "feed1", "u1", title: "Version 1");
        await _store.UpdateArticlesAsync(new[] { first });

        var second = MakeArticle("art1", "feed1", "u1", title: "Version 2");
        await _store.UpdateArticlesAsync(new[] { second });

        var fetched = await _store.FetchArticlesAsync("feed1");
        Assert.Single(fetched);
        Assert.Equal("Version 2", fetched.First().Title);
    }

    [Fact]
    public async Task MultipleFeeds_IndependentQueries()
    {
        await _store.UpdateArticlesAsync(new[]
        {
            MakeArticle("a1", "feed1", "u1", title: "Feed1 Art1"),
            MakeArticle("a2", "feed1", "u2", title: "Feed1 Art2"),
            MakeArticle("a3", "feed2", "u3", title: "Feed2 Art1"),
        });

        var feed1 = await _store.FetchArticlesAsync("feed1");
        var feed2 = await _store.FetchArticlesAsync("feed2");

        Assert.Equal(2, feed1.Count);
        Assert.Single(feed2);
        Assert.All(feed1, a => Assert.Equal("feed1", a.FeedID));
        Assert.All(feed2, a => Assert.Equal("feed2", a.FeedID));
    }

    [Fact]
    public async Task EmptyDatabaseQueries_ReturnEmptyResults()
    {
        var articles = await _store.FetchArticlesAsync("nonexistent");
        Assert.Empty(articles);

        var count = await _store.UnreadCountAsync(new[] { "nonexistent" });
        Assert.Equal(0, count);

        var counts = await _store.UnreadCountsByFeedAsync(new[] { "nonexistent" });
        Assert.Empty(counts);

        var starred = await _store.FetchStarredAsync(new[] { "nonexistent" });
        Assert.Empty(starred);

        var unread = await _store.FetchUnreadArticlesAsync(new[] { "nonexistent" });
        Assert.Empty(unread);
    }

    [Fact]
    public async Task FetchUnreadArticles_OnlyReturnsUnread()
    {
        await _store.UpdateArticlesAsync(new[]
        {
            MakeArticle("a1", "feed1", "u1", read: false),
            MakeArticle("a2", "feed1", "u2", read: false),
        });
        await _store.MarkAsync(new[] { "a1" }, ArticleStatus.Key.Read, true);

        var unread = await _store.FetchUnreadArticlesAsync(new[] { "feed1" });

        Assert.Single(unread);
        Assert.Equal("a2", unread.First().ArticleID);
    }

    [Fact]
    public async Task AuthorsRoundTrip()
    {
        var author = Author.Create("author1", "John Doe", "https://example.com", null, "john@example.com");
        Assert.NotNull(author);
        var authors = new HashSet<Author> { author };
        var a = MakeArticle("art1", "feed1", "u1", authors: authors);
        await _store.UpdateArticlesAsync(new[] { a });

        var fetched = await _store.FetchArticlesAsync("feed1");
        Assert.Single(fetched);
        var art = fetched.First();
        Assert.NotNull(art.Authors);
        Assert.Single(art.Authors);
        var fetchedAuthor = art.Authors.First();
        Assert.Equal("John Doe", fetchedAuthor.Name);
        Assert.Equal("john@example.com", fetchedAuthor.EmailAddress);
    }

    [Fact]
    public async Task EmptyFeedIDs_ReturnsZeroOrEmpty()
    {
        Assert.Equal(0, await _store.UnreadCountAsync(Array.Empty<string>()));
        Assert.Empty(await _store.UnreadCountsByFeedAsync(Array.Empty<string>()));
        Assert.Empty(await _store.FetchUnreadArticlesAsync(Array.Empty<string>()));
    }

    [Fact]
    public async Task MarkWithEmptyList_IsNoOp()
    {
        // Should not throw
        await _store.MarkAsync(Array.Empty<string>(), ArticleStatus.Key.Read, true);
        await _store.MarkAsync(Array.Empty<string>(), ArticleStatus.Key.Starred, true);
    }

    [Fact]
    public async Task CleanupOldArticles_DeletesOldUnstarred_PreservesStarredAndRecent()
    {
        var oldDate = DateTime.UtcNow.AddDays(-120);
        var recentDate = DateTime.UtcNow.AddDays(-10);

        var oldUnstarred = MakeArticle("old1", "feed1", "u1", title: "Old Unstarred", dateArrived: oldDate);
        var oldStarred = MakeArticle("old2", "feed1", "u2", title: "Old Starred", starred: true, dateArrived: oldDate);
        var recent = MakeArticle("new1", "feed1", "u3", title: "Recent Article", dateArrived: recentDate);

        await _store.UpdateArticlesAsync(new[] { oldUnstarred, oldStarred, recent });

        // Star the second article via MarkAsync to ensure statuses row is updated
        await _store.MarkAsync(new[] { "old2" }, ArticleStatus.Key.Starred, true);

        var deleted = await _store.CleanupOldArticlesAsync(retentionDays: 90);

        Assert.Equal(1, deleted);

        var remaining = await _store.FetchArticlesAsync("feed1");
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, a => a.ArticleID == "old1");
        Assert.Contains(remaining, a => a.ArticleID == "old2");
        Assert.Contains(remaining, a => a.ArticleID == "new1");
    }

    [Fact]
    public async Task UpdateWithEmptyList_IsNoOp()
    {
        await _store.UpdateArticlesAsync(Array.Empty<Article>());
        var fetched = await _store.FetchArticlesAsync("feed1");
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task StatusPreservedOnUpdate_ReadNotOverwritten()
    {
        var a = MakeArticle("art1", "feed1", "u1", read: false);
        await _store.UpdateArticlesAsync(new[] { a });

        // Mark as read via MarkAsync
        await _store.MarkAsync(new[] { "art1" }, ArticleStatus.Key.Read, true);

        // Re-insert same article (INSERT OR IGNORE on statuses won't overwrite)
        var a2 = MakeArticle("art1", "feed1", "u1", read: false);
        await _store.UpdateArticlesAsync(new[] { a2 });

        // Status should still be read (INSERT OR IGNORE preserves existing row)
        Assert.Equal(0, await _store.UnreadCountAsync(new[] { "feed1" }));
    }
}
