using Microsoft.Data.Sqlite;
using WinNewsWire.Core;
using WinNewsWire.Web;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public void ExistingWindowsJsonMigratesWithoutChangingMembershipOrIDs()
    {
        var directory = Path.Combine(AppConfig.AccountsDirectory, "legacy-json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Settings.json"), """
            {
              "Name":"Existing account",
              "TopLevelFeeds":[{"FeedID":"legacy-id","Url":"https://example.com/feed","Name":"Publisher","EditedName":"Custom",
                "ExternalID":"subscription","ETag":"etag","LastModified":"yesterday","NewArticleNotificationsEnabled":true,"ReaderViewAlwaysEnabled":true}],
              "Folders":[{"FolderID":7,"Name":"Folder","ExternalID":"remote-folder","Feeds":[
                {"FeedID":"legacy-id","Url":"https://example.com/feed","Name":"Publisher","EditedName":"Custom",
                 "ExternalID":"subscription","ETag":"etag","LastModified":"yesterday","NewArticleNotificationsEnabled":true,"ReaderViewAlwaysEnabled":true}]}]
            }
            """);
        var account = _manager.CreateAccount("legacy-json");
        var feed = Assert.Single(account.TopLevelFeeds);
        Assert.Equal("Existing account", account.Name);
        Assert.Equal("legacy-id", feed.FeedID);
        Assert.Same(feed, Assert.Single(Assert.Single(account.Folders).Feeds));
        Assert.Equal("remote-folder", account.Folders[0].ExternalID);
        var settings = account.FeedSettingsDatabase.AllRows()[feed.Url];
        Assert.Equal("Custom", settings.EditedName);
        Assert.Equal("subscription", settings.ExternalID);
        Assert.Equal("etag", settings.ConditionalGetInfo?.ETag);
        Assert.Equal("yesterday", settings.ConditionalGetInfo?.LastModified);
        Assert.True(settings.NewArticleNotificationsEnabled);
        Assert.True(settings.ReaderViewAlwaysEnabled);
    }

    [Fact]
    public void FeedPreferenceWritesDoNotRequireAnAccountSnapshot()
    {
        var account = _manager.CreateAccount();
        var feed = account.AddFeed("https://example.com/feed");
        feed.EditedName = "Persisted directly";
        feed.ReaderViewAlwaysEnabled = true;
        feed.NewArticleNotificationsEnabled = true;
        Assert.DoesNotContain("Persisted directly", File.ReadAllText(Path.Combine(account.AccountDirectory, "Settings.json")));
        var reopened = _manager.Reopen(account);
        var restored = Assert.Single(reopened.FlattenedFeeds());
        Assert.Equal("Persisted directly", restored.EditedName);
        Assert.True(restored.ReaderViewAlwaysEnabled);
        Assert.True(restored.NewArticleNotificationsEnabled);
    }

    [Fact]
    public void CacheAndResponseMetadataSurviveRestart()
    {
        var account = _manager.CreateAccount();
        var feed = account.AddFeed("https://example.com/feed");
        var timestamp = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
        feed.CacheControlInfo = new(timestamp, TimeSpan.FromHours(1));
        feed.LastCheckDate = timestamp;
        feed.LastResponseCode = 304;
        feed.ETag = "cached-etag";
        feed.ConditionalGetInfoDate = timestamp;
        var reopened = _manager.Reopen(account);
        var restored = Assert.Single(reopened.FlattenedFeeds());
        Assert.Equal(new CacheControlInfo(timestamp, TimeSpan.FromHours(1)), restored.CacheControlInfo);
        Assert.Equal(timestamp, restored.LastCheckDate);
        Assert.Equal(304, restored.LastResponseCode);
        Assert.Equal("cached-etag", restored.ETag);
        Assert.Equal(timestamp, restored.ConditionalGetInfoDate);
    }

    [Fact]
    public void OpmlRestorationUsesSqliteMetadata()
    {
        var account = _manager.CreateAccount();
        var feed = account.AddFeed("https://example.com/feed", "Publisher", feedID: "canonical-id");
        account.SaveChanges();
        feed.EditedName = "SQLite name";
        feed.NewArticleNotificationsEnabled = true;
        File.Delete(Path.Combine(account.AccountDirectory, "Settings.json"));
        var reopened = _manager.Reopen(account);
        var restored = Assert.Single(reopened.FlattenedFeeds());
        Assert.Equal("canonical-id", restored.FeedID);
        Assert.Equal("SQLite name", restored.EditedName);
        Assert.True(restored.NewArticleNotificationsEnabled);
    }

    [Fact]
    public void AccountPreferencesPersistWatermarksAndConditionalHeaders()
    {
        var account = _manager.CreateAccount();
        var timestamp = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
        account.Name = "Custom account";
        account.IsActive = false;
        account.Settings.Username = " user ";
        account.Settings.EndpointUrl = new("https://reader.example/api/");
        account.Settings.LastArticleFetchStartTime = timestamp;
        account.Settings.LastRefreshCompletedDate = timestamp.AddMinutes(1);
        account.Settings.SetConditionalGetInfo("subscriptions", new(null, "version"));
        var reopened = _manager.Reopen(account);
        Assert.Equal("Custom account", reopened.Name);
        Assert.False(reopened.IsActive);
        Assert.Equal("user", reopened.CredentialUsername);
        Assert.Equal("https://reader.example/api/", reopened.Server);
        Assert.Equal(timestamp, reopened.Settings.LastArticleFetchStartTime);
        Assert.Equal(timestamp.AddMinutes(1), reopened.Settings.LastRefreshCompletedDate);
        Assert.Equal("version", reopened.Settings.ConditionalGetInfo("subscriptions")?.ETag);
    }

    [Fact]
    public void LegacyAccountDatabaseOnlyFillsMissingCredentials()
    {
        var path = Path.Combine(AppConfig.DataDirectory, "AccountSettings.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE accountSettings (accountID TEXT, username TEXT, endpointURL TEXT);
                INSERT INTO accountSettings VALUES ('legacy', 'legacy-user', 'https://legacy.example/api/');
                """;
            command.ExecuteNonQuery();
        }
        var account = _manager.CreateAccount("legacy");
        Assert.Equal("legacy-user", account.CredentialUsername);
        Assert.Equal("https://legacy.example/api/", account.Server);
        account.Settings.Username = "new-user";
        account.Settings.EndpointUrl = new("https://new.example/api/");
        var reopened = _manager.Reopen(account);
        Assert.Equal("new-user", reopened.CredentialUsername);
        Assert.Equal("https://new.example/api/", reopened.Server);
    }

    [Fact]
    public void EmptyCredentialAssignmentsDoNotEraseStoredSettings()
    {
        var account = _manager.CreateAccount();
        account.Settings.Username = " user ";
        account.Settings.EndpointUrl = new("https://reader.example/api/");
        account.Settings.Username = " ";
        account.Settings.EndpointUrl = null;
        Assert.Equal("user", account.CredentialUsername);
        Assert.Equal("https://reader.example/api/", account.Server);
    }

    [Fact]
    public void SameUrlInDifferentAccountsHasIndependentPreferences()
    {
        var first = _manager.CreateAccount();
        var second = _manager.CreateAccount();
        var one = first.AddFeed("https://example.com/feed");
        var two = second.AddFeed(one.Url);
        one.EditedName = "First";
        two.EditedName = "Second";
        Assert.Equal("First", first.FeedSettingsDatabase.AllRows()[one.Url].EditedName);
        Assert.Equal("Second", second.FeedSettingsDatabase.AllRows()[two.Url].EditedName);
    }

    [Fact]
    public void ConditionalHeadersKeepTheirDateUntilChangedAndClearTogether()
    {
        var account = _manager.CreateAccount();
        var feed = account.AddFeed("https://example.com/feed");
        feed.Settings.ConditionalGetInfo = new(null, "etag");
        var date = feed.ConditionalGetInfoDate;
        feed.Settings.ConditionalGetInfo = new(null, "etag");
        Assert.Equal(date, feed.ConditionalGetInfoDate);
        feed.Settings.ConditionalGetInfo = null;
        Assert.Null(feed.ConditionalGetInfoDate);
        Assert.Null(account.FeedSettingsDatabase.AllRows()[feed.Url].ConditionalGetInfoDate);
    }

    [Fact]
    public void EmptySubscriptionCleanupDoesNotEraseRecoverableMetadata()
    {
        using var database = new FeedSettingsDatabase(":memory:");
        database.EnsureFeedExists("one", "1");
        database.EnsureFeedExists("two", "2");
        database.DeleteSettingsForFeedsNotIn(new HashSet<string>());
        Assert.Equal(2, database.AllRows().Count);
        database.DeleteSettingsForFeedsNotIn(new HashSet<string> { "one" });
        Assert.Equal("one", Assert.Single(database.AllRows()).Key);
    }

    public void Dispose() => _manager.Dispose();
}
