using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class AccountManagerNewsSubscriptionTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Theory]
    [InlineData("https://netnewswire.blog/feed.xml")]
    [InlineData("https://netnewswire.blog/feed.json")]
    public void NativeNewsFeedsAreRecognizedInActiveAccountsOnly(string url)
    {
        using var manager = new AccountManager();
        Assert.False(manager.AnyAccountHasNetNewsWireNewsSubscription());
        var account = manager.CreateLocalAccount("News");
        account.AddFeed(url, "An edited name", account.AddFolder("News folder"));
        Assert.True(manager.AnyAccountHasFeedWithUrl(url));
        Assert.True(manager.AnyAccountHasNetNewsWireNewsSubscription());
        account.IsActive = false;
        Assert.False(manager.AnyAccountHasFeedWithUrl(url));
        Assert.False(manager.AnyAccountHasNetNewsWireNewsSubscription());
        account.IsActive = true;
        Assert.True(manager.AnyAccountHasNetNewsWireNewsSubscription());
    }

    [Fact]
    public void UnrelatedFeedsDoNotDisableTheNewsCommand()
    {
        using var manager = new AccountManager();
        manager.DefaultAccount.AddFeed("https://example.com/feed.xml");
        Assert.True(manager.AnyAccountHasFeedWithUrl("https://example.com/feed.xml"));
        Assert.False(manager.AnyAccountHasNetNewsWireNewsSubscription());
        Assert.Equal("https://netnewswire.blog/feed.xml", AccountManager.NetNewsWireNewsUrl);
    }

    public void Dispose() => _storage.Dispose();
}
