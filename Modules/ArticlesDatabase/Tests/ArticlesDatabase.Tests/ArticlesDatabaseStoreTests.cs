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
        DateTime? dateArrived = null, HashSet<Attachment>? attachments = null)
    {
        var status = new ArticleStatus(articleID, read, starred, dateArrived ?? DateTime.UtcNow);
        return new Article(AccountID, articleID, feedID, uniqueID,
            title, contentHtml, contentText, null,
            "https://example.com/" + uniqueID, null, "summary", null,
            datePublished ?? DateTime.UtcNow, null, authors, attachments, status);
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
    public async Task AccountCountsScopeArticlesButIncludeAllStatusRows()
    {
        await _store.UpdateArticlesAsync([
            MakeArticle("one", "subscribed", "one", read: false, starred: true),
            MakeArticle("two", "subscribed", "two", read: true),
            MakeArticle("old", "unsubscribed", "old", read: false, starred: true)
        ]);
        await _store.CreateStatusesIfNeededAsync(["missing-body"]);
        Assert.Equal(new ArticleCounts(2, 1, 1, 4), await _store.FetchArticleCountsAsync(["subscribed", "subscribed"]));
        Assert.Equal(new ArticleCounts(0, 0, 0, 4), await _store.FetchArticleCountsAsync([]));
        int previous = _store.Queue.Run(connection =>
            SQLitePCL.raw.sqlite3_limit(connection.Handle!, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, 500));
        Assert.True(previous >= 500);
        try
        {
            var scope = Enumerable.Range(0, 1001).Select(index => "empty-feed-" + index)
                .Prepend("subscribed").Append("subscribed");
            Assert.Equal(new ArticleCounts(2, 1, 1, 4), await _store.FetchArticleCountsAsync(scope));
        }
        finally
        {
            _store.Queue.Run(connection => SQLitePCL.raw.sqlite3_limit(connection.Handle!, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, previous));
        }
    }

    [Fact]
    public async Task VacuumPreservesArticleCountsStatusesAndSearchRows()
    {
        await _store.UpdateArticlesAsync([MakeArticle("one", "feed", "one", title: "Needle", read: false, starred: true)]);
        var before = await _store.FetchArticleCountsAsync(["feed"]);
        await _store.VacuumAsync();
        Assert.Equal(before, await _store.FetchArticleCountsAsync(["feed"]));
        var article = Assert.Single(await _store.SearchAsync("Needle"));
        Assert.Equal("one", article.ArticleID);
        Assert.False(article.Status.Read);
        Assert.True(article.Status.Starred);
    }

    [Fact]
    public async Task FailedStatisticsQueriesDoNotReturnSuccessShapedZeroCounts()
    {
        _store.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _store.FetchArticleCountsAsync(["feed"]));
    }

    [Fact]
    public async Task LargeResultsLoadAllRelationshipsWithinSqlitesParameterLimit()
    {
        var articles = Enumerable.Range(0, 1001).Select(index => MakeArticle(index.ToString(), "large-feed", "unique-" + index,
            authors: [Author.Create("author-" + index, "Author " + index, null, null, null)!],
            attachments: [Attachment.Create("attachment-" + index, "https://example.com/media/" + index,
                "audio/mpeg", "Part " + index, 1024L + index, 65_000_000_000L + index)!])).ToArray();
        await _store.UpdateArticlesAsync(articles);
        int previous = _store.Queue.Run(connection =>
            SQLitePCL.raw.sqlite3_limit(connection.Handle!, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, 500));
        Assert.True(previous >= 500);
        try
        {
            var fetched = await _store.FetchArticlesAsync("large-feed");
            Assert.Equal(1001, fetched.Count);
            foreach (var article in fetched)
            {
                var index = int.Parse(article.ArticleID);
                Assert.Equal("Author " + index, Assert.Single(article.Authors!).Name);
                var attachment = Assert.Single(article.Attachments!);
                Assert.Equal("Part " + index, attachment.Title);
                Assert.Equal(65_000_000_000L + index, attachment.DurationInSeconds);
            }
        }
        finally
        {
            _store.Queue.Run(connection => SQLitePCL.raw.sqlite3_limit(connection.Handle!, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, previous));
        }
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
        var a = MakeArticle("art1", "feed1", "u1", title: "Searchable Title", contentHtml: null, contentText: "interesting body text");
        await _store.UpdateArticlesAsync(new[] { a });

        var byTitle = await _store.SearchAsync("Searchable");
        Assert.Single(byTitle);
        Assert.Equal("art1", byTitle.First().ArticleID);

        var byBody = await _store.SearchAsync("interesting");
        Assert.Single(byBody);
        Assert.Equal("art1", byBody.First().ArticleID);
    }

    [Fact]
    public async Task SearchIndexTracksEditsWithoutLeavingOldRows()
    {
        await _store.UpdateArticlesAsync([MakeArticle("a1", "feed1", "u1", title: "Original", contentHtml: "<p>obsolete</p>")]);
        await _store.UpdateArticlesAsync([MakeArticle("a1", "feed1", "u1", title: "Updated", contentHtml: "<p>replacement</p>")]);
        Assert.Empty(await _store.SearchAsync("Original"));
        Assert.Empty(await _store.SearchAsync("obsolete"));
        Assert.Single(await _store.SearchAsync("replacement"));
        Assert.Equal(1L, _store.Queue.Run(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM search;";
            return (long)command.ExecuteScalar()!;
        }));
    }

    [Fact]
    public async Task SearchMatchesAuthorsAndUnicodePunctuation()
    {
        var author = Author.Create("author1", "Alice Example", null, null, null)!;
        await _store.UpdateArticlesAsync([MakeArticle("a1", "feed1", "u1",
            contentHtml: "<p>\u2018BeReal\u2019 &amp; news</p>", authors: [author])]);
        Assert.Single(await _store.SearchAsync("bereal"));
        Assert.Single(await _store.SearchAsync("Alice"));
        await _store.UpdateArticlesAsync([MakeArticle("a1", "feed1", "u1", authors: null)]);
        Assert.Empty(await _store.SearchAsync("Alice"));
        Assert.Null(Assert.Single(await _store.FetchArticlesAsync("feed1")).Authors);
    }

    [Theory]
    [InlineData("inter", 1)]
    [InlineData("\"interesting", 1)]
    [InlineData("interesting OR missing", 1)]
    [InlineData("interesting AND missing", 0)]
    [InlineData("\"(*)", 0)]
    [InlineData("", 0)]
    public async Task SearchSupportsPrefixWordsAndHandlesPunctuation(string query, int expected)
    {
        await _store.UpdateArticlesAsync([MakeArticle("a1", "feed1", "u1", contentHtml: "<p>interesting</p>")]);
        Assert.Equal(expected, (await _store.SearchAsync(query)).Count);
    }

    [Fact]
    public async Task SearchHasNoImplicitHundredArticleLimitAndHonorsScope()
    {
        await _store.UpdateArticlesAsync(Enumerable.Range(0, 125)
            .Select(i => MakeArticle($"a{i}", "feed1", $"u{i}", title: "Common title")));
        Assert.Equal(125, (await _store.SearchAsync("Common", limit: null)).Count);
        Assert.Equal(3, (await _store.SearchAsync("Common", limit: 3)).Count);
        Assert.Empty(await _store.SearchAsync("Common", Array.Empty<string>()));
        Assert.Empty(await _store.SearchAsync("Common", ["other-feed"]));
    }

    [Fact]
    public async Task OpeningLegacyDatabaseBackfillsMissingIndex()
    {
        await _store.UpdateArticlesAsync([MakeArticle("a1", "feed1", "u1", title: "Migrated")]);
        _store.Queue.Run(c =>
        {
            using var command = c.CreateCommand();
            command.CommandText = "DELETE FROM search; UPDATE articles SET searchRowID = NULL;";
            command.ExecuteNonQuery();
        });
        using var reopened = new ArticlesDatabaseStore(_dbPath, AccountID);
        Assert.Single(await reopened.SearchAsync("Migrated"));
        Assert.Single(await reopened.FetchUnreadArticlesAsync(["feed1"]));
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
