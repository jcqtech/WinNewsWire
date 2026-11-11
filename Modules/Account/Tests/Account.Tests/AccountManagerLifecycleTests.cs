using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class AccountManagerLifecycleTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Fact]
    public async Task OneFailedAccountDoesNotPreventRefreshingOtherAccounts()
    {
        await using var manager = new AccountManager();
        int refreshed = 0;
        manager.CreateRemoteAccount(AccountType.Feedbin, "Failing", new Delegate
        {
            OnRefresh = (_, _) => Task.FromException(new HttpRequestException("test failure"))
        });
        manager.CreateRemoteAccount(AccountType.Feedbin, "Working", new Delegate
        {
            OnRefresh = (_, _) => { refreshed++; return Task.CompletedTask; }
        });
        var error = await Assert.ThrowsAsync<AggregateException>(() => manager.RefreshAllAsync());
        Assert.IsType<HttpRequestException>(Assert.Single(error.InnerExceptions));
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task RemovalAndManagerDisposalWaitForCancelledWorkBeforeDeletingItsFiles()
    {
        var started = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        await using var manager = new AccountManager();
        var account = manager.CreateRemoteAccount(AccountType.Feedbin, "Remote", new Delegate
        {
            OnRefresh = async (owner, ct) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally
                {
                    cancelled.SetResult();
                    await cleanup.Task;
                    Assert.True(Directory.Exists(owner.AccountDirectory));
                    Assert.Empty(await owner.Database.FetchArticlesAsync("feed"));
                }
            }
        });
        var refresh = account.RefreshAllAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var removal = manager.RemoveAccountAsync(account);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveAccountAsync(account));
        var disposal = manager.DisposeAsync().AsTask();
        Assert.False(removal.IsCompleted);
        Assert.False(disposal.IsCompleted);
        cleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await Task.WhenAll(removal, disposal).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(Directory.Exists(account.AccountDirectory));
        using var reopened = new AccountManager();
        Assert.DoesNotContain(reopened.Accounts, candidate => candidate.AccountID == account.AccountID);
    }

    [Fact]
    public async Task FailureToPrepareRemovalLeavesTheAccountUsable()
    {
        using var manager = new AccountManager();
        int refreshed = 0;
        var account = manager.CreateRemoteAccount(AccountType.Feedbin, "Remote", new Delegate
        {
            OnRefresh = (_, _) => { refreshed++; return Task.CompletedTask; }
        });
        File.WriteAllText(Path.Combine(AppConfig.DataDirectory, "DeletedAccounts"), "occupied");
        var error = await Record.ExceptionAsync(() => manager.RemoveAccountAsync(account));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Contains(account, manager.Accounts);
        await account.RefreshAllAsync();
        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task FailedArchiveCleanupIsReportedWithoutResurrectingTheRemovedAccount()
    {
        using var manager = new AccountManager();
        var account = manager.CreateRemoteAccount(AccountType.Feedbin, "Remote", new Delegate());
        var marker = Path.Combine(account.AccountDirectory, "cleanup-lock");
        var deletedRoot = Path.Combine(AppConfig.DataDirectory, "DeletedAccounts");
        File.WriteAllText(marker, "test");
        File.SetAttributes(marker, FileAttributes.ReadOnly);
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => manager.RemoveAccountAsync(account));
            Assert.Contains("archived files", error.Message);
            Assert.DoesNotContain(account, manager.Accounts);
            Assert.False(Directory.Exists(account.AccountDirectory));
            Assert.Single(Directory.GetDirectories(deletedRoot));
            using var reopened = new AccountManager();
            Assert.DoesNotContain(reopened.Accounts, candidate => candidate.AccountID == account.AccountID);
        }
        finally
        {
            if (File.Exists(marker)) File.SetAttributes(marker, FileAttributes.Normal);
            if (Directory.Exists(deletedRoot))
                foreach (var file in Directory.EnumerateFiles(deletedRoot, "cleanup-lock", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task AnUnregisteredAccountCannotBeDeleted()
    {
        using var manager = new AccountManager();
        var other = _storage.CreateAccount();
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveAccountAsync(other));
        Assert.True(Directory.Exists(other.AccountDirectory));
    }

    [Fact]
    public async Task QueuedStatusNotificationsReachTheManagerOnlyAfterRealChanges()
    {
        using var manager = new AccountManager();
        var account = manager.CreateRemoteAccount(AccountType.Feedbin, "Remote", new Delegate());
        await account.Database.CreateStatusesIfNeededAsync(["article"]);
        int notifications = 0;
        manager.ArticleStatusesQueued += (sender, _) =>
        {
            Assert.Same(account, sender);
            notifications++;
        };
        await account.MarkAsync(["article"], Articles.ArticleStatus.Key.Starred, true);
        await account.MarkAsync(["article"], Articles.ArticleStatus.Key.Starred, true);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void AccountMetadataChangesNotifyOnlyAfterDurableChanges()
    {
        using var manager = new AccountManager();
        var account = manager.CreateRemoteAccount(AccountType.Feedbin, "Before", new Delegate());
        int stateChanges = 0, nameChanges = 0, managerChanges = 0;
        account.AccountStateChanged += (sender, _) =>
        {
            Assert.Same(account, sender);
            var saved = new AppDefaults(Path.Combine(account.AccountDirectory, "AccountPreferences.json"));
            Assert.False(saved.GetValue(account.AccountID + "-isActive", true));
            stateChanges++;
        };
        account.DisplayNameChanged += (sender, _) =>
        {
            Assert.Same(account, sender);
            var saved = new AppDefaults(Path.Combine(account.AccountDirectory, "AccountPreferences.json"));
            Assert.Equal("After", saved.GetValue<string>(account.AccountID + "-name"));
            nameChanges++;
        };
        manager.AccountsDidChange += (sender, _) => { Assert.Same(manager, sender); managerChanges++; };
        account.IsActive = true;
        account.Name = "Before";
        account.IsActive = false;
        account.IsActive = false;
        account.Name = "After";
        account.Name = "After";
        Assert.Equal(1, stateChanges);
        Assert.Equal(1, nameChanges);
        Assert.Equal(2, managerChanges);
    }

    [Fact]
    public void FailedMetadataWritesDoNotPublishAccountChanges()
    {
        using var manager = new AccountManager();
        var account = manager.CreateRemoteAccount(AccountType.Feedbin, "Before", new Delegate());
        int notifications = 0;
        manager.AccountsDidChange += (_, _) => notifications++;
        using var locked = new FileStream(Path.Combine(account.AccountDirectory, "AccountPreferences.json"),
            FileMode.Open, FileAccess.Read, FileShare.None);
        var stateError = Record.Exception(() => account.IsActive = false);
        var nameError = Record.Exception(() => account.Name = "After");
        Assert.True(stateError is IOException or UnauthorizedAccessException);
        Assert.True(nameError is IOException or UnauthorizedAccessException);
        Assert.True(account.IsActive);
        Assert.Equal("Before", account.Name);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task ObserverFailureDoesNotDisposeAnAdoptedFactoryDelegate()
    {
        using var manager = new AccountManager();
        var accountDelegate = new Delegate();
        var previous = AccountDelegateFactory.Resolver;
        var observerError = new InvalidOperationException("Observer failed.");
        EventHandler observer = (_, _) => throw observerError;
        try
        {
            AccountDelegateFactory.Resolver = (_, _) => accountDelegate;
            manager.AccountsDidChange += observer;
            Assert.Same(observerError, Assert.Throws<InvalidOperationException>(
                () => manager.CreateRemoteAccount(AccountType.Feedbin, "Remote")));
            var account = Assert.Single(manager.Accounts, account => ReferenceEquals(account.Delegate, accountDelegate));
            Assert.Equal(0, accountDelegate.DisposeCount);
            await account.RefreshAllAsync();
        }
        finally
        {
            manager.AccountsDidChange -= observer;
            AccountDelegateFactory.Resolver = previous;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructorFailureDisposesFactoryOwnedDelegateWithoutReplacingTheOriginalError(bool cleanupFails)
    {
        using var manager = new AccountManager();
        var creationError = new IOException("Account settings could not be loaded.");
        var accountDelegate = new Delegate
        {
            ConstructionError = creationError,
            DisposalError = cleanupFails ? new InvalidOperationException("Cleanup failed.") : null
        };
        var previous = AccountDelegateFactory.Resolver;
        try
        {
            AccountDelegateFactory.Resolver = (_, _) => accountDelegate;
            Assert.Same(creationError, Assert.Throws<IOException>(() => manager.CreateRemoteAccount(AccountType.Feedbin, "Remote")));
            Assert.Equal(1, accountDelegate.DisposeCount);
            Assert.DoesNotContain(manager.Accounts, account => ReferenceEquals(account.Delegate, accountDelegate));
        }
        finally { AccountDelegateFactory.Resolver = previous; }
    }

    [Fact]
    public void ConstructorFailureLeavesAnUnadoptedBorrowedDelegateForItsCaller()
    {
        using var manager = new AccountManager();
        var creationError = new IOException("Account settings could not be loaded.");
        var accountDelegate = new Delegate { ConstructionError = creationError };
        Assert.Same(creationError, Assert.Throws<IOException>(
            () => manager.CreateRemoteAccount(AccountType.Feedbin, "Remote", accountDelegate)));
        Assert.Equal(0, accountDelegate.DisposeCount);
        accountDelegate.Dispose();
    }

    [Fact]
    public void MetadataFailureClosesTheConstructedAccountAndItsDelegateExactlyOnce()
    {
        using var manager = new AccountManager();
        var accountDelegate = new Delegate();
        var previous = AccountDelegateFactory.Resolver;
        try
        {
            AccountDelegateFactory.Resolver = (_, id) =>
            {
                Directory.CreateDirectory(Path.Combine(AppConfig.AccountsDirectory, id, "Meta.json"));
                return accountDelegate;
            };
            var error = Record.Exception(() => manager.CreateRemoteAccount(AccountType.Feedbin, "Remote"));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(1, accountDelegate.DisposeCount);
            Assert.DoesNotContain(manager.Accounts, account => ReferenceEquals(account.Delegate, accountDelegate));
        }
        finally { AccountDelegateFactory.Resolver = previous; }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Delegate : IAccountDelegate
    {
        public AccountType Type => AccountType.Feedbin;
        public bool SupportsRemoteSync => true;
        public Exception? ConstructionError { get; init; }
        public Exception? DisposalError { get; init; }
        public string? CredentialUsername => ConstructionError is { } error ? throw error : null;
        public int DisposeCount { get; private set; }
        public Func<Account, CancellationToken, Task>? OnRefresh { get; init; }
        public void Dispose()
        {
            DisposeCount++;
            if (DisposalError is { } error) throw error;
        }
        public Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
            => OnRefresh?.Invoke(account, ct) ?? Task.CompletedTask;
        public Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
            => Task.FromException<Feed?>(new NotSupportedException());
    }

    public void Dispose() => _storage.Dispose();
}
