using WinNewsWire.Database;

namespace WinNewsWire.ErrorLog;

/// <summary>Port of <c>ErrorLogDatabase</c> / <c>ErrorLogTable</c>.</summary>
public sealed class ErrorLogDatabase : IDisposable
{
    private const string CreateStatements = @"
    CREATE TABLE IF NOT EXISTS errors (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        date INTEGER NOT NULL,
        sourceName TEXT NOT NULL,
        sourceID INTEGER NOT NULL,
        operation TEXT NOT NULL,
        fileName TEXT NOT NULL,
        functionName TEXT NOT NULL,
        lineNumber INTEGER NOT NULL,
        errorMessage TEXT NOT NULL
    );
    CREATE INDEX IF NOT EXISTS errors_date_index ON errors (date DESC);
    ";

    public DatabaseQueue Queue { get; }

    public ErrorLogDatabase(string path)
    {
        Queue = new DatabaseQueue(path);
        Queue.Run(c => { using var cmd = c.CreateCommand(); cmd.CommandText = CreateStatements; cmd.ExecuteNonQuery(); });
    }

    public event EventHandler<ErrorLogEntry>? EntryAdded;

    public Task AddAsync(ErrorLogEntry entry) =>
        Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"INSERT INTO errors (date, sourceName, sourceID, operation, fileName, functionName, lineNumber, errorMessage)
                                 VALUES (@d,@sn,@si,@op,@fn,@func,@ln,@msg);";
            cmd.Parameters.AddWithValue("@d", new DateTimeOffset(entry.Date).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("@sn", entry.SourceName);
            cmd.Parameters.AddWithValue("@si", entry.SourceID);
            cmd.Parameters.AddWithValue("@op", entry.Operation);
            cmd.Parameters.AddWithValue("@fn", entry.FileName);
            cmd.Parameters.AddWithValue("@func", entry.FunctionName);
            cmd.Parameters.AddWithValue("@ln", entry.LineNumber);
            cmd.Parameters.AddWithValue("@msg", entry.ErrorMessage);
            cmd.ExecuteNonQuery();
            EntryAdded?.Invoke(this, entry);
            return Task.CompletedTask;
        });

    public Task<List<ErrorLogEntry>> FetchRecentAsync(int limit = 500) =>
        Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT id, date, sourceName, sourceID, operation, fileName, functionName, lineNumber, errorMessage FROM errors ORDER BY date DESC LIMIT {limit};";
            using var r = cmd.ExecuteReader();
            var list = new List<ErrorLogEntry>();
            while (r.Read())
            {
                list.Add(new ErrorLogEntry(
                    r.GetInt64(0),
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)).UtcDateTime,
                    r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetString(8)));
            }
            return Task.FromResult(list);
        });

    public Task ClearAsync() =>
        Queue.RunAsync(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM errors;"; cmd.ExecuteNonQuery(); return Task.CompletedTask; });

    public void Dispose() => Queue.Dispose();
}
