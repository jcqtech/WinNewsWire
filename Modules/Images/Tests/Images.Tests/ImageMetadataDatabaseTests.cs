using Microsoft.Data.Sqlite;
using WinNewsWire.Database;
using Xunit;

namespace WinNewsWire.Images.Tests;

public sealed class ImageMetadataDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "image-metadata-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private string DatabasePath => Path.Combine(_directory, "ImageMetadata.db");
    public ImageMetadataDatabaseTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task NativeTablesAndPositiveMappingsSurviveRestart()
    {
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock))
        {
            await database.SaveHomePageFaviconAsync("https://site.test/", "https://site.test/favicon.png");
            await database.SaveFeedIconUrlAsync("https://site.test/feed", "https://site.test/feed.png");
            await database.RecordFailureAsync("https://site.test/broken", 404);
        }
        using var reopened = new ImageMetadataDatabase(DatabasePath, _clock);
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
        using var database = new ImageMetadataDatabase(DatabasePath, _clock);
        await database.SaveHomePageFaviconAsync("https://site.test/", null);
        _clock.Advance(ImageMetadataDatabase.FailureRetry - TimeSpan.FromSeconds(1));
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
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock)) await database.SaveHomePageFaviconAsync("https://site.test/", null);
        _clock.Advance(ImageMetadataDatabase.FailureRetry);
        using var reopened = new ImageMetadataDatabase(DatabasePath, _clock);
        Assert.False(reopened.HomePageHasNoFavicon("https://site.test/"));
    }

    [Fact]
    public async Task FailureRetryBoundaryAndExplicitClearAreDurable()
    {
        using var database = new ImageMetadataDatabase(DatabasePath, _clock);
        await database.RecordFailureAsync("https://site.test/icon", null);
        _clock.Advance(ImageMetadataDatabase.FailureRetry - TimeSpan.FromSeconds(1));
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
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock))
        {
            await database.SaveDownloadedImageAsync(new(url, 1024,
                width is { } w && height is { } h ? (w, h) : null, null));
            Assert.False(database.RecentlyFailed(url));
        }
        _clock.Advance(ImageMetadataDatabase.FailureRetention + TimeSpan.FromDays(1));
        using var reopened = new ImageMetadataDatabase(DatabasePath, _clock);
        Assert.Equal(unsuitable, reopened.IsUnsuitableForThumbnail(url));
        Assert.False(reopened.RecentlyFailed(url));
        await reopened.RecordFailureAsync(url, 404);
        await reopened.ClearFailureAsync(url);
        Assert.Equal(unsuitable, reopened.IsUnsuitableForThumbnail(url));
        await reopened.SaveDownloadedImageAsync(new(url, 2048, (640, 480), null));
        Assert.False(reopened.IsUnsuitableForThumbnail(url));
        using var refreshed = new ImageMetadataDatabase(DatabasePath, _clock);
        Assert.False(refreshed.IsUnsuitableForThumbnail(url));
    }

    [Fact]
    public async Task FailedDownloadedImageWritesKeepPersistedAndInMemorySuitability()
    {
        const string url = "https://site.test/tiny.png";
        using var database = new ImageMetadataDatabase(DatabasePath, _clock);
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
        using var reopened = new ImageMetadataDatabase(DatabasePath, _clock);
        Assert.True(reopened.IsUnsuitableForThumbnail(url));
    }

    [Fact]
    public async Task StartupDeletesFailuresOnlyAfterThirtyThreeDays()
    {
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock)) await database.RecordFailureAsync("https://site.test/icon", 404);
        _clock.Advance(ImageMetadataDatabase.FailureRetention);
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock)) Assert.Equal(1L, FailureCount());
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var reopened = new ImageMetadataDatabase(DatabasePath, _clock);
        Assert.Equal(0L, FailureCount());
    }

    [Fact]
    public async Task FailedWritesLeaveThePreviousCacheEntryUntouched()
    {
        using var database = new ImageMetadataDatabase(DatabasePath, _clock);
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
        var database = new ImageMetadataDatabase(DatabasePath, _clock);
        await database.SaveFeedIconUrlAsync("feed", "icon");
        await database.VacuumAsync();
        Assert.Equal("icon", database.IconUrl("feed"));
        database.Dispose();
        Assert.Throws<ObjectDisposedException>(() => database.IconUrl("feed"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.SaveFeedIconUrlAsync("feed", "new"));
    }

    [Fact]
    public async Task StartupVacuumSharesNativeBookkeepingAndPreservesImageMappings()
    {
        var first = _clock.GetUtcNow();
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock))
            await database.SaveFeedIconUrlAsync("feed", "icon");
        using (var connection = Open()) Assert.Equal(first, RSDatabaseInfoTable.LastVacuumDate(connection));
        _clock.Advance(TimeSpan.FromDays(13) - TimeSpan.FromSeconds(1));
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock))
        using (var connection = Open()) Assert.Equal(first, RSDatabaseInfoTable.LastVacuumDate(connection));
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var reopened = new ImageMetadataDatabase(DatabasePath, _clock);
        using var saved = Open();
        Assert.Equal(_clock.GetUtcNow(), RSDatabaseInfoTable.LastVacuumDate(saved));
        Assert.Equal("icon", reopened.IconUrl("feed"));
    }

    [Fact]
    public async Task ThumbnailResultsSurviveRestartIncludingNegativeResultsAndChunkedQueries()
    {
        var records = Enumerable.Range(0, 501)
            .Select(index => new ArticleThumbnailRecord(index.ToString(), index % 2 == 0
                ? [$"https://example.com/{index}.jpg", $"https://example.com/{index}-second.jpg"] : []))
            .ToArray();
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock))
            await database.SaveArticleThumbnailsAsync(records);
        using var reopened = new ImageMetadataDatabase(DatabasePath, _clock);
        var loaded = await reopened.FetchArticleThumbnailsAsync(records.Select(record => record.ArticleId).Append("missing"));
        Assert.Equal(records.Length, loaded.Count);
        Assert.True(records.ToHashSet().SetEquals(loaded));
    }

    [Fact]
    public async Task StartupExpiresThumbnailResultsOnlyAfterThirtyThreeDays()
    {
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock))
            await database.SaveArticleThumbnailsAsync([new("article", [])]);
        _clock.Advance(ImageMetadataDatabase.FailureRetention);
        using (var database = new ImageMetadataDatabase(DatabasePath, _clock))
            Assert.Single(await database.FetchArticleThumbnailsAsync(["article"]));
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var reopened = new ImageMetadataDatabase(DatabasePath, _clock);
        Assert.Empty(await reopened.FetchArticleThumbnailsAsync(["article"]));
    }

    [Fact]
    public async Task FailedThumbnailBatchRollsBackEarlierRows()
    {
        using var database = new ImageMetadataDatabase(DatabasePath, _clock);
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
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
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
