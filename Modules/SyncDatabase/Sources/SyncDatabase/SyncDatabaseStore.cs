using WinNewsWire.Database;

namespace WinNewsWire.SyncDatabase;

/// <summary>Port of <c>SyncDatabase</c>. Persistent queue of pending sync actions.</summary>
public sealed class SyncDatabaseStore : IDisposable
{
    private const string CreateStatements = @"
    CREATE TABLE IF NOT EXISTS syncStatus (articleID TEXT NOT NULL, key TEXT NOT NULL, flag INTEGER NOT NULL DEFAULT 0, selected INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (articleID, key));
    ";

    public DatabaseQueue Queue { get; }

    public SyncDatabaseStore(string databasePath)
    {
        Queue = new DatabaseQueue(databasePath);
        try { Queue.Run(c => { using var cmd = c.CreateCommand(); cmd.CommandText = CreateStatements; cmd.ExecuteNonQuery(); }); }
        catch { Queue.Dispose(); throw; }
    }

    public Task InsertStatusesAsync(IEnumerable<SyncStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Count == 0) return Task.CompletedTask;
        return Queue.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var s in list)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO syncStatus (articleID, key, flag, selected) VALUES (@a, @k, @f, @s);";
                cmd.Parameters.AddWithValue("@a", s.ArticleID);
                cmd.Parameters.AddWithValue("@k", s.Key.ToString().ToLowerInvariant());
                cmd.Parameters.AddWithValue("@f", s.Flag ? 1 : 0);
                cmd.Parameters.AddWithValue("@s", s.Selected ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return Task.CompletedTask;
        });
    }

    public Task<HashSet<SyncStatus>> SelectForProcessingAsync(int? limit = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit ?? 0);
        return Queue.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();
            var result = new HashSet<SyncStatus>();
            using (var sel = c.CreateCommand())
            {
                sel.CommandText = "SELECT articleID, key, flag FROM syncStatus" + (limit is not null ? " LIMIT @limit" : "") + ";";
                if (limit is not null) sel.Parameters.AddWithValue("@limit", limit.Value);
                using var r = sel.ExecuteReader();
                while (r.Read())
                {
                    var k = Enum.Parse<SyncStatus.SyncKey>(r.GetString(1), ignoreCase: true);
                    result.Add(new SyncStatus(r.GetString(0), k, r.GetInt32(2) != 0, Selected: true));
                }
            }
            if (result.Count > 0)
            {
                foreach (var batch in result.Chunk(500))
                {
                    using var upd = c.CreateCommand();
                    var ph = string.Join(",", batch.Select((_, i) => $"(@a{i}, @k{i})"));
                    upd.CommandText = $"UPDATE syncStatus SET selected = 1 WHERE (articleID, key) IN (VALUES {ph});";
                    int i = 0;
                    foreach (var s in batch)
                    {
                        upd.Parameters.AddWithValue($"@a{i}", s.ArticleID);
                        upd.Parameters.AddWithValue($"@k{i}", s.Key.ToString().ToLowerInvariant());
                        i++;
                    }
                    upd.ExecuteNonQuery();
                }
            }
            tx.Commit();
            return Task.FromResult(result);
        });
    }

    public Task<int> SelectPendingCountAsync()
        => Queue.RunAsync(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM syncStatus;"; return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar())); });

    public Task<HashSet<string>> SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey key)
        => Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT articleID FROM syncStatus WHERE key = @k;";
            cmd.Parameters.AddWithValue("@k", key.ToString().ToLowerInvariant());
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>();
            while (r.Read()) set.Add(r.GetString(0));
            return Task.FromResult(set);
        });

    public Task ResetAllSelectedAsync()
        => Queue.RunAsync(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE syncStatus SET selected = 0;"; cmd.ExecuteNonQuery(); return Task.CompletedTask; });

    public Task DeleteSelectedAsync(IEnumerable<string> articleIDs, SyncStatus.SyncKey? key = null)
        => UpdateSelectedAsync(articleIDs, key, delete: true);

    public Task ResetSelectedAsync(IEnumerable<string> articleIDs, SyncStatus.SyncKey? key = null)
        => UpdateSelectedAsync(articleIDs, key, delete: false);

    private Task UpdateSelectedAsync(IEnumerable<string> articleIDs, SyncStatus.SyncKey? key, bool delete)
    {
        var ids = articleIDs.Distinct().ToList();
        if (ids.Count == 0) return Task.CompletedTask;
        return Queue.RunAsync(c =>
        {
            using var transaction = c.BeginTransaction();
            foreach (var batch in ids.Chunk(500))
            {
                var ph = string.Join(",", batch.Select((_, i) => $"@i{i}"));
                using var cmd = c.CreateCommand();
                cmd.CommandText = delete
                    ? $"DELETE FROM syncStatus WHERE selected = 1 AND articleID IN ({ph})"
                    : $"UPDATE syncStatus SET selected = 0 WHERE articleID IN ({ph})";
                if (key is not null)
                {
                    cmd.CommandText += " AND key = @key";
                    cmd.Parameters.AddWithValue("@key", key.Value.ToString().ToLowerInvariant());
                }
                for (int i = 0; i < batch.Length; i++) cmd.Parameters.AddWithValue($"@i{i}", batch[i]);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
            return Task.CompletedTask;
        });
    }

    public Task VacuumAsync(CancellationToken ct = default) => Queue.VacuumAsync(ct);
    public void Dispose() => Queue.Dispose();
}
