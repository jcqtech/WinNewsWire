using WinNewsWire.NewsBlur;
using Xunit;

namespace WinNewsWire.Account.Tests.NewsBlur;

/// <summary>Account/Tests/AccountTests/NewsBlur/NewsBlurFeedNameTests.swift.</summary>
public class NewsBlurFeedNameTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public void EmptyNameKeepsExistingName()
    {
        var accountDelegate = new NewsBlurAccountDelegate();
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        SyncName("Feed One", accountDelegate, account);
        SyncName("", accountDelegate, account);
        Assert.Equal("Feed One", account.FlattenedFeeds().Single(f => f.FeedID == "1").Name);
    }

    [Fact]
    public void ServerRenameLeavesEditedNameAlone()
    {
        var accountDelegate = new NewsBlurAccountDelegate();
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        SyncName("Feed One", accountDelegate, account);
        var feed = account.FlattenedFeeds().Single(f => f.FeedID == "1");
        feed.EditedName = "Edited Name";
        SyncName("Renamed On NewsBlur", accountDelegate, account);
        Assert.Equal("Edited Name", feed.EditedName);
        Assert.Equal("Renamed On NewsBlur", feed.Name);
        Assert.Equal("Edited Name", feed.NameForDisplay);
    }

    private static void SyncName(string name, NewsBlurAccountDelegate accountDelegate, Account account)
        => accountDelegate.SyncFeeds(account, [new NewsBlurFeed(1, name, "https://example.com/feed.rss", "https://example.com/")]);

    public void Dispose() => _manager.Dispose();
}
