using Microsoft.Data.Sqlite;
using WinNewsWire.Database;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class FeedSettingsDatabaseTests
{
    [Fact]
    public void RefreshedMetadataIsPersistedInOneWriteWithPropertyNotifications()
    {
        using var directory = new SettingsTestDirectory();
        using var database = new FeedSettingsDatabase(Path.Combine(directory.Path, "feeds.db"));
        const string url = "https://example.com/feed";
        var settings = new FeedSettings(url, url, database) { EditedName = "Keep this", NewArticleNotificationsEnabled = true };
        using var observer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath, Pooling = false
        }.ToString());
        observer.Open();
        observer.Execute("""
            CREATE TABLE refreshWrites (count INTEGER);
            INSERT INTO refreshWrites VALUES (0);
            CREATE TRIGGER count_refresh AFTER INSERT ON feedSettings BEGIN UPDATE refreshWrites SET count = count + 1; END;
            """);
        var columns = new List<FeedSettingsDatabase.Column>();
        settings.SettingChanged += (_, column) => columns.Add(column);
        var before = settings.CurrentValues;
        var after = before with
        {
            HomePageUrl = "https://example.com/", IconUrl = "https://example.com/icon.png",
            ContentHash = "changed", LastCheckDate = DateTime.UtcNow, LastResponseCode = 200
        };
        Assert.True(settings.TryApplyRefreshMetadata(before, after));
        Assert.Equal(1L, observer.Scalar<long>("SELECT count FROM refreshWrites;"));
        Assert.Equal(5, columns.Count);
        var stored = database.AllRows()[url];
        Assert.Equal("changed", stored.ContentHash);
        Assert.Equal("Keep this", stored.EditedName);
        Assert.True(stored.NewArticleNotificationsEnabled);
    }

    [Fact]
    public void StaleRefreshSnapshotCannotOverwriteANewerPreference()
    {
        var settings = new FeedSettings("https://example.com/feed", "feed");
        var before = settings.CurrentValues;
        settings.EditedName = "Changed by user";
        settings.ReaderViewAlwaysEnabled = true;
        Assert.False(settings.TryApplyRefreshMetadata(before, before with { ContentHash = "new" }));
        Assert.Equal("Changed by user", settings.EditedName);
        Assert.True(settings.ReaderViewAlwaysEnabled);
        Assert.Null(settings.ContentHash);
        var latest = settings.CurrentValues;
        Assert.True(settings.TryApplyRefreshMetadata(latest, latest with { ContentHash = "new" }));
        Assert.Equal("Changed by user", settings.EditedName);
    }

    [Fact]
    public void FailedRefreshMetadataWriteDoesNotPublishPartialState()
    {
        using var directory = new SettingsTestDirectory();
        using var database = new FeedSettingsDatabase(Path.Combine(directory.Path, "feeds.db"));
        var settings = new FeedSettings("https://example.com/feed", "feed", database);
        using var observer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.DatabasePath, Pooling = false
        }.ToString());
        observer.Open();
        observer.Execute("CREATE TRIGGER reject_refresh BEFORE INSERT ON feedSettings BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        int notifications = 0;
        settings.SettingChanged += (_, _) => notifications++;
        var before = settings.CurrentValues;
        Assert.Throws<SqliteException>(() => settings.TryApplyRefreshMetadata(before, before with { ContentHash = "new", LastResponseCode = 200 }));
        Assert.Same(before, settings.CurrentValues);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void FeedIDColumnIsUpdatable()
    {
        using var database = new FeedSettingsDatabase(":memory:");
        const string url = "https://example.com/feed.xml";
        database.EnsureFeedExists(url, url);
        database.SetString("feed/" + url, url, FeedSettingsDatabase.Column.FeedID);
        Assert.Equal("feed/" + url, database.AllRows()[url].FeedID);
    }

    [Fact]
    public void EnsureFeedExistsDoesNotReplaceFeedID()
    {
        using var database = new FeedSettingsDatabase(":memory:");
        const string url = "https://example.com/feed.xml";
        database.EnsureFeedExists(url, "feed/" + url);
        database.EnsureFeedExists(url, url);
        Assert.Equal("feed/" + url, database.AllRows()[url].FeedID);
    }

    [Fact]
    public void FailedSchemaSetupReleasesItsConnectionAndKeepsExistingData()
    {
        string path = Path.Combine(Path.GetTempPath(), "feed-settings-setup-" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        try
        {
            using (var seed = new SqliteConnection(connectionString))
            {
                seed.Open();
                seed.Execute("CREATE VIEW feedSettings AS SELECT 'keep' AS marker;");
            }
            Assert.Throws<SqliteException>(() => new FeedSettingsDatabase(path));
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.True(exclusive.Length > 0);
            using var reopened = new SqliteConnection(connectionString);
            reopened.Open();
            Assert.Equal("keep", reopened.Scalar<string>("SELECT marker FROM feedSettings;"));
        }
        finally { File.Delete(path); }
    }
}
