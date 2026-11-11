using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class AccountCopyTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task CrossAccountCopyKeepsTheSourceAndUsesDestinationIdentity()
    {
        var source = _manager.CreateAccount();
        var feed = source.AddFeed("https://example.com/feed", "Publisher");
        feed.EditedName = "Chosen title";
        feed.ExternalID = "source-only";
        var service = new CopyDelegate();
        var destination = _manager.CreateAccount(accountDelegate: service);
        var folder = destination.AddFolder("Destination");
        var copied = await destination.CopyFeedAsync(feed, folder);
        Assert.Same(feed, Assert.Single(source.TopLevelFeeds));
        Assert.Same(copied, Assert.Single(folder.Feeds));
        Assert.Equal(destination.AccountID, copied.AccountID);
        Assert.Equal("Chosen title", copied.EditedName);
        Assert.Equal("destination-id", copied.ExternalID);
        Assert.NotSame(feed, copied);
        Assert.Equal(1, service.Creations);
    }

    [Fact]
    public async Task ExistingDestinationFeedIsReusedWithoutRenaming()
    {
        var source = _manager.CreateAccount();
        var feed = source.AddFeed("https://example.com/feed", "Source title");
        var service = new CopyDelegate();
        var destination = _manager.CreateAccount(accountDelegate: service);
        var existing = destination.AddFeed(feed.Url, "Destination title");
        var folder = destination.AddFolder("Folder");
        Assert.Same(existing, await destination.CopyFeedAsync(feed, folder));
        Assert.Equal("Destination title", existing.NameForDisplay);
        Assert.Equal(0, service.Creations);
        Assert.Contains(existing, destination.TopLevelFeeds);
        Assert.Contains(existing, folder.Feeds);
    }

    [Fact]
    public async Task CopyToAnExistingMembershipIsANoop()
    {
        var account = _manager.CreateAccount();
        var feed = account.AddFeed("https://example.com/feed");
        Assert.Same(feed, await account.CopyFeedAsync(feed, null));
        Assert.Single(account.TopLevelFeeds);
    }

    [Fact]
    public async Task CopyIntoTheSameAccountAddsOnlyTheNewMembership()
    {
        var account = _manager.CreateAccount();
        var first = account.AddFolder("First");
        var second = account.AddFolder("Second");
        var feed = account.AddFeed("https://example.com/feed", folder: first);
        Assert.Same(feed, await account.CopyFeedAsync(feed, second));
        Assert.Contains(feed, first.Feeds);
        Assert.Contains(feed, second.Feeds);
    }

    [Fact]
    public async Task FailedDestinationSubscriptionNeverRemovesTheSource()
    {
        var source = _manager.CreateAccount();
        var feed = source.AddFeed("https://example.com/fail");
        var destination = _manager.CreateAccount(accountDelegate: new CopyDelegate { FailingUrl = feed.Url });
        await Assert.ThrowsAsync<HttpRequestException>(() => destination.CopyFeedAsync(feed, null));
        Assert.Same(feed, Assert.Single(source.TopLevelFeeds));
        Assert.Empty(destination.FlattenedFeeds());
    }

    [Fact]
    public async Task FolderCopyPreservesSourceAndReusesSubscribedDestinationFeeds()
    {
        var source = _manager.CreateAccount();
        var folder = source.AddFolder("Folder");
        var one = source.AddFeed("https://example.com/one", folder: folder);
        source.AddFeed("https://example.com/two", folder: folder);
        var service = new CopyDelegate();
        var destination = _manager.CreateAccount(accountDelegate: service);
        var existing = destination.AddFeed(one.Url);
        var copied = await destination.CopyFolderAsync(folder);
        Assert.Equal(2, folder.Feeds.Count);
        Assert.Equal(2, copied.Feeds.Count);
        Assert.Contains(existing, copied.Feeds);
        Assert.Equal(1, service.Creations);
        Assert.Equal(destination.AccountID, copied.AccountID);
    }

    [Fact]
    public async Task FolderCopyReportsPartialFailureWithoutLosingCompletedSubscriptions()
    {
        var source = _manager.CreateAccount();
        var folder = source.AddFolder("Folder");
        source.AddFeed("https://example.com/one", folder: folder);
        source.AddFeed("https://example.com/fail", folder: folder);
        var destination = _manager.CreateAccount(accountDelegate: new CopyDelegate { FailingUrl = "https://example.com/fail" });
        await Assert.ThrowsAsync<HttpRequestException>(() => destination.CopyFolderAsync(folder));
        Assert.Equal(2, folder.Feeds.Count);
        var reopened = _manager.Reopen(destination);
        Assert.Equal("https://example.com/one", Assert.Single(Assert.Single(reopened.Folders).Feeds).Url);
    }

    [Fact]
    public async Task DuplicateFolderNamesAndForeignDestinationsAreRejected()
    {
        var source = _manager.CreateAccount();
        var folder = source.AddFolder("Folder");
        var feed = source.AddFeed("https://example.com/feed");
        var destination = _manager.CreateAccount(accountDelegate: new CopyDelegate());
        destination.AddFolder("Folder");
        await Assert.ThrowsAsync<InvalidOperationException>(() => destination.CopyFolderAsync(folder));
        await Assert.ThrowsAsync<ArgumentException>(() => destination.CopyFeedAsync(feed, folder));
    }

    private sealed class CopyDelegate : IAccountDelegate
    {
        public AccountType Type => AccountType.OnMyMac;
        public int Creations { get; private set; }
        public string? FailingUrl { get; init; }
        public Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
        {
            if (urlOrSite == FailingUrl) throw new HttpRequestException("Subscription failed.");
            Creations++;
            var feed = account.AddFeed(urlOrSite, "Publisher", folder);
            feed.EditedName = name;
            feed.ExternalID = "destination-id";
            return Task.FromResult<Feed?>(feed);
        }
        public Task<Folder> CreateFolderAsync(Account account, string name, CancellationToken ct = default)
            => Task.FromResult(account.AddFolder(name));
        public Task AddFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        {
            account.AddFeed(feed.Url, folder: folder);
            return Task.CompletedTask;
        }
        public Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct) => Task.CompletedTask;
    }
    public void Dispose() => _manager.Dispose();
}
