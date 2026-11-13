using WinNewsWire.Articles;
using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.ArticlesDatabase.Tests;

/// <summary>ArticlesDatabase/Tests/ArticlesDatabaseTests/ArticleUpdateTests.swift.</summary>
public class ArticleUpdateTests : IDisposable
{
    private readonly ArticlesDatabaseStore _database = new(":memory:", "test");

    private static ParsedItem ThumbnailItem(string? thumbnail) => new(null, "thumbnail", "feed1", null, null, "Title", null,
        "<p>Body</p>", null, null, null, "https://example.test/full.jpg", null, null, null, null, null, null, thumbnailUrl: thumbnail);

    [Fact]
    public async Task ParsedThumbnailsRoundTripAndThumbnailOnlyChangesAreDetected()
    {
        await _database.UpdateAsync([ThumbnailItem("https://example.test/small.jpg")], "feed1");
        _database.EmptyCaches();
        var stored = Assert.Single(await _database.FetchArticlesAsync("feed1"));
        Assert.Equal("https://example.test/small.jpg", stored.RawThumbnailLink);
        Assert.Equal("https://example.test/full.jpg", stored.RawImageLink);
        var changed = Assert.Single((await _database.UpdateAsync([ThumbnailItem("https://example.test/new.jpg")], "feed1")).Updated!);
        Assert.Same(stored.Status, changed.Status);
        _database.EmptyCaches();
        Assert.Equal("https://example.test/new.jpg", Assert.Single(await _database.FetchArticlesAsync("feed1")).RawThumbnailLink);
        Assert.Null((await _database.UpdateAsync([ThumbnailItem("https://example.test/new.jpg")], "feed1")).Updated);
        Assert.Null(Assert.Single((await _database.UpdateAsync([ThumbnailItem(null)], "feed1")).Updated!).RawThumbnailLink);
        _database.EmptyCaches();
        Assert.Null(Assert.Single(await _database.FetchArticlesAsync("feed1")).RawThumbnailLink);
    }

