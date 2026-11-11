using System.Runtime.Versioning;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Feedbin;
using WinNewsWire.ReaderAPI;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests;

[SupportedOSPlatform("windows")]
public sealed class AccountReauthorizationTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Fact]
    public async Task ReconnectingALegacyProfilePreservesSubscriptionsArticlesAndStatuses()
    {
        var account = _storage.CreateAccount(accountDelegate: new FeedbinAccountDelegate());
        var id = account.AccountID;
        var folder = account.AddFolder("Folder");
        var feed = account.AddFeed("https://example.com/feed", "Custom", folder, "17");
        var status = new ArticleStatus("stable-article", true, true, DateTime.UnixEpoch);
        await account.Database.UpdateArticlesAsync([new Article(id, status.ArticleID, feed.FeedID, "remote-entry",
            "Title", "Body", null, null, null, null, null, null, DateTime.UnixEpoch, null, null, null, status)]);
        var credentials = new Credentials(CredentialsType.Basic, "user", "updated-test-password");
        var replacement = new FeedbinAccountDelegate(credentials);
        await account.ReauthorizeAsync(replacement, [credentials]);
        Assert.Equal(id, account.AccountID);
        Assert.Same(replacement, account.Delegate);
        Assert.Same(folder, Assert.Single(account.Folders));
        Assert.Same(feed, Assert.Single(folder.Feeds));
        Assert.Equal(credentials, account.RetrieveCredentials(CredentialsType.Basic));

        using var restored = new AccountManager();
        var reloaded = restored.Accounts.Single(candidate => candidate.AccountID == id);
        Assert.Equal(credentials, Assert.IsType<FeedbinAccountDelegate>(reloaded.Delegate).API.Credentials);
        Assert.Equal("17", Assert.Single(Assert.Single(reloaded.Folders).Feeds).FeedID);
        var article = Assert.Single(await reloaded.Database.FetchArticlesAsync("17"));
        Assert.Equal("stable-article", article.ArticleID);
        Assert.True(article.Status.Read);
        Assert.True(article.Status.Starred);
    }

    [Fact]
    public async Task ReauthorizationCannotSilentlySwitchRemoteIdentity()
    {
        var old = new Credentials(CredentialsType.Basic, "first-user", "test");
        var original = new FeedbinAccountDelegate(old);
        var account = _storage.CreateAccount(accountDelegate: original);
        var changed = new Credentials(CredentialsType.Basic, "other-user", "test");
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.ReauthorizeAsync(new FeedbinAccountDelegate(changed), [changed]));
        Assert.Same(original, account.Delegate);
        Assert.Equal("first-user", account.CredentialUsername);
    }

    [Fact]
    public async Task FailedAuthenticationMetadataWriteKeepsTheOldDelegateAndCanRetry()
    {
        var original = new FeedbinAccountDelegate();
        var account = _storage.CreateAccount(accountDelegate: original);
        var credentials = new Credentials(CredentialsType.Basic, "user", "test");
        var replacement = new FeedbinAccountDelegate(credentials);
        using (File.Open(Path.Combine(account.AccountDirectory, "AccountPreferences.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => account.ReauthorizeAsync(replacement, [credentials]));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Same(original, account.Delegate);
        Assert.Null(account.CredentialUsername);
        await account.ReauthorizeAsync(replacement, [credentials]);
        Assert.Same(replacement, account.Delegate);
        Assert.Equal("user", account.CredentialUsername);
    }

    [Fact]
    public async Task LegacyUsernameAndServerAreUpdatedAsOnePreferenceWrite()
    {
        var original = new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://old.example/api/");
        var account = _storage.CreateAccount(accountDelegate: original);
        var credentials = new Credentials(CredentialsType.ReaderBasic, "user", "test");
        var replacement = new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://new.example/api/", credentials);
        using (File.Open(Path.Combine(account.AccountDirectory, "AccountPreferences.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => account.ReauthorizeAsync(replacement, [credentials]));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Null(account.CredentialUsername);
        Assert.Equal("https://old.example/api/", account.Server);
        await account.ReauthorizeAsync(replacement, [credentials]);
        Assert.Equal("user", account.CredentialUsername);
        Assert.Equal("https://new.example/api/", account.Server);
    }

    [Fact]
    public async Task FailedSecretStorageDoesNotChangeTheActiveDelegate()
    {
        var old = new Credentials(CredentialsType.Basic, "user", "old-test-secret");
        var original = new FeedbinAccountDelegate(old);
        var account = _storage.CreateAccount(accountDelegate: original);
        account.StoreCredentials(old);
        var updated = old with { Secret = "new-test-secret" };
        var path = Directory.EnumerateFiles(AppConfig.EnsureSubdirectory("Credentials"), "*.dat").Single();
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => account.ReauthorizeAsync(new FeedbinAccountDelegate(updated), [updated]));
        Assert.Same(original, account.Delegate);
        Assert.Equal(old, account.RetrieveCredentials(CredentialsType.Basic));
    }

    [Fact]
    public async Task ReauthorizationWaitsForRefreshAndCancellationDoesNotReplaceTheDelegate()
    {
        var original = new BlockingDelegate();
        var account = _storage.CreateAccount(accountDelegate: original);
        var refresh = account.RefreshAllAsync();
        await original.Started.Task;
        var credentials = new Credentials(CredentialsType.Basic, "user", "test");
        using var cancellation = new CancellationTokenSource();
        var reconnect = account.ReauthorizeAsync(new FeedbinAccountDelegate(credentials), [credentials], cancellation.Token);
        Assert.False(reconnect.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnect);
        Assert.Same(original, account.Delegate);
        original.Release.SetResult();
        await refresh;
    }

    [Fact]
    public void SelfHostedAccountsWithTheSameUsernameHaveSeparateCredentialScopes()
    {
        var first = _storage.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://first.example/api/"));
        var second = _storage.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://second.example/api/"));
        var one = new Credentials(CredentialsType.ReaderBasic, "user", "one-test-secret");
        var two = one with { Secret = "two-test-secret" };
        first.StoreCredentials(one);
        second.StoreCredentials(two);
        Assert.Equal(one, first.RetrieveCredentials(CredentialsType.ReaderBasic));
        Assert.Equal(two, second.RetrieveCredentials(CredentialsType.ReaderBasic));
        var restored = Assert.IsType<ReaderAPIAccountDelegate>(AccountDelegateFactory.Resolve(first.Type, first.AccountID, first.CredentialUsername, first.Server));
        Assert.Equal(one, restored.API.Credentials);
    }

    [Fact]
    public void RemovingEndpointCredentialsDoesNotReviveLegacyCredentialsOrAffectAnotherServer()
    {
        var legacy = new Credentials(CredentialsType.ReaderBasic, "user", "legacy-test-secret");
        CredentialsManager.Store(legacy, "FreshRSS");
        var first = _storage.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://first.example/api/", legacy));
        var second = _storage.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://second.example/api/", legacy));
        Assert.Equal(legacy, first.RetrieveCredentials(CredentialsType.ReaderBasic));
        first.RemoveCredentials(CredentialsType.ReaderBasic);
        Assert.Null(first.RetrieveCredentials(CredentialsType.ReaderBasic));
        Assert.Equal(legacy, second.RetrieveCredentials(CredentialsType.ReaderBasic));
    }

    [Fact]
    public async Task AnIdentifiedAccountCannotBeReconnectedToADifferentServer()
    {
        var credentials = new Credentials(CredentialsType.ReaderBasic, "user", "test");
        var original = new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://first.example/api/", credentials);
        var account = _storage.CreateAccount(accountDelegate: original);
        var replacement = new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://second.example/api/", credentials);
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.ReauthorizeAsync(replacement, [credentials]));
        Assert.Same(original, account.Delegate);
    }

    private sealed class BlockingDelegate : IAccountDelegate
    {
        public AccountType Type => AccountType.Feedbin;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
            => throw new NotSupportedException();
        public Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
        {
            Started.SetResult();
            return Release.Task;
        }
    }
    public void Dispose() => _storage.Dispose();
}
