using Microsoft.Data.Sqlite;
using Xunit;

namespace WinNewsWire.Database.Tests;

public sealed class DatabaseQueueTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "database-queue-" + Guid.NewGuid().ToString("N"));
    public DatabaseQueueTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FileDatabasesUseNativeDeleteJournalingAndNormalSynchronization()
    {
        string path = Path.Combine(_directory, "new.db");
        using var queue = new DatabaseQueue(path);
        queue.Run(connection =>
        {
            Assert.Equal("delete", connection.Scalar<string>("PRAGMA journal_mode;"));
            Assert.Equal(1L, connection.Scalar<long>("PRAGMA synchronous;"));
            Assert.Equal(1L, connection.Scalar<long>("PRAGMA foreign_keys;"));
            connection.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1);");
        });
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public void InMemoryDatabasesKeepTheirRequiredMemoryJournal()
    {
        using var queue = new DatabaseQueue(":memory:");
        queue.Run(connection =>
        {
            Assert.Equal("memory", connection.Scalar<string>("PRAGMA journal_mode;"));
            Assert.Equal(1L, connection.Scalar<long>("PRAGMA foreign_keys;"));
            connection.Execute("CREATE TABLE t (value TEXT); INSERT INTO t VALUES ('retained');");
        });
        Assert.Equal("retained", queue.Run(connection => connection.Scalar<string>("SELECT value FROM t;")));
    }

    [Fact]
    public void RecoveredWalDataSurvivesTheJournalTransition()
    {
        string path = CreateWalSnapshot();
        Assert.True(new FileInfo(path + "-wal").Length > new FileInfo(path).Length);
        using (var queue = new DatabaseQueue(path))
        {
            queue.Run(connection =>
            {
                Assert.Equal("delete", connection.Scalar<string>("PRAGMA journal_mode;"));
                AssertWalContents(connection);
                connection.Execute("INSERT INTO t (id, text) VALUES (3, 'new write');");
            });
            Assert.False(File.Exists(path + "-wal"));
            Assert.False(File.Exists(path + "-shm"));
        }
        using var reopened = new DatabaseQueue(path);
        Assert.Equal(3L, reopened.Run(connection => connection.Scalar<long>("SELECT COUNT(*) FROM t;")));
        Assert.Equal("new write", reopened.Run(connection => connection.Scalar<string>("SELECT text FROM t WHERE id=3;")));
    }

    [Fact]
    public void ABlockedJournalTransitionReportsFailureWithoutDiscardingWalData()
    {
        string path = CreateWalSnapshot();
        using var candidate = Open(path);
        using (var blocker = Open(path))
        using (var transaction = blocker.BeginTransaction(deferred: true))
        {
            Assert.Equal(2L, blocker.Scalar<long>("SELECT COUNT(*) FROM t;"));
            var error = Assert.Throws<SqliteException>(() => candidate.SetUpDatabase());
            Assert.Equal(5, error.SqliteErrorCode);
            Assert.Equal("wal", candidate.Scalar<string>("PRAGMA journal_mode;"));
            Assert.True(File.Exists(path + "-wal"));
            AssertWalContents(blocker);
        }
        candidate.SetUpDatabase();
        Assert.Equal("delete", candidate.Scalar<string>("PRAGMA journal_mode;"));
        AssertWalContents(candidate);
    }

    [Fact]
    public async Task AsyncWorkUsesOneConnectionAndSerializesTransactionsAcrossAwaits()
    {
        using var queue = new DatabaseQueue(Path.Combine(_directory, "serial.db"));
        SqliteConnection? first = null;
        int active = 0, maximumActive = 0;
        queue.Run(connection => connection.Execute("CREATE TABLE counter (value INTEGER); INSERT INTO counter VALUES (0);"));
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => queue.RunAsync(async connection =>
        {
            first ??= connection;
            Assert.Same(first, connection);
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            try
            {
                using var transaction = connection.BeginTransaction();
                long value = connection.Scalar<long>("SELECT value FROM counter;");
                await Task.Yield();
                connection.Execute("UPDATE counter SET value=$value;", ("$value", value + 1));
                transaction.Commit();
            }
            finally { Interlocked.Decrement(ref active); }
        })));
        Assert.Equal(1, maximumActive);
        Assert.Equal(40L, queue.Run(connection => connection.Scalar<long>("SELECT value FROM counter;")));
    }

    [Fact]
    public async Task FailedTransactionsRollbackAndLeaveTheQueueUsable()
    {
        using var queue = new DatabaseQueue(Path.Combine(_directory, "rollback.db"));
        queue.Run(connection => connection.Execute("CREATE TABLE t (value INTEGER);"));
        var failure = new InvalidOperationException("Fixture transaction failure.");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => queue.RunAsync(async connection =>
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute("INSERT INTO t VALUES (1);");
            await Task.Yield();
            throw failure;
        })));
        Assert.Equal(0L, queue.Run(connection => connection.Scalar<long>("SELECT COUNT(*) FROM t;")));
        queue.Run(connection => connection.Execute("INSERT INTO t VALUES (2);"));
        Assert.Equal(2L, queue.Run(connection => connection.Scalar<long>("SELECT value FROM t;")));
    }

    [Fact]
    public async Task DisposalWaitsForRunningWorkAndRejectsLaterCalls()
    {
        using var queue = new DatabaseQueue(":memory:");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = queue.RunAsync(async connection =>
        {
            started.TrySetResult();
            await release.Task;
            Assert.Equal(42L, connection.Scalar<long>("SELECT 42;"));
        });
        Task? closing = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            closing = Task.Run(queue.Dispose);
            Assert.False(closing.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Throws<ObjectDisposedException>(() => queue.Run(_ => { }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queue.RunAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public void FailedConnectionSetupClosesItsHandleAndCanBeRetried()
    {
        string path = Path.Combine(_directory, "invalid.db");
        byte[] original = "not a SQLite database"u8.ToArray();
        File.WriteAllBytes(path, original);
        using var queue = new DatabaseQueue(path);
        bool called = false;
        Assert.Throws<SqliteException>(() => queue.Run(_ => { called = true; }));
        Assert.False(called);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var data = new byte[original.Length];
            exclusive.ReadExactly(data);
            Assert.Equal(original, data);
            exclusive.SetLength(0);
        }
        queue.Run(connection => connection.Execute("CREATE TABLE t (value INTEGER); INSERT INTO t VALUES (42);"));
        Assert.Equal(42L, queue.Run(connection => connection.Scalar<long>("SELECT value FROM t;")));
    }

    private string CreateWalSnapshot()
    {
        string sourcePath = Path.Combine(_directory, "source.db");
        string snapshotPath = Path.Combine(_directory, "snapshot.db");
        using var source = Open(sourcePath);
        Assert.Equal("wal", source.Scalar<string>("PRAGMA journal_mode=WAL;"));
        source.Execute("""
            PRAGMA wal_autocheckpoint=0;
            PRAGMA user_version=17;
            CREATE TABLE t (id INTEGER PRIMARY KEY, text TEXT, bytes BLOB);
            CREATE VIRTUAL TABLE search USING fts4(body);
            INSERT INTO search VALUES ('retained needle');
            INSERT INTO t VALUES (2, 'body', zeroblob(16000));
            """);
        source.Execute("INSERT INTO t VALUES (1, $text, $bytes);",
            ("$text", "Caf\u00e9 \U0001f680"), ("$bytes", new byte[] { 0, 1, 2, 255 }));
        File.Copy(sourcePath, snapshotPath);
        File.Copy(sourcePath + "-wal", snapshotPath + "-wal");
        return snapshotPath;
    }

    private static void AssertWalContents(SqliteConnection connection)
    {
        Assert.Equal(2L, connection.Scalar<long>("SELECT COUNT(*) FROM t;"));
        Assert.Equal("Caf\u00e9 \U0001f680", connection.Scalar<string>("SELECT text FROM t WHERE id=1;"));
        using var command = connection.Cmd("SELECT bytes FROM t WHERE id=1;");
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, Assert.IsType<byte[]>(command.ExecuteScalar()));
        Assert.Equal(16000L, connection.Scalar<long>("SELECT length(bytes) FROM t WHERE id=2;"));
        Assert.Equal(1L, connection.Scalar<long>("SELECT COUNT(*) FROM search WHERE search MATCH 'needle';"));
        Assert.Equal(17L, connection.Scalar<long>("PRAGMA user_version;"));
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 1
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
