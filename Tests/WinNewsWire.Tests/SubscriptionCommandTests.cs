using System.Net;
using System.Text;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Commands;
using WinNewsWire.Core;
using WinNewsWire.Feedbin;
using WinNewsWire.NewsBlur;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

[CollectionDefinition("Account storage", DisableParallelization = true)]
public sealed class AccountStorageCollection;

[Collection("Account storage")]
public sealed class SubscriptionCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinNewsWire.CommandTests." + Guid.NewGuid().ToString("N"));
    private readonly List<Account.Account> _accounts = [];

    public SubscriptionCommandTests() => AppConfig.SetDataDirectoryOverride(_directory);

    private Account.Account Account(IAccountDelegate? accountDelegate = null)
    {
        accountDelegate ??= new LocalAccountDelegate();
        var account = new Account.Account(Guid.NewGuid().ToString("N"), accountDelegate.Type, "Test", accountDelegate);
        _accounts.Add(account);
        return account;
    }

    [Fact]
    public async Task DeleteFeedUndoRedoPreservesOtherMemberships()
    {
        var account = Account();
        var first = account.AddFolder("First");
        var second = account.AddFolder("Second");
        var feed = account.AddFeed("https://example.com/feed", folder: first);
        account.AddFeed(feed.Url, folder: second);
        var history = new UndoManager();
        await history.ExecuteAsync(DeleteCommand.ForFeed(account, feed, first));
        Assert.Empty(first.Feeds);
        Assert.Same(feed, Assert.Single(second.Feeds));
        await history.UndoAsync();
        Assert.Same(feed, Assert.Single(first.Feeds));
        Assert.Same(feed, Assert.Single(second.Feeds));
        await history.RedoAsync();
        Assert.Empty(first.Feeds);
        Assert.Same(feed, Assert.Single(second.Feeds));
    }

    [Fact]
    public async Task WholeFeedDeletionRemovesAndRestoresEveryMembership()
    {
        var account = Account();
        var first = account.AddFolder("First");
        var second = account.AddFolder("Second");
        var feed = account.AddFeed("https://example.com/feed");
        account.AddFeed(feed.Url, folder: first);
        account.AddFeed(feed.Url, folder: second);
        var history = new UndoManager();
        await history.ExecuteAsync(DeleteCommand.ForAllFeedMemberships([(account, feed), (account, feed)])!);
        Assert.Empty(account.FlattenedFeeds());
        Assert.Equal(2, account.Folders.Count);
        await history.UndoAsync();
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Contains(feed, first.Feeds);
        Assert.Contains(feed, second.Feeds);
        await history.RedoAsync();
        Assert.Empty(account.FlattenedFeeds());
    }

    [Fact]
    public async Task FailureAfterAnEarlierMembershipRemovalRollsBackAllMemberships()
    {
        var accountDelegate = new DeletionDelegate { FailFolder = "Second" };
        var account = Account(accountDelegate);
        var first = account.AddFolder("First");
        var second = account.AddFolder("Second");
        var feed = account.AddFeed("https://example.com/feed");
        account.AddFeed(feed.Url, folder: first);
        account.AddFeed(feed.Url, folder: second);
        var history = new UndoManager();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            history.ExecuteAsync(DeleteCommand.ForAllFeedMemberships([(account, feed)])!));
        Assert.False(history.CanUndo);
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Contains(feed, first.Feeds);
        Assert.Contains(feed, second.Feeds);
    }

    [Fact]
    public async Task AProviderThatRemovesTheEntireSubscriptionIsNotCalledForVanishedMemberships()
    {
        var accountDelegate = new DeletionDelegate { RemoveGlobally = true };
        var account = Account(accountDelegate);
        var folder = account.AddFolder("Folder");
        var feed = account.AddFeed("https://example.com/feed");
        account.AddFeed(feed.Url, folder: folder);
        var history = new UndoManager();
        await history.ExecuteAsync(DeleteCommand.ForAllFeedMemberships([(account, feed)])!);
        Assert.Equal(1, accountDelegate.RemoveCalls);
        Assert.Empty(account.FlattenedFeeds());
        await history.UndoAsync();
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Contains(feed, folder.Feeds);
        Assert.Equal(2, accountDelegate.AddCalls);
    }

    [Fact]
    public async Task WholeFeedUndoRetriesOnlyMembershipsThatWereNotRestored()
    {
        var accountDelegate = new DeletionDelegate();
        var account = Account(accountDelegate);
        var folder = account.AddFolder("Folder");
        var feed = account.AddFeed("https://example.com/feed");
        account.AddFeed(feed.Url, folder: folder);
        var history = new UndoManager();
        await history.ExecuteAsync(DeleteCommand.ForAllFeedMemberships([(account, feed)])!);
        accountDelegate.FailRestoreFolder = "Folder";
        await Assert.ThrowsAsync<HttpRequestException>(history.UndoAsync);
        Assert.True(history.CanUndo);
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Empty(folder.Feeds);
        accountDelegate.FailRestoreFolder = null;
        await history.UndoAsync();
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Contains(feed, folder.Feeds);
        Assert.Equal(3, accountDelegate.AddCalls);
    }

    [Fact]
    public async Task DeleteFolderUndoPreservesPreferencesAndFeedIdentity()
    {
        var account = Account();
        var folder = account.AddFolder("Folder");
        var feed = account.AddFeed("https://example.com/feed", folder: folder);
        feed.ReaderViewAlwaysEnabled = true;
        feed.NewArticleNotificationsEnabled = true;
        var history = new UndoManager();
        await history.ExecuteAsync(DeleteCommand.ForFolder(account, folder));
        Assert.Empty(account.Folders);
        await history.UndoAsync();
        Assert.Same(folder, Assert.Single(account.Folders));
        Assert.Same(feed, Assert.Single(folder.Feeds));
        Assert.True(feed.ReaderViewAlwaysEnabled);
        Assert.True(feed.NewArticleNotificationsEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveUndoPreservesAnExistingDestinationMembership(bool alreadyInDestination)
    {
        var account = Account();
        var source = account.AddFolder("Source");
        var destination = account.AddFolder("Destination");
        var feed = account.AddFeed("https://example.com/feed", folder: source);
        if (alreadyInDestination) account.AddFeed(feed.Url, folder: destination);
        var history = new UndoManager();
        await history.ExecuteAsync(new MoveFeedCommand(account, feed, source, destination));
        Assert.Empty(source.Feeds);
        Assert.Same(feed, Assert.Single(destination.Feeds));
        await history.UndoAsync();
        Assert.Same(feed, Assert.Single(source.Feeds));
        Assert.Equal(alreadyInDestination, destination.Feeds.Contains(feed));
        await history.RedoAsync();
        Assert.Empty(source.Feeds);
        Assert.Same(feed, Assert.Single(destination.Feeds));
    }

    [Fact]
    public async Task FailedServerDeletionIsNotRegisteredAsSuccessfulUndo()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        var account = Account(new FeedbinAccountDelegate(http: http));
        var feed = account.AddFeed("https://example.com/feed", feedID: "123"); feed.ExternalID = "100";
        var history = new UndoManager();
        await Assert.ThrowsAsync<HttpRequestException>(() => history.ExecuteAsync(DeleteCommand.ForFeed(account, feed, null)));
        Assert.False(history.CanUndo);
        Assert.Same(feed, Assert.Single(account.TopLevelFeeds));
    }

    [Fact]
    public async Task FailedServerRestoreCanRetryAndRedoUsesTheNewRemoteID()
    {
        bool failRestore = true;
        string? deletedID = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/reader/add_url")
                return failRestore ? new(HttpStatusCode.ServiceUnavailable)
                    : Json("""{"feed":{"id":456,"feed_title":"Feed","feed_address":"https://example.com/feed"}}""");
            deletedID = await request.Content!.ReadAsStringAsync();
            return Json("""{"code":1}""");
        }));
        var account = Account(new NewsBlurAccountDelegate(http: http));
        var feed = account.AddFeed("https://example.com/feed"); feed.ExternalID = "123";
        var history = new UndoManager();
        await history.ExecuteAsync(DeleteCommand.ForFeed(account, feed, null));
        await Assert.ThrowsAsync<HttpRequestException>(history.UndoAsync);
        Assert.Empty(account.TopLevelFeeds);
        Assert.True(history.CanUndo);
        failRestore = false;
        await history.UndoAsync();
        Assert.Same(feed, Assert.Single(account.TopLevelFeeds));
        Assert.Equal("456", feed.ExternalID);
        Assert.Equal("https://example.com/feed", feed.FeedID);
        await history.RedoAsync();
        Assert.Contains("feed_id=456", deletedID);
        Assert.Empty(account.TopLevelFeeds);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(request => Task.FromResult(respond(request))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }

    private sealed class DeletionDelegate : IAccountDelegate
    {
        private readonly LocalAccountDelegate _local = new();
        public AccountType Type => AccountType.OnMyMac;
        public string? FailFolder { get; init; }
        public bool RemoveGlobally { get; init; }
        public string? FailRestoreFolder { get; set; }
        public int RemoveCalls { get; private set; }
        public int AddCalls { get; private set; }
        public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task<Feed?> CreateFeedAsync(Account.Account account, string url, string? name, Folder? folder, CancellationToken ct)
            => Task.FromException<Feed?>(new NotSupportedException());
        public async Task RemoveFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        {
            RemoveCalls++;
            if (folder?.Name == FailFolder && FailFolder is not null) throw new HttpRequestException("Fixture deletion failed.");
            if (!RemoveGlobally) { await _local.RemoveFeedAsync(account, feed, folder, ct); return; }
            foreach (var member in account.Folders.Where(member => member.Feeds.Contains(feed)).ToArray())
                await _local.RemoveFeedAsync(account, feed, member, ct);
            if (account.TopLevelFeeds.Contains(feed)) await _local.RemoveFeedAsync(account, feed, null, ct);
        }
        public Task RestoreFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        {
            AddCalls++;
            if (folder?.Name == FailRestoreFolder && FailRestoreFolder is not null)
                return Task.FromException(new HttpRequestException("Fixture restoration failed."));
            return _local.RestoreFeedAsync(account, feed, folder, ct);
        }
    }

    public void Dispose()
    {
        foreach (var account in _accounts) account.Dispose();
        AppConfig.SetDataDirectoryOverride(null);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
