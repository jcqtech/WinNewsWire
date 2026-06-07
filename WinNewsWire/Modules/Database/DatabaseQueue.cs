using Microsoft.Data.Sqlite;

namespace WinNewsWire.Database;

/// <summary>
/// Port of <c>DatabaseQueue</c>. Serializes all writes behind a dedicated thread so
/// callers can fire-and-forget while still preserving order. Reads run on the calling
/// thread using a shared-cache connection (SQLite allows concurrent readers).
/// </summary>
public sealed class DatabaseQueue : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _readConn;

    public DatabaseQueue(string filePath)
    {
        // Shared-cache is not available in Microsoft.Data.Sqlite 9.x; plain file + serial writes is fine.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            Cache = SqliteCacheMode.Default,
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using (var pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }
        return c;
    }

    public async Task RunAsync(Func<SqliteConnection, Task> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var c = Open();
            await work(c).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<T> RunAsync<T>(Func<SqliteConnection, Task<T>> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var c = Open();
            return await work(c).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public T Run<T>(Func<SqliteConnection, T> work)
    {
        _gate.Wait();
        try
        {
            using var c = Open();
            return work(c);
        }
        finally { _gate.Release(); }
    }

    public void Run(Action<SqliteConnection> work)
    {
        _gate.Wait();
        try { using var c = Open(); work(c); }
        finally { _gate.Release(); }
    }

    public void Dispose() { _readConn?.Dispose(); _gate.Dispose(); }
}
