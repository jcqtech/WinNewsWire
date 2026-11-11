using WinNewsWire.Feedly;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

/// <summary>Account/Tests/AccountTests/Feedly/FeedlyFeedNameTests.swift.</summary>
public class FeedlyFeedNameTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task MissingTitleKeepsExistingName()
    {
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate());
        var folder = MakeFolder(account);
        await SyncTitle("Feed One", folder, account);
        await SyncTitle(null, folder, account);
        Assert.Equal("Feed One", folder.Feeds.Single(f => f.FeedID == "feed/1").Name);
    }

    [Fact]
    public async Task EmptyTitleKeepsExistingName()
    {
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate());
        var folder = MakeFolder(account);
        await SyncTitle("Feed One", folder, account);
        await SyncTitle("", folder, account);
        Assert.Equal("Feed One", folder.Feeds.Single(f => f.FeedID == "feed/1").Name);
    }

    [Fact]
    public async Task ServerRenameLeavesEditedNameAlone()
    {
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate());
        var folder = MakeFolder(account);
        await SyncTitle("Feed One", folder, account);
        var feed = folder.Feeds.Single(f => f.FeedID == "feed/1");
        feed.EditedName = "Edited Name";
        await SyncTitle("Renamed On Feedly", folder, account);
        Assert.Equal("Edited Name", feed.EditedName);
        Assert.Equal("Renamed On Feedly", feed.Name);
        Assert.Equal("Edited Name", feed.NameForDisplay);
    }

    private static Folder MakeFolder(Account account)
    {
        var folder = account.AddFolder("Folder");
        folder.ExternalID = "folder/1";
        return folder;
    }

    private static Task SyncTitle(string? title, Folder folder, Account account)
        => FeedlyFolderReconciliation.SyncFeedsForCollectionFoldersAsync(account, [([new FeedlyFeed("feed/1", title, null, null)], folder)]);

    public void Dispose() => _manager.Dispose();
}
