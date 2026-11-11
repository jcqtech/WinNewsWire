using Microsoft.Data.Sqlite;
using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.SyncDatabase;
using Xunit;
using AccountModel = WinNewsWire.Account.Account;

namespace RemoteAccounts.Tests;

/// <summary>Tests for Pass 9 remote-sync flush-back: MarkAsync queues pending sync statuses
/// into SyncDatabase, and <see cref="RemoteSyncHelpers.FlushPendingAsync"/> drains the queue.</summary>
public class RemoteSyncFlushbackTests : IDisposable
{
    private readonly string _tempDataDir;

    public RemoteSyncFlushbackTests()
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), "WinNewsWire-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDir);
        AppConfig.SetDataDirectoryOverride(_tempDataDir);
    }

    public void Dispose()
    {
        AppConfig.SetDataDirectoryOverride(null);
        // Microsoft.Data.Sqlite pools connections; release file handles before deletion.
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tempDataDir)) Directory.Delete(_tempDataDir, recursive: true); } catch { }
    }

    private AccountModel NewTestAccount(string prefix)
    {
        var id = prefix + "-" + Guid.NewGuid().ToString("N");
        return new AccountModel(id, AccountType.Feedbin, "Test", new FakeSyncDelegate());
    }

    private sealed class FakeSyncDelegate : IAccountDelegate
    {
        public AccountType Type => AccountType.Feedbin;
        public bool SupportsRemoteSync => true;
        public Task<Feed?> CreateFeedAsync(AccountModel a, string u, string? n, Folder? f, CancellationToken c) => Task.FromResult<Feed?>(null);
        public Task RefreshAllAsync(AccountModel a, IProgress<ProgressInfo>? p, CancellationToken c) => Task.CompletedTask;
    }

    [Fact]
    public async Task MarkAsync_EnqueuesRemoteSyncStatus_WhenDelegateSupportsSync()
    {
        var account = NewTestAccount("test-sync");
        try
        {
            await account.Database.CreateStatusesIfNeededAsync(["a1", "a2"], defaultRead: false);
            await account.MarkAsync(new[] { "a1", "a2" }, ArticleStatus.Key.Read, true);
            var pending = await account.SyncDatabase.SelectPendingCountAsync();
            Assert.Equal(2, pending);

            await account.SyncDatabase.ResetAllSelectedAsync();
            var readIDs = await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Read);
            Assert.Equal(new HashSet<string> { "a1", "a2" }, readIDs);
        }
        finally { account.Dispose(); }
    }

    [Fact]
    public async Task FlushPending_InvokesSendGroupedByKeyAndFlag_ThenDeletes()
    {
        var account = NewTestAccount("test-flush");
        try
        {
            await account.Database.CreateStatusesIfNeededAsync(["1", "2", "4", "5"], defaultRead: false);
            await account.Database.CreateStatusesIfNeededAsync(["3"], defaultRead: true);
            await account.MarkAsync(new[] { "1", "2" }, ArticleStatus.Key.Read, true);
            await account.MarkAsync(new[] { "3" }, ArticleStatus.Key.Read, false);
            await account.MarkAsync(new[] { "4", "5" }, ArticleStatus.Key.Starred, true);

            var calls = new List<(SyncStatus.SyncKey key, bool flag, int count)>();
            await RemoteSyncHelpers.FlushPendingAsync(account, (key, flag, ids, c) =>
            {
                calls.Add((key, flag, ids.Count));
                return Task.CompletedTask;
            });

            Assert.Equal(3, calls.Count);
            Assert.Contains((SyncStatus.SyncKey.Read, true, 2), calls);
            Assert.Contains((SyncStatus.SyncKey.Read, false, 1), calls);
            Assert.Contains((SyncStatus.SyncKey.Starred, true, 2), calls);
            Assert.Equal(0, await account.SyncDatabase.SelectPendingCountAsync());
        }
        finally { account.Dispose(); }
    }

    [Fact]
    public async Task FlushPending_FailedGroupIsReportedAndRemainsRetryable()
    {
        var account = NewTestAccount("test-fail");
        try
        {
            await account.Database.CreateStatusesIfNeededAsync(["ok1", "fail1"], defaultRead: false);
            await account.MarkAsync(new[] { "ok1" }, ArticleStatus.Key.Read, true);
            await account.MarkAsync(new[] { "fail1" }, ArticleStatus.Key.Starred, true);

            await Assert.ThrowsAsync<ArticleStatusSyncException>(() => RemoteSyncHelpers.FlushPendingAsync(account, (key, flag, ids, c) =>
            {
                if (key == SyncStatus.SyncKey.Starred) throw new InvalidOperationException("boom");
                return Task.CompletedTask;
            }));

            Assert.Equal(1, await account.SyncDatabase.SelectPendingCountAsync());
            Assert.Single(await account.SyncDatabase.SelectForProcessingAsync());
        }
        finally { account.Dispose(); }
    }

    [Fact]
    public async Task FlushPending_FailedStarIsNotDeletedBySuccessfulReadForSameArticle()
    {
        using var account = NewTestAccount("same-article");
        await account.Database.CreateStatusesIfNeededAsync(["1"], defaultRead: false);
        await account.MarkAsync(["1"], ArticleStatus.Key.Read, true);
        await account.MarkAsync(["1"], ArticleStatus.Key.Starred, true);
        await Assert.ThrowsAsync<ArticleStatusSyncException>(() => RemoteSyncHelpers.FlushPendingAsync(account, (key, flag, ids, ct) =>
            key == SyncStatus.SyncKey.Starred ? Task.FromException(new HttpRequestException("failed")) : Task.CompletedTask));
        Assert.Empty(await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Read));
        Assert.Contains("1", await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Starred));
    }

    [Fact]
    public async Task FailingUnreadGroupStillSendsStarredGroup()
    {
        using var account = NewTestAccount("send-all-groups");
        await account.SyncDatabase.InsertStatusesAsync([
            new("1", SyncStatus.SyncKey.Read, false),
            new("2", SyncStatus.SyncKey.Starred, true),
        ]);
        await Assert.ThrowsAsync<ArticleStatusSyncException>(() => RemoteSyncHelpers.FlushPendingAsync(account, (key, flag, ids, ct) =>
            key == SyncStatus.SyncKey.Read ? Task.FromException(new HttpRequestException("422")) : Task.CompletedTask));
        Assert.Equal(new HashSet<string> { "1" }, await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Read));
        Assert.Empty(await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Starred));
    }
}
