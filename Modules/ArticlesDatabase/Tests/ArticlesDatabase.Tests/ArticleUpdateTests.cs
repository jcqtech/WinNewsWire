using WinNewsWire.Articles;
using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.ArticlesDatabase.Tests;

/// <summary>ArticlesDatabase/Tests/ArticlesDatabaseTests/ArticleUpdateTests.swift.</summary>
public class ArticleUpdateTests : IDisposable
{
    private readonly ArticlesDatabaseStore _database = new(":memory:", "test");

    [Fact]
    public async Task IdenticalUpdateReportsNoChanges()
    {
        var item = Item("Title", null);
        var first = await _database.UpdateAsync([item], "feed1", deleteOlder: false);
        Assert.Single(first.New!);
        _database.EmptyCaches();
        var second = await _database.UpdateAsync([item], "feed1", deleteOlder: false);
        Assert.Null(second.New);
        Assert.Null(second.Updated);
    }

    [Fact]
    public async Task RefreshReusesPersistedStatusesWithoutReinsertingThem()
    {
        var item = Item("Title", null);
        var original = Assert.Single((await _database.UpdateAsync([item], "feed1")).New!);
        await _database.MarkAsync([original.ArticleID], ArticleStatus.Key.Read, false);
        await _database.MarkAsync([original.ArticleID], ArticleStatus.Key.Starred, true);
        _database.EmptyCaches();
        var stored = Assert.Single(await _database.FetchArticlesAsync("feed1"));
        _database.Queue.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_existing_status BEFORE INSERT ON statuses
                WHEN EXISTS (SELECT 1 FROM statuses WHERE articleID = NEW.articleID)
                BEGIN SELECT RAISE(ABORT, 'Existing statuses must be reused'); END;
                """;
            command.ExecuteNonQuery();
        });

        var added = ParsedItemFixture.Create("2", "feed1", DateTime.UtcNow);
        var changes = await _database.UpdateAsync([Item("Changed", null), added], "feed1");
        Assert.Single(changes.New!);
        var updated = Assert.Single(changes.Updated!);
        Assert.Same(stored.Status, updated.Status);
        Assert.False(updated.Status.Read);
        Assert.True(updated.Status.Starred);
        Assert.Equal(stored.Status.DateArrived, updated.Status.DateArrived);
        Assert.Null((await _database.UpdateAsync([Item("Changed", null), added], "feed1")).Updated);
    }

    [Fact]
    public async Task MarkdownChangePersists()
    {
        await _database.UpdateAsync([Item("Title", "one")], "feed1", deleteOlder: false);
        var changes = await _database.UpdateAsync([Item("Title", "two")], "feed1", deleteOlder: false);
        Assert.Single(changes.Updated!);
        _database.EmptyCaches();
        Assert.Equal("two", Assert.Single(await _database.FetchArticlesAsync("feed1")).Markdown);
    }

    [Fact]
    public async Task ClearedFieldStopsReportingAsUpdated()
    {
        await _database.UpdateAsync([Item("Title", null)], "feed1", deleteOlder: false);
        var clearing = await _database.UpdateAsync([Item(null, null)], "feed1", deleteOlder: false);
        Assert.Single(clearing.Updated!);
        _database.EmptyCaches();
        Assert.Null(Assert.Single(await _database.FetchArticlesAsync("feed1")).Title);
        _database.EmptyCaches();
        var repeated = await _database.UpdateAsync([Item(null, null)], "feed1", deleteOlder: false);
        Assert.Null(repeated.Updated);
    }

    [Fact]
    public void EqualityComparesMarkdown()
    {
        Assert.NotEqual(ArticleWithMarkdown("one"), ArticleWithMarkdown("two"));
        Assert.Equal(ArticleWithMarkdown("one"), ArticleWithMarkdown("one"));
    }

    [Fact]
    public async Task FractionalDatesSurviveAndDoNotProduceRepeatedUpdates()
    {
        var date = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        var item = ParsedItemFixture.Create("fractional", "feed1", date);
        await _database.UpdateAsync([item], "feed1");
        _database.EmptyCaches();
        var stored = Assert.Single(await _database.FetchArticlesAsync("feed1"));
        Assert.NotNull(stored.DatePublished);
        Assert.InRange(Math.Abs((stored.DatePublished.Value - date).Ticks), 0, 3);
        var repeated = await _database.UpdateAsync([item], "feed1");
        Assert.Null(repeated.New);
        Assert.Null(repeated.Updated);
    }

    [Fact]
    public async Task AttachmentDurationRetains64BitPrecision()
    {
        var item = new ParsedItem(null, "long-duration", "feed1", null, null, "Title", null,
            "<p>Body</p>", null, null, null, null, null, null, null, null, null,
            new HashSet<ParsedAttachment> { new("https://example.com/audio", "audio/mpeg", null, null, 4294967296L) });
        await _database.UpdateAsync([item], "feed1");
        _database.EmptyCaches();
        var stored = Assert.Single(await _database.FetchArticlesAsync("feed1"));
        Assert.Equal(4294967296L, Assert.Single(stored.Attachments!).DurationInSeconds);
    }

    private static Article ArticleWithMarkdown(string markdown)
        => new("test", "article1", "feed1", "1", "Title", "<p>Test</p>", null, markdown,
            null, null, null, null, null, null, null, null,
            new ArticleStatus("article1", false, false, DateTime.UnixEpoch));

    private static ParsedItem Item(string? title, string? markdown)
        => ParsedItemFixture.Create("1", "feed1", DateTime.UnixEpoch, title, markdown);

    public void Dispose() => _database.Dispose();
}

internal static class ParsedItemFixture
{
    public static ParsedItem Create(string uniqueID, string feedID, DateTime? datePublished, string? title = "Title", string? markdown = null)
        => new(null, uniqueID, feedID, $"https://example.com/{uniqueID}", null, title, null,
            "<p>Test</p>", null, markdown, null, null, null, datePublished, null, null, null, null);
}
