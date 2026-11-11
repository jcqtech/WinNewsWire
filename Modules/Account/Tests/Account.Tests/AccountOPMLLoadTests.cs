using WinNewsWire.Parsers;
using Xunit;

namespace WinNewsWire.Account.Tests;

/// <summary>Account/Tests/AccountTests/AccountOPMLLoadTests.swift, including its inputs and assertions.</summary>
public class AccountOPMLLoadTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public void ImportCreatesEachNewFeedSettingsRowOnce()
    {
        var account = _manager.CreateAccount();
        using var audit = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = account.FeedSettingsDatabase.DatabasePath, Pooling = false
            }.ToString());
        audit.Open();
        using var command = audit.CreateCommand();
        command.CommandText = """
            CREATE TABLE settingsWrites (feedURL TEXT);
            CREATE TRIGGER countSettingsWrites AFTER INSERT ON feedSettings
            BEGIN INSERT INTO settingsWrites VALUES (NEW.feedURL); END;
            """;
        command.ExecuteNonQuery();
        var items = OpmlRoundTripTests.Parse(string.Concat(Enumerable.Range(0, 50).Select(index =>
            $"""<outline text="Feed {index}" xmlUrl="https://example.com/feed/{index}" htmlUrl="https://example.com/{index}"/>""")));

        account.LoadOpmlItems(items);

        command.CommandText = "SELECT COUNT(*) FROM settingsWrites;";
        Assert.Equal(50L, (long)command.ExecuteScalar()!);
        Assert.Equal(50, account.FeedSettingsDatabase.AllRows().Count);
        Assert.All(account.FlattenedFeeds(), feed => Assert.Equal(feed.Name, feed.EditedName));
        Assert.Equal(50, _manager.Reopen(account).TopLevelFeeds.Count);
    }

    [Fact]
    public void RestoringOwnFileSetsNameOnly()
    {
        var account = _manager.CreateAccount();
        account.LoadOpmlItems(Items("Feed One"), isManualImport: false);
        var feed = Assert.Single(account.FlattenedFeeds());
        Assert.Equal("Feed One", feed.Name);
        Assert.Null(feed.EditedName);
        Assert.Equal("Feed One", feed.NameForDisplay);
    }

    [Fact]
    public void ImportingSetsEditedNameSoTheTitleSurvivesRefreshes()
    {
        var account = _manager.CreateAccount();
        account.LoadOpmlItems(Items("My Name For It"), isManualImport: true);
        var feed = Assert.Single(account.FlattenedFeeds());
        Assert.Equal("My Name For It", feed.Name);
        Assert.Equal("My Name For It", feed.EditedName);
    }

    [Fact]
    public void RestoringOwnFileLeavesAnExistingEditedNameAlone()
    {
        var account = _manager.CreateAccount();
        account.LoadOpmlItems(Items("Feed One"), isManualImport: false);
        var renamedFeed = Assert.Single(account.FlattenedFeeds());
        renamedFeed.EditedName = "My Name For It";
        var specifier = Assert.Single(Items("My Name For It")).FeedSpecifier!;
        var restoredFeed = account.NewFeed(specifier, isManualImport: false);
        Assert.Equal("My Name For It", restoredFeed.Name);
        Assert.Equal("My Name For It", restoredFeed.EditedName);
    }

    private static IReadOnlyList<OpmlItem> Items(string title) => OpmlRoundTripTests.Parse($"""
        <outline text="{title}" title="{title}" type="rss" version="RSS" htmlUrl="https://example.com/" xmlUrl="https://example.com/feed.xml"/>
        """);

    public void Dispose() => _manager.Dispose();
}
