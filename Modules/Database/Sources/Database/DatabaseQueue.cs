using Microsoft.Data.Sqlite;

namespace WinNewsWire.Database;

/// <summary>
/// Port of <c>DatabaseQueue</c>. Owns one connection and serializes reads and writes.
/// Async work runs off the caller's thread; in-memory databases live until disposal.
/// </summary>
public sealed class DatabaseQueue : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public DatabaseQueue(string filePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            Cache = SqliteCacheMode.Default,
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    private SqliteConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is not null) return _connection;
            var connection = new SqliteConnection(_connectionString);
            try
            {
                connection.Open();
                connection.SetUpDatabase();
                return _connection = connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }

    public async Task RunAsync(Func<SqliteConnection, Task> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => work(Connection)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => work(Connection)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public T Run<T>(Func<SqliteConnection, T> work)
    {
        _gate.Wait();
        try
        {
            return work(Connection);
        }
        finally { _gate.Release(); }
    }

    public void Run(Action<SqliteConnection> work)
    {
        _gate.Wait();
        try { work(Connection); }
        finally { _gate.Release(); }
    }

    public Task VacuumAsync(CancellationToken ct = default)
        => RunAsync(connection =>
        {
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
            return Task.CompletedTask;
        });

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
        // Queued callers still need the gate to report ObjectDisposedException safely.
        finally { _gate.Release(); }
    }
}
