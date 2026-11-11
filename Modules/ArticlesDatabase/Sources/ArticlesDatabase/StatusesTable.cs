using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using WinNewsWire.Articles;
using WinNewsWire.Database;

namespace WinNewsWire.ArticlesDatabase;

internal sealed class StatusesTable
{
    private readonly ConcurrentDictionary<string, ArticleStatus> _cache = new(StringComparer.Ordinal);

    public ArticleStatus Get(string id, bool read, bool starred, DateTime arrived)
        => _cache.GetOrAdd(id, static (key, state) => new ArticleStatus(key, state.read, state.starred, state.arrived),
            (read, starred, arrived));

    public ArticleStatus EnsureStatus(SqliteConnection connection, ArticleStatus initial)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT OR IGNORE INTO statuses (articleID, read, starred, dateArrived) VALUES (@id, @read, @starred, @arrived);";
        insert.Parameters.AddWithValue("@id", initial.ArticleID);
        insert.Parameters.AddWithValue("@read", initial.Read);
        insert.Parameters.AddWithValue("@starred", initial.Starred);
        insert.Parameters.AddWithValue("@arrived", initial.DateArrived.ToUnixTimestamp());
        insert.ExecuteNonQuery();

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT read, starred, dateArrived FROM statuses WHERE articleID = @id;";
        query.Parameters.AddWithValue("@id", initial.ArticleID);
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new InvalidDataException("The article status could not be stored.");
        return Get(initial.ArticleID, row.GetBoolean(0), row.GetBoolean(1),
            row.GetUnixTime(2));
    }

    public HashSet<string> Mark(SqliteConnection connection, IReadOnlyCollection<string> ids, ArticleStatus.Key key, bool value)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var statuses = new List<ArticleStatus>();
        using var transaction = connection.BeginTransaction();
        foreach (var batch in ids.Chunk(500))
        {
            var placeholders = string.Join(",", batch.Select((_, i) => $"@id{i}"));
            using (var query = connection.CreateCommand())
            {
                query.CommandText = $"SELECT articleID, read, starred, dateArrived FROM statuses WHERE articleID IN ({placeholders});";
                for (int i = 0; i < batch.Length; i++) query.Parameters.AddWithValue($"@id{i}", batch[i]);
                using var rows = query.ExecuteReader();
                while (rows.Read())
                {
                    var status = Get(rows.GetString(0), rows.GetBoolean(1), rows.GetBoolean(2),
                        rows.GetUnixTime(3));
                    statuses.Add(status);
                    if (status.BoolStatus(key) != value) changed.Add(status.ArticleID);
                }
            }
            using var update = connection.CreateCommand();
            var column = key == ArticleStatus.Key.Read ? "read" : "starred";
            update.CommandText = $"UPDATE statuses SET {column} = @value WHERE articleID IN ({placeholders});";
            update.Parameters.AddWithValue("@value", value);
            for (int i = 0; i < batch.Length; i++) update.Parameters.AddWithValue($"@id{i}", batch[i]);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        foreach (var status in statuses) status.SetBoolStatus(value, key);
        return changed;
    }

    public void Repair(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.CommandText = @"UPDATE statuses SET read = @read, starred = @starred
            WHERE articleID = @id AND (read != @read OR starred != @starred);";
        var id = update.Parameters.Add("@id", SqliteType.Text);
        var read = update.Parameters.Add("@read", SqliteType.Integer);
        var starred = update.Parameters.Add("@starred", SqliteType.Integer);
        foreach (var status in _cache.Values)
        {
            id.Value = status.ArticleID;
            read.Value = status.Read;
            starred.Value = status.Starred;
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Remove(IEnumerable<string> ids)
    {
        foreach (var id in ids) _cache.TryRemove(id, out _);
    }

    public void EmptyCache() => _cache.Clear();
}
