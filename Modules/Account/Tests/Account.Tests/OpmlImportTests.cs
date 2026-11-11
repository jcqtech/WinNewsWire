using System.Text;
using WinNewsWire.Parsers;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Account.Tests;

public class OpmlImportTests : IDisposable
{
    private readonly TestAccountManager _manager = new();
    private static ParserData Subscriptions => new("test.opml", Encoding.UTF8.GetBytes(
        OpmlRoundTripTests.Document("""<outline text="My Feed" xmlUrl="https://example.com/feed"/>""")));

    [Fact]
    public async Task ImportUsesDelegateThenRefreshesArticles()
    {
        var accountDelegate = new ImportDelegate();
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        await account.ImportOpmlAsync(Subscriptions);
        Assert.Equal(new[] { "import", "refresh" }, accountDelegate.Calls);
        Assert.Equal("My Feed", Assert.Single(account.TopLevelFeeds).EditedName);
        Assert.False(account.IsOpmlImportInProgress);
    }

    [Fact]
    public async Task ImportResetsTheArticleFetchDateBeforeRefreshing()
    {
        var accountDelegate = new ImportDelegate();
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        account.Settings.LastArticleFetchStartTime = DateTime.UtcNow;
        accountDelegate.RefreshAction = () =>
        {
            Assert.Null(account.Settings.LastArticleFetchStartTime);
            return Task.CompletedTask;
        };

        await account.ImportOpmlAsync(Subscriptions);

        Assert.Null(_manager.Reopen(account).Settings.LastArticleFetchStartTime);
    }

    [Fact]
    public async Task InvalidDocumentIsReportedBeforeAnyMutationOrNetworkCall()
    {
        var accountDelegate = new ImportDelegate();
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        account.AddFeed("https://example.com/existing");
        await Assert.ThrowsAsync<FeedParserException>(() =>
            account.ImportOpmlAsync(new ParserData("bad.opml", Encoding.UTF8.GetBytes("<rss/>"))));
        Assert.Empty(accountDelegate.Calls);
        Assert.Single(account.TopLevelFeeds);
        Assert.False(account.IsOpmlImportInProgress);
    }

    [Fact]
    public async Task UnsupportedAccountDoesNotPretendToImportLocally()
    {
        var accountDelegate = new ImportDelegate { SupportsOpmlImport = false };
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        await Assert.ThrowsAsync<NotSupportedException>(() => account.ImportOpmlAsync(Subscriptions));
        Assert.Empty(account.TopLevelFeeds);
        Assert.Empty(accountDelegate.Calls);
    }

    [Fact]
    public async Task InactiveAccountCannotImport()
    {
        var account = _manager.CreateAccount(accountDelegate: new ImportDelegate());
        account.IsActive = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.ImportOpmlAsync(Subscriptions));
        Assert.Empty(account.TopLevelFeeds);
    }

    [Fact]
    public async Task ConcurrentImportIsRejectedAndGuardIsReleased()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountDelegate = new ImportDelegate { ImportAction = _ => completion.Task };
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        var first = account.ImportOpmlAsync(Subscriptions);
        Assert.True(account.IsOpmlImportInProgress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.ImportOpmlAsync(Subscriptions));
        completion.SetResult();
        await first;
        Assert.False(account.IsOpmlImportInProgress);
    }

    [Fact]
    public async Task FailedImportIsReportedAndCanBeRetried()
    {
        var accountDelegate = new ImportDelegate { ImportAction = _ => throw new IOException("import failed") };
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        await Assert.ThrowsAsync<IOException>(() => account.ImportOpmlAsync(Subscriptions));
        Assert.False(account.IsOpmlImportInProgress);
        Assert.DoesNotContain("refresh", accountDelegate.Calls);
        accountDelegate.ImportAction = null;
        await account.ImportOpmlAsync(Subscriptions);
        Assert.Single(account.TopLevelFeeds);
    }

    [Fact]
    public async Task CancellationReleasesImportGuard()
    {
        using var cancellation = new CancellationTokenSource();
        var accountDelegate = new ImportDelegate { ImportAction = ct => Task.Delay(Timeout.Infinite, ct) };
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        var import = account.ImportOpmlAsync(Subscriptions, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
        Assert.False(account.IsOpmlImportInProgress);
        Assert.DoesNotContain("refresh", accountDelegate.Calls);
    }

    [Fact]
    public async Task RefreshFailureDoesNotLoseImportedSubscriptions()
    {
        var accountDelegate = new ImportDelegate { RefreshAction = () => throw new HttpRequestException("offline") };
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        await Assert.ThrowsAsync<HttpRequestException>(() => account.ImportOpmlAsync(Subscriptions));
        Assert.Single(account.TopLevelFeeds);
        Assert.Single(_manager.Reopen(account).TopLevelFeeds);
    }

    private sealed class ImportDelegate : IAccountDelegate
    {
        public AccountType Type => AccountType.OnMyMac;
        public bool SupportsOpmlImport { get; set; } = true;
        public List<string> Calls { get; } = [];
        public Func<CancellationToken, Task>? ImportAction { get; set; }
        public Func<Task>? RefreshAction { get; set; }

        public async Task ImportOpmlAsync(Account account, ParserData data, CancellationToken ct)
        {
            Calls.Add("import");
            if (ImportAction is not null) await ImportAction(ct);
            else account.LoadOpmlItems(OpmlParser.Parse(data).Children, isManualImport: true);
        }

        public Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            Calls.Add("refresh");
            return RefreshAction?.Invoke() ?? Task.CompletedTask;
        }

        public Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
            => throw new NotSupportedException();
    }

    public void Dispose() => _manager.Dispose();
}
