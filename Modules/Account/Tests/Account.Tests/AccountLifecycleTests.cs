using WinNewsWire.Articles;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class AccountLifecycleTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Fact]
    public void RegisteredProgressReportersRemainUsableAfterReset()
    {
        var account = _storage.CreateAccount();
        var combined = new CombinedRefreshProgress();
        combined.AddAccount(account);
        var reporter = combined.GetProgressReporter(account);
        reporter.Report(new Core.ProgressInfo(0, 3, 3));
        combined.Reset();
        reporter.Report(new Core.ProgressInfo(2, 0, 2));
        Assert.Equal(new Core.ProgressInfo(2, 0, 2), combined.Snapshot);
        Assert.False(combined.IsComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshReportsCombinedProgressAndCompletesItsSessionEvenWhenCancelled(bool cancel)
    {
        var started = Signal();
        var finish = Signal();
        var forwarded = new TaskCompletionSource<Core.ProgressInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var progress = new Progress<Core.ProgressInfo>(value => forwarded.TrySetResult(value));
        var pending = new Core.ProgressInfo(2, 1, 3);
        var account = _storage.CreateAccount(accountDelegate: new Delegate
        {
            OnProgress = reporter => reporter!.Report(pending),
            OnRefresh = async (_, ct) => { started.TrySetResult(); await finish.Task.WaitAsync(ct); }
        });
        var refresh = account.RefreshAllAsync(progress, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(pending, await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(pending, CombinedRefreshProgress.Shared.Snapshot);
            Assert.False(CombinedRefreshProgress.Shared.IsComplete);
        }
        finally
        {
            if (cancel) cancellation.Cancel();
            else finish.TrySetResult();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
            else await refresh;
        }
        Assert.True(CombinedRefreshProgress.Shared.IsComplete);
        Assert.Equal(new Core.ProgressInfo(0, 3, 3), CombinedRefreshProgress.Shared.Snapshot);
    }

    [Fact]
    public async Task AsyncDisposalCancelsRefreshAndKeepsStorageAliveUntilItsCleanupFinishes()
    {
        var started = Signal();
        var cancelled = Signal();
        var finish = Signal();
        var account = _storage.CreateAccount(accountDelegate: new Delegate
        {
            OnRefresh = async (owner, ct) =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException)
                {
                    cancelled.SetResult();
                    await finish.Task;
                    Assert.Empty(await owner.Database.FetchArticlesAsync("feed"));
                    throw;
                }
            }
        });
        var refresh = account.RefreshAllAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposal = account.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(disposal.IsCompleted);
        finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => account.Database.FetchArticlesAsync("feed"));
    }

    [Fact]
    public async Task LifetimeCancellationReachesSubscriptionMutations()
    {
        var started = Signal();
        var account = _storage.CreateAccount(accountDelegate: new Delegate
        {
            OnCreateFolder = async (_, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        });
        var mutation = account.CreateFolderAsync("Folder");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await account.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.Empty(account.Folders);
    }

    [Fact]
    public async Task StatusSyncAndQueuedMutationsCannotRaceAnActiveRefresh()
    {
        var started = Signal();
        int statusCalls = 0, mutationCalls = 0;
        var account = _storage.CreateAccount(accountDelegate: new Delegate
        {
            OnRefresh = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); },
            OnSend = (_, _) => { statusCalls++; return Task.CompletedTask; },
            OnCreateFolder = (_, _) => { mutationCalls++; return Task.CompletedTask; }
        });
        var refresh = account.RefreshAllAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var send = account.SendArticleStatusAsync();
        var mutation = account.CreateFolderAsync("Folder");
        Assert.False(send.IsCompleted);
        Assert.False(mutation.IsCompleted);
        await account.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        Assert.Equal(0, statusCalls);
        Assert.Equal(0, mutationCalls);
    }

    [Fact]
    public async Task DisposalWaitsForStatusWritesAndCancelsQueuedMarks()
    {
        var account = _storage.CreateAccount();
        await account.StatusMutationGate.WaitAsync();
        var marking = account.MarkAsync(["article"], ArticleStatus.Key.Read, true);
        var disposal = account.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => marking);
            Assert.False(disposal.IsCompleted);
        }
        finally { account.StatusMutationGate.Release(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SynchronousDisposalRejectsActiveWorkRatherThanClosingItsDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        var started = Signal();
        var account = _storage.CreateAccount(accountDelegate: new Delegate
        {
            OnRefresh = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        });
        var refresh = account.RefreshAllAsync(ct: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(account.Dispose);
        Assert.Empty(await account.Database.FetchArticlesAsync("feed"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        account.Dispose();
    }

    [Fact]
    public async Task DisposalIsIdempotentAndRejectsNewOperations()
    {
        var account = _storage.CreateAccount();
        var first = account.DisposeAsync().AsTask();
        Assert.Same(first, account.DisposeAsync().AsTask());
        await first;
        account.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => account.RefreshAllAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => account.CreateFolderAsync("Folder"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => account.MarkAsync(["article"], ArticleStatus.Key.Read, true));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => account.SendArticleStatusAsync());
        Assert.Throws<ObjectDisposedException>(() => account.SyncDatabase);
    }

    [Fact]
    public async Task SyncDatabaseIsCreatedOnceForConcurrentConsumers()
    {
        var account = _storage.CreateAccount(accountDelegate: new Delegate());
        var databases = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => account.SyncDatabase)));
        Assert.All(databases, database => Assert.Same(databases[0], database));
    }

    [Fact]
    public async Task DelegateShutdownFinishesBeforeAccountStorageCloses()
    {
        var started = Signal();
        var finish = Signal();
        var accountDelegate = new Delegate();
        var account = _storage.CreateAccount(accountDelegate: accountDelegate);
        accountDelegate.OnDisposeAsync = async () =>
        {
            started.TrySetResult();
            await finish.Task;
            Assert.Empty(await account.Database.FetchArticlesAsync("feed"));
        };
        var disposal = account.DisposeAsync().AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(disposal.IsCompleted); }
        finally { finish.TrySetResult(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => account.Database.FetchArticlesAsync("feed"));
    }

    [Fact]
    public async Task SynchronousDisposalDoesNotCloseStorageWhileDelegateWorkIsPending()
    {
        var accountDelegate = new Delegate { CanDisposeSynchronously = false };
        var account = _storage.CreateAccount(accountDelegate: accountDelegate);
        Assert.Throws<InvalidOperationException>(account.Dispose);
        Assert.Empty(await account.Database.FetchArticlesAsync("feed"));
        accountDelegate.CanDisposeSynchronously = true;
        account.Dispose();
        Assert.Equal(1, accountDelegate.DisposeCount);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Delegate : IAccountDelegate
    {
        public AccountType Type => AccountType.OnMyMac;
        public Func<Account, CancellationToken, Task>? OnRefresh { get; init; }
        public Action<IProgress<Core.ProgressInfo>?>? OnProgress { get; init; }
        public Func<Account, CancellationToken, Task>? OnSend { get; init; }
        public Func<Account, CancellationToken, Task>? OnCreateFolder { get; init; }
        public Func<Task>? OnDisposeAsync { get; set; }
        public bool CanDisposeSynchronously { get; set; } = true;
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
        public async ValueTask DisposeAsync()
        {
            if (OnDisposeAsync is not null) await OnDisposeAsync();
            Dispose();
        }
        public Task RefreshAllAsync(Account account, IProgress<Core.ProgressInfo>? progress, CancellationToken ct)
        {
            OnProgress?.Invoke(progress);
            return OnRefresh?.Invoke(account, ct) ?? Task.CompletedTask;
        }
        public Task SendArticleStatusAsync(Account account, CancellationToken ct = default)
            => OnSend?.Invoke(account, ct) ?? Task.CompletedTask;
        public Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
            => Task.FromException<Feed?>(new NotSupportedException());
        public async Task<Folder> CreateFolderAsync(Account account, string name, CancellationToken ct = default)
        {
            if (OnCreateFolder is not null) await OnCreateFolder(account, ct);
            return account.AddFolder(name);
        }
    }

    public void Dispose() => _storage.Dispose();
}
