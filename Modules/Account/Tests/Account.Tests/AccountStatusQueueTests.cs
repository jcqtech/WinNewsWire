using Microsoft.Data.Sqlite;
using WinNewsWire.Articles;
using WinNewsWire.Feedbin;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class AccountStatusQueueTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Fact]
    public async Task UnchangedAndUnknownStatusesAreNotQueuedOrNotified()
    {
        var account = _storage.CreateAccount(accountDelegate: new FeedbinAccountDelegate());
        await account.Database.CreateStatusesIfNeededAsync(["same"]);
        int notifications = 0;
        account.StatusesChanged += (_, _) => notifications++;
        await account.MarkAsync(["same"], ArticleStatus.Key.Read, true);
        await account.MarkAsync(["unknown"], ArticleStatus.Key.Read, false);
        Assert.Equal(0, await account.SyncDatabase.SelectPendingCountAsync());
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task MixedBatchesQueueAndNotifyOnlyChangedArticleIds()
    {
        var account = _storage.CreateAccount(accountDelegate: new FeedbinAccountDelegate());
        await account.Database.CreateStatusesIfNeededAsync(["same", "changed"]);
        await account.Database.MarkAsync(["changed"], ArticleStatus.Key.Read, false);
        StatusesChangedEventArgs? notification = null;
        account.StatusesChanged += (_, args) => notification = args;
        await account.MarkAsync(["same", "changed", "unknown"], ArticleStatus.Key.Read, true);
        Assert.Equal("changed", Assert.Single(await account.SyncDatabase.SelectForProcessingAsync()).ArticleID);
        Assert.NotNull(notification);
        Assert.Equal("changed", Assert.Single(notification.ArticleIDs));
    }

    [Fact]
    public async Task QueueWriteFailureRollsBackLocalStateSoTheSameActionCanRetry()
    {
        var account = _storage.CreateAccount(accountDelegate: new FeedbinAccountDelegate());
        await account.Database.CreateStatusesIfNeededAsync(["article"], defaultRead: false);
        account.SyncDatabase.Queue.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_queue BEFORE INSERT ON syncStatus BEGIN SELECT RAISE(ABORT, 'test queue failure'); END;";
            command.ExecuteNonQuery();
        });
        await Assert.ThrowsAsync<SqliteException>(() => account.MarkAsync(["article"], ArticleStatus.Key.Read, true));
        Assert.Contains("article", await account.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Read, false));
        Assert.Equal(0, await account.SyncDatabase.SelectPendingCountAsync());
        account.SyncDatabase.Queue.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_queue;";
            command.ExecuteNonQuery();
        });
        await account.MarkAsync(["article"], ArticleStatus.Key.Read, true);
        Assert.Contains("article", await account.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Read, true));
        Assert.Equal(1, await account.SyncDatabase.SelectPendingCountAsync());
    }

    [Fact]
    public async Task RollbackFailureIsReportedTogetherWithTheQueueFailure()
    {
        var account = _storage.CreateAccount(accountDelegate: new FeedbinAccountDelegate());
        await account.Database.CreateStatusesIfNeededAsync(["article"], defaultRead: false);
        account.SyncDatabase.Queue.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_queue BEFORE INSERT ON syncStatus BEGIN SELECT RAISE(ABORT, 'test queue failure'); END;";
            command.ExecuteNonQuery();
        });
        account.Database.Queue.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_rollback BEFORE UPDATE OF read ON statuses WHEN NEW.read = 0 BEGIN SELECT RAISE(ABORT, 'test rollback failure'); END;";
            command.ExecuteNonQuery();
        });
        var error = await Assert.ThrowsAsync<AggregateException>(() => account.MarkAsync(["article"], ArticleStatus.Key.Read, true));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.All(error.InnerExceptions, failure => Assert.IsType<SqliteException>(failure));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    public async Task MoreThanOneHundredPendingChangesFlushInTheBackgroundWithoutBlockingTheMark(int count)
    {
        var sender = new SendingDelegate();
        var account = _storage.CreateAccount(accountDelegate: sender);
        var ids = Enumerable.Range(0, count).Select(id => id.ToString()).ToArray();
        await account.Database.CreateStatusesIfNeededAsync(ids, defaultRead: false);
        int queuedEvents = 0;
        account.ArticleStatusesQueued += (_, _) => queuedEvents++;
        await account.MarkAsync(ids, ArticleStatus.Key.Read, true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, queuedEvents);
        if (count > 100)
        {
            await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sender.Completed.Task.IsCompleted);
            sender.Release.SetResult();
            await sender.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, await account.SyncDatabase.SelectPendingCountAsync());
        }
        else
        {
            Assert.False(sender.Started.Task.IsCompleted);
            Assert.Equal(100, await account.SyncDatabase.SelectPendingCountAsync());
        }
        await account.DisposeAsync();
    }

    private sealed class SendingDelegate : IAccountDelegate
    {
        public AccountType Type => AccountType.Feedbin;
        public bool SupportsRemoteSync => true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
            => Task.FromException<Feed?>(new NotSupportedException());
        public Task RefreshAllAsync(Account account, IProgress<Core.ProgressInfo>? progress, CancellationToken ct)
            => Task.CompletedTask;
        public async Task SendArticleStatusAsync(Account account, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await RemoteSyncHelpers.FlushPendingAsync(account, async (_, _, _, token) => await Release.Task.WaitAsync(token), ct);
            Completed.TrySetResult();
        }
    }

    public void Dispose() => _storage.Dispose();
}
