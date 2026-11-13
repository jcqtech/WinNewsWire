using Microsoft.Data.Sqlite;
using WinNewsWire.Database;
using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class ImageDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "image-metadata-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private string DatabasePath => Path.Combine(_directory, "Images.db");
    public ImageDatabaseTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void OriginalThreeTableDatabaseIsImportedAndDeletedOnlyAfterItsDataIsAvailable()
    {
        string legacyPath = Path.Combine(_directory, "ImageMetadata.db");
        WriteLegacyDatabase(legacyPath);
        using var database = new ImageDatabase(DatabasePath, _clock, legacyDatabasePath: legacyPath);
        Assert.Equal("https://site.test/favicon.png", database.FaviconUrl("https://site.test/"));
        Assert.Equal("https://site.test/icon.png", database.IconUrl("https://site.test/feed"));
        Assert.True(database.RecentlyFailed("https://site.test/missing"));
        Assert.False(File.Exists(legacyPath));
        using var saved = Open();
        Assert.Equal("wal", saved.Scalar<string>("PRAGMA journal_mode;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationPreservesWindowsThumbnailAndSourceRecordsIncludingRecoveredWalAndOlderSchemas(bool oldValidationSchema)
    {
        string sourcePath = Path.Combine(_directory, "source.db");
        string legacyPath = Path.Combine(_directory, "ImageMetadata.db");
        const string image = "https://site.test/source.png";
        using (var source = new ImageDatabase(sourcePath, _clock))
        {
            await source.SaveHomePageFaviconAsync("https://site.test/", null);
            await source.SaveFeedIconUrlAsync("https://site.test/feed", "legacy");
            await source.RecordFailureAsync("https://site.test/missing", 404);
            await source.SaveArticleThumbnailsAsync([new("article", ["https://site.test/photo.png"])]);
            await source.SaveDownloadedImageAsync(new(image, 1024, (48, 48),
                new(null, "\"legacy\""), _clock.GetUtcNow().AddDays(3)));
            if (oldValidationSchema)
            {
                using var legacy = Open(sourcePath);
                legacy.Execute("ALTER TABLE downloadedImage DROP COLUMN nextCheck;");
            }
            File.Copy(sourcePath, legacyPath);
            File.Copy(sourcePath + "-wal", legacyPath + "-wal");
        }
        using (var destination = new ImageDatabase(DatabasePath, _clock))
            await destination.SaveFeedIconUrlAsync("https://site.test/feed", "current");
        using (var database = new ImageDatabase(DatabasePath, _clock, legacyDatabasePath: legacyPath))
        {
            Assert.True(database.HomePageHasNoFavicon("https://site.test/"));
            Assert.Equal("current", database.IconUrl("https://site.test/feed"));
            Assert.True(database.RecentlyFailed("https://site.test/missing"));
            Assert.True(database.IsUnsuitableForThumbnail(image));
            Assert.Equal(["https://site.test/photo.png"],
                Assert.Single(await database.FetchArticleThumbnailsAsync(["article"])).ThumbnailUrls);
            var validation = Assert.IsType<DownloadedImageValidation>(await database.FetchDownloadedImageValidationAsync(image));
            Assert.Equal("\"legacy\"", validation.ConditionalGet?.ETag);
            Assert.Equal(oldValidationSchema ? DateTimeOffset.UnixEpoch : _clock.GetUtcNow().AddDays(3), validation.NextCheck);
            var usage = Assert.Single(await database.FetchDownloadedImageUsageByLeastRecentAccessAsync());
            Assert.Equal(1024, usage.ByteCount);
            Assert.Equal(image, usage.Url);
        }
        Assert.False(File.Exists(legacyPath));
        Assert.False(File.Exists(legacyPath + "-wal"));
        Assert.False(File.Exists(legacyPath + "-shm"));
        using var reopened = new ImageDatabase(DatabasePath, _clock, legacyDatabasePath: legacyPath);
        Assert.Equal("current", reopened.IconUrl("https://site.test/feed"));
        Assert.True(reopened.IsUnsuitableForThumbnail(image));
    }

    [Fact]
    public void FailedMigrationRollsBackEveryTableAndKeepsTheLegacyDatabaseForRetry()
    {
        string legacyPath = Path.Combine(_directory, "ImageMetadata.db");
        WriteLegacyDatabase(legacyPath);
        using (var destination = new ImageDatabase(DatabasePath, _clock)) { }
        using (var connection = Open())
            connection.Execute("""
                CREATE TRIGGER reject_legacy_favicon BEFORE INSERT ON homePageFavicon
                BEGIN SELECT RAISE(ABORT, 'fixture import failure'); END;
                """);
        Assert.Throws<SqliteException>(() =>
        {
            using var unexpected = new ImageDatabase(DatabasePath, _clock, legacyDatabasePath: legacyPath);
        });
        Assert.True(File.Exists(legacyPath));
        using (var connection = Open())
        {
            Assert.Equal(0L, connection.Scalar<long>("SELECT count(*) FROM downloadFailure;"));
            Assert.Equal(0L, connection.Scalar<long>("SELECT count(*) FROM homePageFavicon;"));
            connection.Execute("DROP TRIGGER reject_legacy_favicon;");
        }
        using var retried = new ImageDatabase(DatabasePath, _clock, legacyDatabasePath: legacyPath);
        Assert.True(retried.RecentlyFailed("https://site.test/missing"));
        Assert.False(File.Exists(legacyPath));
    }

    private void WriteLegacyDatabase(string path)
    {
        using var connection = Open(path);
        connection.Execute("""
            CREATE TABLE downloadFailure (url TEXT PRIMARY KEY NOT NULL, statusCode INTEGER, lastChecked REAL NOT NULL);
            CREATE TABLE homePageFavicon (homePageURL TEXT PRIMARY KEY NOT NULL, faviconURL TEXT, lastChecked REAL NOT NULL);
            CREATE TABLE feedIconURL (feedURL TEXT PRIMARY KEY NOT NULL, iconURL TEXT NOT NULL, lastChecked REAL NOT NULL);
            INSERT INTO downloadFailure VALUES ('https://site.test/missing', 404, $now);
            INSERT INTO homePageFavicon VALUES ('https://site.test/', 'https://site.test/favicon.png', $now);
            INSERT INTO feedIconURL VALUES ('https://site.test/feed', 'https://site.test/icon.png', $now);
            """, ("$now", (_clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds));
    }

    [Fact]
    public async Task NativeTablesAndPositiveMappingsSurviveRestart()
    {
        using (var database = new ImageDatabase(DatabasePath, _clock))
        {
            await database.SaveHomePageFaviconAsync("https://site.test/", "https://site.test/favicon.png");
            await database.SaveFeedIconUrlAsync("https://site.test/feed", "https://site.test/feed.png");
            await database.RecordFailureAsync("https://site.test/broken", 404);
        }
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        Assert.Equal("https://site.test/favicon.png", reopened.FaviconUrl("https://site.test/"));
        Assert.Equal("https://site.test/feed.png", reopened.IconUrl("https://site.test/feed"));
        Assert.True(reopened.RecentlyFailed("https://site.test/broken"));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT lastChecked FROM downloadFailure WHERE url='https://site.test/broken';";
        Assert.Equal((_clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds, (double)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task NegativeHomepageExpiresInMemoryAtFiveDays()
    {
        using var database = new ImageDatabase(DatabasePath, _clock);
        await database.SaveHomePageFaviconAsync("https://site.test/", null);
        _clock.Advance(ImageDatabase.FailureRetry - TimeSpan.FromSeconds(1));
        Assert.True(database.HomePageHasNoFavicon("https://site.test/"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(database.HomePageHasNoFavicon("https://site.test/"));
        await database.SaveHomePageFaviconAsync("https://site.test/", "https://site.test/icon");
        Assert.False(database.HomePageHasNoFavicon("https://site.test/"));
        Assert.Equal("https://site.test/icon", database.FaviconUrl("https://site.test/"));
    }

    [Fact]
    public async Task ExpiredNegativeHomepageDoesNotReturnAfterRestart()
    {
        using (var database = new ImageDatabase(DatabasePath, _clock)) await database.SaveHomePageFaviconAsync("https://site.test/", null);
        _clock.Advance(ImageDatabase.FailureRetry);
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        Assert.False(reopened.HomePageHasNoFavicon("https://site.test/"));
    }

    [Fact]
    public async Task FailureRetryBoundaryAndExplicitClearAreDurable()
    {
        using var database = new ImageDatabase(DatabasePath, _clock);
        await database.RecordFailureAsync("https://site.test/icon", null);
        _clock.Advance(ImageDatabase.FailureRetry - TimeSpan.FromSeconds(1));
        Assert.True(database.RecentlyFailed("https://site.test/icon"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(database.RecentlyFailed("https://site.test/icon"));
        await database.RecordFailureAsync("https://site.test/icon", 404);
        await database.ClearFailureAsync("https://site.test/icon");
        Assert.False(database.RecentlyFailed("https://site.test/icon"));
    }

    [Theory]
    [InlineData(48u, 48u, true)]
    [InlineData(1000u, 64u, true)]
    [InlineData(null, null, true)]
    [InlineData(64u, 64u, false)]
    public async Task StoredDimensionsPersistIndependentlyOfFailureExpiryAndCanBeReplaced(
        uint? width, uint? height, bool unsuitable)
    {
        const string url = "https://site.test/tiny.png";
        using (var database = new ImageDatabase(DatabasePath, _clock))
        {
            await database.SaveDownloadedImageAsync(new(url, 1024,
                width is { } w && height is { } h ? (w, h) : null, null));
            Assert.False(database.RecentlyFailed(url));
        }
        _clock.Advance(ImageDatabase.FailureRetention + TimeSpan.FromDays(1));
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        Assert.Equal(unsuitable, reopened.IsUnsuitableForThumbnail(url));
        Assert.False(reopened.RecentlyFailed(url));
        await reopened.RecordFailureAsync(url, 404);
        await reopened.ClearFailureAsync(url);
        Assert.Equal(unsuitable, reopened.IsUnsuitableForThumbnail(url));
        await reopened.SaveDownloadedImageAsync(new(url, 2048, (640, 480), null));
        Assert.False(reopened.IsUnsuitableForThumbnail(url));
        using var refreshed = new ImageDatabase(DatabasePath, _clock);
        Assert.False(refreshed.IsUnsuitableForThumbnail(url));
    }

    [Fact]
    public async Task FailedDownloadedImageWritesKeepPersistedAndInMemorySuitability()
    {
        const string url = "https://site.test/tiny.png";
        using var database = new ImageDatabase(DatabasePath, _clock);
        await database.SaveDownloadedImageAsync(new(url, 1024, (48, 48), null));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_downloaded_image BEFORE INSERT ON downloadedImage
            BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;
            """;
        command.ExecuteNonQuery();
        await Assert.ThrowsAsync<SqliteException>(() => database.SaveDownloadedImageAsync(new(url, 2048, (640, 480), null)));
        Assert.True(database.IsUnsuitableForThumbnail(url));
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        Assert.True(reopened.IsUnsuitableForThumbnail(url));
    }

    [Fact]
    public async Task AccessDateUpdatesOrderUsageAndDeletionClearsSuitabilityAcrossBatches()
    {
        using var database = new ImageDatabase(DatabasePath, _clock);
        var urls = Enumerable.Range(0, 501).Select(index => "https://site.test/" + index).ToArray();
        foreach (var url in urls) await database.SaveDownloadedImageAsync(new(url, 100, null, null));
        _clock.Advance(TimeSpan.FromDays(1));
        await database.SaveDownloadedImageAccessDateAsync(urls[0]);
        var usage = await database.FetchDownloadedImageUsageByLeastRecentAccessAsync();
        Assert.Equal(urls[0], usage[^1].Url);
        Assert.Equal(_clock.GetUtcNow(), usage[^1].LastAccessed);
        await database.DeleteDownloadedImagesAsync(urls);
        Assert.Empty(await database.FetchDownloadedImageUsageByLeastRecentAccessAsync());
        Assert.All(urls, url => Assert.False(database.IsUnsuitableForThumbnail(url)));
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        Assert.Empty(await reopened.FetchDownloadedImageUsageByLeastRecentAccessAsync());
    }

    [Fact]
    public async Task FailedBatchedDeletionRollsBackEarlierRowsAndKeepsSuitability()
    {
        using var database = new ImageDatabase(DatabasePath, _clock);
        var urls = Enumerable.Range(0, 501).Select(index => "https://site.test/" + index).ToArray();
        foreach (var url in urls) await database.SaveDownloadedImageAsync(new(url, 100, null, null));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_image_delete BEFORE DELETE ON downloadedImage
            WHEN OLD.url='https://site.test/500' BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;
            """;
        command.ExecuteNonQuery();
        await Assert.ThrowsAsync<SqliteException>(() => database.DeleteDownloadedImagesAsync(urls));
        Assert.Equal(501, (await database.FetchDownloadedImageUsageByLeastRecentAccessAsync()).Count);
        Assert.All(urls, url => Assert.True(database.IsUnsuitableForThumbnail(url)));
    }

    [Fact]
    public async Task ExistingDownloadedImageTablesGainValidationDatesWithoutLosingSourcesOrValidators()
    {
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE downloadedImage (url TEXT PRIMARY KEY NOT NULL,byteCount INTEGER NOT NULL,
                    pixelWidth INTEGER,pixelHeight INTEGER,lastAccessed REAL NOT NULL,etag TEXT,lastModified TEXT);
                INSERT INTO downloadedImage VALUES ('https://site.test/icon',100,48,48,0,'"old"',NULL);
                """;
            command.ExecuteNonQuery();
        }
        using var database = new ImageDatabase(DatabasePath, _clock);
        var old = Assert.IsType<DownloadedImageValidation>(await database.FetchDownloadedImageValidationAsync("https://site.test/icon"));
        Assert.Equal(DateTimeOffset.UnixEpoch, old.NextCheck);
        Assert.Equal("\"old\"", old.ConditionalGet?.ETag);
        Assert.True(database.IsUnsuitableForThumbnail("https://site.test/icon"));
        await database.SaveDownloadedImageNextCheckAsync("https://site.test/icon", _clock.GetUtcNow().AddDays(3));
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        Assert.Equal(_clock.GetUtcNow().AddDays(3),
            (await reopened.FetchDownloadedImageValidationAsync("https://site.test/icon"))?.NextCheck);
    }

    [Fact]
    public async Task StartupDeletesFailuresOnlyAfterThirtyThreeDays()
    {
        using (var database = new ImageDatabase(DatabasePath, _clock)) await database.RecordFailureAsync("https://site.test/icon", 404);
        _clock.Advance(ImageDatabase.FailureRetention);
        using (var database = new ImageDatabase(DatabasePath, _clock)) Assert.Equal(1L, FailureCount());
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        Assert.Equal(0L, FailureCount());
    }

    [Fact]
    public async Task FailedWritesLeaveThePreviousCacheEntryUntouched()
    {
        using var database = new ImageDatabase(DatabasePath, _clock);
        await database.SaveHomePageFaviconAsync("https://site.test/", "old");
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_favicon BEFORE INSERT ON homePageFavicon BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => database.SaveHomePageFaviconAsync("https://site.test/", "new"));
        Assert.Equal("old", database.FaviconUrl("https://site.test/"));
    }

    [Fact]
    public async Task VacuumKeepsMappingsAndDisposedReadsFail()
    {
        var database = new ImageDatabase(DatabasePath, _clock);
        await database.SaveFeedIconUrlAsync("feed", "icon");
        await database.VacuumAsync();
        Assert.Equal("icon", database.IconUrl("feed"));
        database.Dispose();
        Assert.Throws<ObjectDisposedException>(() => database.IconUrl("feed"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.SaveFeedIconUrlAsync("feed", "new"));
    }

    [Fact]
    public async Task StartupAndMaintenanceCheckpointInsteadOfAdvancingVacuumBookkeeping()
    {
        var first = _clock.GetUtcNow();
        using (var database = new ImageDatabase(DatabasePath, _clock))
            await database.SaveFeedIconUrlAsync("feed", "icon");
        using (var connection = Open())
        {
            RSDatabaseInfoTable.CreateTableIfNeeded(connection);
            RSDatabaseInfoTable.SetLastVacuumDate(connection, first);
        }
        _clock.Advance(TimeSpan.FromDays(14));
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        await reopened.VacuumAsync();
        using var saved = Open();
        Assert.Equal("wal", saved.Scalar<string>("PRAGMA journal_mode;"));
        Assert.Equal(first, RSDatabaseInfoTable.LastVacuumDate(saved));
        Assert.Equal(0L, new FileInfo(DatabasePath + "-wal").Length);
        Assert.Equal("icon", reopened.IconUrl("feed"));
    }

    [Fact]
    public async Task ThumbnailResultsSurviveRestartIncludingNegativeResultsAndChunkedQueries()
    {
        var records = Enumerable.Range(0, 501)
            .Select(index => new ArticleThumbnailRecord(index.ToString(), index % 2 == 0
                ? [$"https://example.com/{index}.jpg", $"https://example.com/{index}-second.jpg"] : []))
            .ToArray();
        using (var database = new ImageDatabase(DatabasePath, _clock))
            await database.SaveArticleThumbnailsAsync(records);
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        var loaded = await reopened.FetchArticleThumbnailsAsync(records.Select(record => record.ArticleId).Append("missing"));
        Assert.Equal(records.Length, loaded.Count);
        Assert.True(records.ToHashSet().SetEquals(loaded));
    }

    [Fact]
    public async Task StartupExpiresThumbnailResultsOnlyAfterThirtyThreeDays()
    {
        using (var database = new ImageDatabase(DatabasePath, _clock))
            await database.SaveArticleThumbnailsAsync([new("article", [])]);
        _clock.Advance(ImageDatabase.FailureRetention);
        using (var database = new ImageDatabase(DatabasePath, _clock))
            Assert.Single(await database.FetchArticleThumbnailsAsync(["article"]));
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var reopened = new ImageDatabase(DatabasePath, _clock);
        Assert.Empty(await reopened.FetchArticleThumbnailsAsync(["article"]));
    }

    [Fact]
    public async Task FailedThumbnailBatchRollsBackEarlierRows()
    {
        using var database = new ImageDatabase(DatabasePath, _clock);
        await database.SaveArticleThumbnailsAsync([new("existing", ["https://example.com/old.jpg"])]);
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_thumbnail BEFORE INSERT ON articleThumbnailURLs
                WHEN NEW.articleID='rejected' BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => database.SaveArticleThumbnailsAsync(
            [new("existing", ["https://example.com/new.jpg"]), new("rejected", [])]));
        var saved = Assert.Single(await database.FetchArticleThumbnailsAsync(["existing", "rejected"]));
        Assert.Equal(["https://example.com/old.jpg"], saved.ThumbnailUrls);
    }

    private long FailureCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM downloadFailure;";
        return (long)command.ExecuteScalar()!;
    }
    private SqliteConnection Open(string? path = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
