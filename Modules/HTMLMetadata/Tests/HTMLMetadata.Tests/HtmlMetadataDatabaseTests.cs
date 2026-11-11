using Microsoft.Data.Sqlite;
using WinNewsWire.Database;
using Xunit;

namespace WinNewsWire.HTMLMetadata.Tests;

public sealed class HtmlMetadataDatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "html-metadata-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private string DatabasePath => Path.Combine(_directory, "HTMLMetadata.db");
    public HtmlMetadataDatabaseTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task NativeSchemaAndJsonRoundTripAcrossRestart()
    {
        var record = Record();
        using (var database = new HtmlMetadataDatabase(DatabasePath, _clock)) await database.SaveAsync(record, 201);
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT lastChecked, statusCode, favicons, openGraphImages FROM metadata WHERE url=$url";
            command.Parameters.AddWithValue("$url", record.Url);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            Assert.Equal((_clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds, row.GetDouble(0));
            Assert.Equal(201, row.GetInt32(1));
            Assert.Contains("\"urlString\":", row.GetString(2));
            Assert.Contains("\"secureURL\":", row.GetString(3));
        }
        using var reopened = new HtmlMetadataDatabase(DatabasePath, _clock);
        var loaded = Assert.IsType<HtmlMetadataRecord>(await reopened.CachedRecordAsync(record.Url));
        Assert.Equal(record.Favicons, loaded.Favicons);
        Assert.Equal(record.AppleTouchIcons, loaded.AppleTouchIcons);
        Assert.Equal(record.OpenGraphImages, loaded.OpenGraphImages);
        Assert.Equal(record.FeedLinks, loaded.FeedLinks);
        Assert.Equal(record.TwitterImageUrl, loaded.TwitterImageUrl);
    }

    [Fact]
    public async Task SuccessExpiresAt149HoursButRemainsAvailableAsStale()
    {
        using var database = new HtmlMetadataDatabase(DatabasePath, _clock);
        var record = Record();
        await database.SaveAsync(record);
        _clock.Advance(HtmlMetadataDatabase.CacheExpiration - TimeSpan.FromSeconds(1));
        Assert.NotNull(await database.CachedRecordAsync(record.Url));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await database.CachedRecordAsync(record.Url));
        Assert.NotNull(await database.StaleCachedRecordAsync(record.Url));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(404, 264)]
    [InlineData(429, 264)]
    public async Task FailureRetryBoundariesMatchNative(int status, int hours)
    {
        using var database = new HtmlMetadataDatabase(DatabasePath, _clock);
        await database.NoteFailureAsync("https://example.test/", status);
        Assert.Null(await database.CachedRecordAsync("https://example.test/"));
        Assert.Null(await database.LastDownloadDateAsync("https://example.test/"));
        _clock.Advance(TimeSpan.FromHours(hours) - TimeSpan.FromSeconds(1));
        Assert.True(await database.RecentlyFailedAsync("https://example.test/"));
        database.EmptyCache();
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await database.RecentlyFailedAsync("https://example.test/"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(404)]
    public async Task FailureDoesNotReplaceSuccessAfterMemoryEviction(int status)
    {
        using var database = new HtmlMetadataDatabase(DatabasePath, _clock);
        var record = Record();
        var originalDate = _clock.GetUtcNow();
        await database.SaveAsync(record);
        database.EmptyCache();
        _clock.Advance(TimeSpan.FromDays(7));
        await database.NoteFailureAsync(record.Url, status);
        Assert.False(await database.RecentlyFailedAsync(record.Url));
        Assert.NotNull(await database.StaleCachedRecordAsync(record.Url));
        Assert.Equal(originalDate, await database.LastDownloadDateAsync(record.Url));
    }

    [Fact]
    public async Task StartupRetentionKeepsTheExactThirtyDayBoundary()
    {
        var record = Record();
        using (var database = new HtmlMetadataDatabase(DatabasePath, _clock)) await database.SaveAsync(record);
        _clock.Advance(HtmlMetadataDatabase.MaximumAge);
        using (var database = new HtmlMetadataDatabase(DatabasePath, _clock)) Assert.NotNull(await database.StaleCachedRecordAsync(record.Url));
        _clock.Advance(TimeSpan.FromSeconds(1));
        using var reopened = new HtmlMetadataDatabase(DatabasePath, _clock);
        Assert.Null(await reopened.StaleCachedRecordAsync(record.Url));
    }

    [Fact]
    public async Task FailedPersistenceDoesNotPublishNewMemoryState()
    {
        using var database = new HtmlMetadataDatabase(DatabasePath, _clock);
        var record = Record();
        await database.SaveAsync(record);
        using (var connection = Open())
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER reject_metadata BEFORE INSERT ON metadata BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;";
            trigger.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => database.SaveAsync(new(record.Url, twitterImageUrl: "https://example.test/new.png")));
        Assert.Equal(record.TwitterImageUrl, (await database.CachedRecordAsync(record.Url))?.TwitterImageUrl);
    }

    [Fact]
    public async Task CorruptJsonIsAnExplicitFailureNotAnEmptySuccess()
    {
        using var database = new HtmlMetadataDatabase(DatabasePath, _clock);
        await database.SaveAsync(Record());
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE metadata SET favicons='not json';";
            command.ExecuteNonQuery();
        }
        database.EmptyCache();
        await Assert.ThrowsAsync<InvalidDataException>(() => database.CachedRecordAsync("https://example.test/"));
    }

    [Fact]
    public async Task VacuumPreservesRowsAndDisposalRejectsQueries()
    {
        var database = new HtmlMetadataDatabase(DatabasePath, _clock);
        await database.SaveAsync(Record());
        await database.VacuumAsync();
        database.EmptyCache();
        Assert.NotNull(await database.CachedRecordAsync("https://example.test/"));
        database.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => database.CachedRecordAsync("https://example.test/"));
    }

    [Fact]
    public async Task StartupVacuumUsesTheNativePersistentThirteenDayBoundary()
    {
        var first = _clock.GetUtcNow();
        using (var database = new HtmlMetadataDatabase(DatabasePath, _clock)) await database.SaveAsync(Record());
        using (var connection = Open()) Assert.Equal(first, RSDatabaseInfoTable.LastVacuumDate(connection));
        _clock.Advance(TimeSpan.FromDays(13) - TimeSpan.FromMilliseconds(1));
        using (var database = new HtmlMetadataDatabase(DatabasePath, _clock))
        using (var connection = Open()) Assert.Equal(first, RSDatabaseInfoTable.LastVacuumDate(connection));
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        using (var database = new HtmlMetadataDatabase(DatabasePath, _clock))
        {
            using var connection = Open();
            Assert.Equal(_clock.GetUtcNow(), RSDatabaseInfoTable.LastVacuumDate(connection));
            Assert.NotNull(await database.StaleCachedRecordAsync(Record().Url));
        }
    }

    [Fact]
    public void FailedVacuumDoesNotAdvanceItsPersistentDate()
    {
        var first = _clock.GetUtcNow();
        using (var database = new HtmlMetadataDatabase(DatabasePath, _clock)) { }
        _clock.Advance(TimeSpan.FromDays(13));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        Assert.Throws<SqliteException>(() => connection.VacuumIfNeeded(_clock));
        Assert.Equal(first, RSDatabaseInfoTable.LastVacuumDate(connection));
    }

    [Fact]
    public void VacuumBookkeepingPreservesNativeFractionalUnixDatesAndOtherKeys()
    {
        using (var database = new HtmlMetadataDatabase(DatabasePath, _clock)) { }
        using var connection = Open();
        connection.Execute("INSERT INTO RSDatabaseInfo (key, value) VALUES ('other', 'keep');");
        var fractionalDate = _clock.GetUtcNow().AddMilliseconds(123);
        RSDatabaseInfoTable.SetLastVacuumDate(connection, fractionalDate);
        Assert.Equal((fractionalDate - DateTimeOffset.UnixEpoch).TotalSeconds,
            (RSDatabaseInfoTable.LastVacuumDate(connection)!.Value - DateTimeOffset.UnixEpoch).TotalSeconds);
        _clock.Advance(TimeSpan.FromSeconds(1));
        connection.VacuumIfNeeded(_clock, daysBetweenVacuums: 0);
        Assert.Equal(_clock.GetUtcNow(), RSDatabaseInfoTable.LastVacuumDate(connection));
        Assert.Equal("keep", connection.Scalar<string>("SELECT value FROM RSDatabaseInfo WHERE key='other';"));
    }

    private static HtmlMetadataRecord Record() => new("https://example.test/",
        [new("image/png", "https://example.test/favicon.png")],
        [new("apple-touch-icon", "180x180", 180, 180, "https://example.test/touch.png")],
        [new("News", "application/rss+xml", "https://example.test/feed")],
        [new("http://example.test/image.png", "https://example.test/image.png", "image/png", 512, 512, "Image")],
        "https://example.test/twitter.png");
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
