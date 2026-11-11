using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class FeedSettingsImporterTests : IDisposable
{
    private const string Url = "https://example.com/feed";
    private readonly SettingsTestDirectory _directory = new();
    private readonly FeedSettingsDatabase _database;
    private static readonly DateTime Date = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(700_000_000);

    public FeedSettingsImporterTests() => _database = new(Path.Combine(_directory.Path, "FeedSettings.sqlite"));
    private void Import() => FeedSettingsImporter.ImportIfNeeded(_directory.Path, _database);
    private void Write(Dictionary<string, object> feeds) => _directory.WritePlist("FeedMetadata.plist", feeds);
    private FeedSettingsDatabase.Row ImportFeed(Dictionary<string, object> fields)
    {
        Write(new() { [Url] = fields });
        Import();
        return _database.AllRows()[Url];
    }

    [Fact]
    public void NoPlistFile()
    {
        Import();
        Assert.True(_database.IsEmpty);
    }

    [Fact]
    public void DatabaseAlreadyHasRows()
    {
        Write(new() { [Url] = new Dictionary<string, object> { ["feedID"] = "feed1" } });
        _database.EnsureFeedExists("https://other.com/feed", "other");
        Import();
        Assert.False(_database.AllRows().ContainsKey(Url));
        Assert.True(_database.AllRows().ContainsKey("https://other.com/feed"));
    }

    [Fact]
    public void UnreadablePlistFile()
    {
        Directory.CreateDirectory(Path.Combine(_directory.Path, "FeedMetadata.plist"));
        Import();
        Assert.True(_database.IsEmpty);
    }

    [Fact]
    public void CorruptedPlistFile()
    {
        File.WriteAllText(Path.Combine(_directory.Path, "FeedMetadata.plist"), "not a valid plist");
        Import();
        Assert.True(_database.IsEmpty);
    }

    [Fact]
    public void EmptyPlist()
    {
        Write(new());
        Import();
        Assert.True(_database.IsEmpty);
    }

    [Fact]
    public void NonDictionaryFeedValueSkipped()
    {
        Write(new() { [Url] = "not a dictionary" });
        Import();
        Assert.True(_database.IsEmpty);
    }

    [Fact]
    public void MinimalFeed()
    {
        var row = ImportFeed(new());
        Assert.Equal(Url, row.FeedID);
        Assert.Null(row.HomePageUrl);
        Assert.Null(row.EditedName);
        Assert.False(row.NewArticleNotificationsEnabled);
        Assert.False(row.ReaderViewAlwaysEnabled);
        Assert.Null(row.ConditionalGetInfo);
        Assert.Null(row.ConditionalGetInfoDate);
        Assert.Null(row.CacheControlInfo);
        Assert.Null(row.Authors);
        Assert.Null(row.ExternalID);
        Assert.Null(row.FolderRelationship);
        Assert.Null(row.LastCheckDate);
    }

    [Fact]
    public void AllStringFields()
    {
        var row = ImportFeed(new()
        {
            ["feedID"] = "custom-feed-id", ["homePageURL"] = "https://example.com", ["iconURL"] = "https://example.com/icon.png",
            ["faviconURL"] = "https://example.com/favicon.ico", ["editedName"] = "My Custom Name",
            ["contentHash"] = "abc123hash", ["subscriptionID"] = "ext-sub-99",
        });
        Assert.Equal("custom-feed-id", row.FeedID);
        Assert.Equal("https://example.com", row.HomePageUrl);
        Assert.Equal("https://example.com/icon.png", row.IconUrl);
        Assert.Equal("https://example.com/favicon.ico", row.FaviconUrl);
        Assert.Equal("My Custom Name", row.EditedName);
        Assert.Equal("abc123hash", row.ContentHash);
        Assert.Equal("ext-sub-99", row.ExternalID);
    }

    [Fact]
    public void BoolFields()
    {
        var row = ImportFeed(new() { ["isNotifyAboutNewArticles"] = true, ["isArticleExtractorAlwaysOn"] = true });
        Assert.True(row.NewArticleNotificationsEnabled);
        Assert.True(row.ReaderViewAlwaysEnabled);
    }

    [Fact]
    public void ConditionalGetInfo()
    {
        var row = ImportFeed(new()
        {
            ["conditionalGetInfo"] = new Dictionary<string, object> { ["lastModified"] = "Wed, 01 Jan 2025 00:00:00 GMT", ["etag"] = "abc123" },
        });
        Assert.NotNull(row.ConditionalGetInfo);
        Assert.Equal("Wed, 01 Jan 2025 00:00:00 GMT", row.ConditionalGetInfo.LastModified);
        Assert.Equal("abc123", row.ConditionalGetInfo.ETag);
    }

    [Fact]
    public void ConditionalGetInfoDate()
        => Assert.Equal(Date, ImportFeed(new() { ["conditionalGetInfoDate"] = Date }).ConditionalGetInfoDate);

    [Fact]
    public void CacheControlInfo()
    {
        var row = ImportFeed(new() { ["cacheControlInfo"] = new Dictionary<string, object> { ["dateCreated"] = Date, ["maxAge"] = 3600d } });
        Assert.NotNull(row.CacheControlInfo);
        Assert.Equal(Date, row.CacheControlInfo.DateCreated);
        Assert.Equal(3600, row.CacheControlInfo.MaxAge.TotalSeconds);
    }

    [Fact]
    public void CacheControlInfoPartialSkipped()
        => Assert.Null(ImportFeed(new() { ["cacheControlInfo"] = new Dictionary<string, object> { ["dateCreated"] = Date } }).CacheControlInfo);

    [Fact]
    public void Authors()
    {
        var row = ImportFeed(new()
        {
            ["authors"] = new object[]
            {
                new Dictionary<string, object> { ["name"] = "Brent Simmons", ["url"] = "https://inessential.com/", ["authorID"] = "author1" },
            },
        });
        var author = Assert.Single(row.Authors!);
        Assert.Equal("Brent Simmons", author.Name);
        Assert.Equal("https://inessential.com/", author.Url);
    }

    [Fact]
    public void FolderRelationship()
    {
        var row = ImportFeed(new() { ["folderRelationship"] = new Dictionary<string, object> { ["Tech"] = "folder-id-1", ["News"] = "folder-id-2" } });
        Assert.NotNull(row.FolderRelationship);
        Assert.Equal("folder-id-1", row.FolderRelationship["Tech"]);
        Assert.Equal("folder-id-2", row.FolderRelationship["News"]);
    }

    [Fact]
    public void LastCheckDate() => Assert.Equal(Date, ImportFeed(new() { ["lastCheckDate"] = Date }).LastCheckDate);

    [Fact]
    public void MultipleFeeds()
    {
        Write(new()
        {
            ["https://example.com/feed1"] = new Dictionary<string, object> { ["feedID"] = "id1", ["editedName"] = "Feed One" },
            ["https://example.com/feed2"] = new Dictionary<string, object> { ["feedID"] = "id2", ["editedName"] = "Feed Two" },
        });
        Import();
        var rows = _database.AllRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal("Feed One", rows["https://example.com/feed1"].EditedName);
        Assert.Equal("Feed Two", rows["https://example.com/feed2"].EditedName);
    }

    [Fact]
    public void ImportIsOneTime()
    {
        ImportFeed(new() { ["editedName"] = "Original" });
        Assert.Equal("Original", _database.AllRows()[Url].EditedName);
        Write(new() { [Url] = new Dictionary<string, object> { ["editedName"] = "Changed" } });
        Import();
        Assert.Equal("Original", _database.AllRows()[Url].EditedName);
    }

    [Fact]
    public void FeedIDDefaultsToFeedURL() => Assert.Equal(Url, ImportFeed(new() { ["editedName"] = "No Feed ID" }).FeedID);

    [Fact]
    public void FeedIDFromDict() => Assert.Equal("custom-id", ImportFeed(new() { ["feedID"] = "custom-id" }).FeedID);

    [Fact]
    public void BinaryPropertyListIsSupported()
    {
        _directory.WritePlist("FeedMetadata.plist", new()
        {
            [Url] = new Dictionary<string, object> { ["feedID"] = "binary-id", ["lastCheckDate"] = Date },
        }, binary: true);
        Import();
        Assert.Equal("binary-id", _database.AllRows()[Url].FeedID);
        Assert.Equal(Date, _database.AllRows()[Url].LastCheckDate);
    }

    public void Dispose()
    {
        _database.Dispose();
        _directory.Dispose();
    }
}