    [Fact]
    public async Task ExistingArticleDatabasesGainThumbnailStorageWithoutLosingArticles()
    {
        var path = Path.Combine(Path.GetTempPath(), "thumbnail-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var legacy = new ArticlesDatabaseStore(path, "test"))
            {
                await legacy.UpdateAsync([ThumbnailItem(null)], "feed1");
                legacy.Queue.Run(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "ALTER TABLE articles DROP COLUMN thumbnailURL;";
                    command.ExecuteNonQuery();
                });
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var migrated = new ArticlesDatabaseStore(path, "test");
                var previous = Assert.Single(await migrated.FetchArticlesAsync("feed1"));
                Assert.Equal("Title", previous.Title);
                Assert.Equal("https://example.test/full.jpg", previous.RawImageLink);
                await migrated.UpdateAsync([ThumbnailItem("https://example.test/small.jpg")], "feed1");
                migrated.EmptyCaches();
                Assert.Equal("https://example.test/small.jpg", Assert.Single(await migrated.FetchArticlesAsync("feed1")).RawThumbnailLink);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AttachmentFeedOrderRoundTripsAndReorderingReportsAnUpdate()
    {
        var first = new ParsedAttachment("https://example.com/z.mp3", "audio/mpeg", null, null, null);
        var second = new ParsedAttachment("https://example.com/a.mp3", "audio/mpeg", null, null, null);
        ParsedItem MediaItem(params ParsedAttachment[] media) => new(null, "ordered", "feed1", null, null, "Title", null,
            "<p>Body</p>", null, null, null, null, null, null, null, null, null, media);
        await _database.UpdateAsync([MediaItem(first, second)], "feed1");
        _database.EmptyCaches();
        Assert.Equal(new[] { first.Url, second.Url }, Assert.Single(await _database.FetchArticlesAsync("feed1")).Attachments!.Select(a => a.URL));
        Assert.Single((await _database.UpdateAsync([MediaItem(second, first)], "feed1")).Updated!);
        _database.EmptyCaches();
        Assert.Equal(new[] { second.Url, first.Url }, Assert.Single(await _database.FetchArticlesAsync("feed1")).Attachments!.Select(a => a.URL));
        Assert.Null((await _database.UpdateAsync([MediaItem(second, first)], "feed1")).Updated);
    }

    [Fact]
    public async Task AttachmentMetadataIsArticleScopedAndUpdatesWithoutChangingArticleBody()
    {
        ParsedItem MediaItem(string id, long width) => new(null, id, "feed1", null, null, "Title", null,
            "<p>Body</p>", null, null, null, null, null, null, null, null, null,
            new HashSet<ParsedAttachment> { new("https://example.com/image.jpg", "image/jpeg", null, null, null,
                width, 480, ParsedAttachment.AttachmentMedium.Image) });
        await _database.UpdateAsync([MediaItem("one", 640), MediaItem("two", 320)], "feed1");
        _database.EmptyCaches();
        var stored = (await _database.FetchArticlesAsync("feed1")).ToDictionary(a => a.UniqueID);
        Assert.Equal(640, Assert.Single(stored["one"].Attachments!).Width);
        Assert.Equal(320, Assert.Single(stored["two"].Attachments!).Width);
        var changes = await _database.UpdateAsync([MediaItem("one", 1280)], "feed1");
        var updated = Assert.Single(changes.Updated!);
        Assert.Equal(1280, Assert.Single(updated.Attachments!).Width);
        Assert.Same(stored["one"].Status, updated.Status);
        _database.EmptyCaches();
        stored = (await _database.FetchArticlesAsync("feed1")).ToDictionary(a => a.UniqueID);
        Assert.Equal(1280, Assert.Single(stored["one"].Attachments!).Width);
        Assert.Equal(Attachment.AttachmentMedium.Image, Assert.Single(stored["one"].Attachments!).Medium);
        Assert.Equal(320, Assert.Single(stored["two"].Attachments!).Width);
        Assert.Null((await _database.UpdateAsync([MediaItem("one", 1280)], "feed1")).Updated);
    }

    [Fact]
    public async Task LegacyNormalizedAttachmentsMigrateWithoutLosingExistingMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), "attachment-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var legacy = new ArticlesDatabaseStore(path, "test"))
            {
                legacy.Queue.Run(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        ALTER TABLE articles DROP COLUMN attachments;
                        INSERT INTO articles(articleID, feedID, uniqueID) VALUES ('article', 'feed1', '1');
                        INSERT INTO statuses(articleID) VALUES ('article');
                        INSERT INTO attachments VALUES ('media', 'https://example.com/audio', 'audio/mpeg', 'Episode', 4294967296, 180);
                        INSERT INTO attachmentsLookup VALUES ('media', 'article');
                        """;
                    command.ExecuteNonQuery();
                });
            }
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var migrated = new ArticlesDatabaseStore(path, "test");
                var attachment = Assert.Single(Assert.Single(await migrated.FetchArticlesAsync("feed1")).Attachments!);
                Assert.Equal("media", attachment.AttachmentID);
                Assert.Equal("Episode", attachment.Title);
                Assert.Equal(4294967296L, attachment.SizeInBytes);
                Assert.Equal(180, attachment.DurationInSeconds);
                Assert.Null(attachment.Width);
                Assert.Null(attachment.Medium);
            }
        }
        finally { File.Delete(path); }
    }

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

    [Fact]
    public async Task ClearedAttachmentsPersistAndStopReportingAsUpdated()
    {
        var item = new ParsedItem(null, "clear-media", "feed1", null, null, "Title", null,
            "<p>Body</p>", null, null, null, null, null, null, null, null, null,
            [new("https://example.test/episode.mp3", "audio/mpeg", null, 12345678, 3600)]);
        await _database.UpdateAsync([item], "feed1");
        _database.EmptyCaches();
        Assert.Equal(3600L, Assert.Single(Assert.Single(await _database.FetchArticlesAsync("feed1")).Attachments!).DurationInSeconds);
        var cleared = new ParsedItem(null, "clear-media", "feed1", null, null, "Title", null,
            "<p>Body</p>", null, null, null, null, null, null, null, null, null, null);
        Assert.Null(Assert.Single((await _database.UpdateAsync([cleared], "feed1")).Updated!).Attachments);
        _database.EmptyCaches();
        Assert.Null(Assert.Single(await _database.FetchArticlesAsync("feed1")).Attachments);
        Assert.Null((await _database.UpdateAsync([cleared], "feed1")).Updated);
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
