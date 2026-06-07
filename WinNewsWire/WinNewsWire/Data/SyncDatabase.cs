using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace WinNewsWire.Data;

/// <summary>
/// Manages the Sync database (Sync.sqlite3) — tracks pending status changes
/// that need to be pushed to a sync service.
/// One instance per cloud-synced account.
/// </summary>
public sealed class SyncDatabase : IDisposable
{
    private readonly string _accountId;

    public SyncDatabase(string accountId)
    {
        _accountId = accountId;
        InitializeSchema();
    }

    public void Dispose()
    {
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        ExecuteNonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS syncStatus (
                articleID TEXT NOT NULL,
                key       TEXT NOT NULL,
                flag      BOOL NOT NULL DEFAULT 0,
                selected  BOOL NOT NULL DEFAULT 0,
                PRIMARY KEY (articleID, key)
            );");
    }

    // ── CRUD ────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts or replaces a sync status entry.
    /// </summary>
    public void InsertOrReplace(string articleId, string key, bool flag)
    {
        ValidateKey(key);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO syncStatus (articleID, key, flag, selected)
            VALUES ($articleID, $key, $flag, 0);";
        cmd.Parameters.AddWithValue("$articleID", articleId);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$flag", flag ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts or replaces multiple sync status entries in a transaction.
    /// </summary>
    public void InsertOrReplace(IEnumerable<SyncStatusRow> rows)
    {
        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();

        foreach (var row in rows)
        {
            ValidateKey(row.Key);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO syncStatus (articleID, key, flag, selected)
                VALUES ($articleID, $key, $flag, 0);";
            cmd.Parameters.AddWithValue("$articleID", row.ArticleID);
            cmd.Parameters.AddWithValue("$key", row.Key);
            cmd.Parameters.AddWithValue("$flag", row.Flag ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    // ── Sync Processing Workflow ────────────────────────────────────────

    /// <summary>
    /// Step 1: Marks all rows as selected for processing.
    /// </summary>
    public void SelectForProcessing()
    {
        using var conn = OpenConnection();
        ExecuteNonQuery(conn, "UPDATE syncStatus SET selected = 1;");
    }

    /// <summary>
    /// Step 2: Returns all selected sync statuses, optionally limited.
    /// </summary>
    public List<SyncStatusRow> GetSelected(int? limit = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = limit.HasValue
            ? $"SELECT * FROM syncStatus WHERE selected = 1 LIMIT {limit.Value};"
            : "SELECT * FROM syncStatus WHERE selected = 1;";

        var result = new List<SyncStatusRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SyncStatusRow
            {
                ArticleID = reader.GetString(reader.GetOrdinal("articleID")),
                Key = reader.GetString(reader.GetOrdinal("key")),
                Flag = reader.GetInt32(reader.GetOrdinal("flag")) != 0,
                Selected = reader.GetInt32(reader.GetOrdinal("selected")) != 0,
            });
        }
        return result;
    }

    /// <summary>
    /// Step 3a (success): Deletes selected rows for the given article IDs.
    /// </summary>
    public void DeleteSelectedForArticles(IEnumerable<string> articleIds)
    {
        using var conn = OpenConnection();
        var ids = new List<string>(articleIds);
        if (ids.Count == 0) return;

        using var cmd = conn.CreateCommand();
        var placeholders = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            var param = $"$id{i}";
            placeholders.Add(param);
            cmd.Parameters.AddWithValue(param, ids[i]);
        }
        cmd.CommandText = $"DELETE FROM syncStatus WHERE selected = 1 AND articleID IN ({string.Join(",", placeholders)});";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Step 3b (failure): Resets selected flag for the given article IDs.
    /// </summary>
    public void ResetSelectedForArticles(IEnumerable<string> articleIds)
    {
        using var conn = OpenConnection();
        var ids = new List<string>(articleIds);
        if (ids.Count == 0) return;

        using var cmd = conn.CreateCommand();
        var placeholders = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            var param = $"$id{i}";
            placeholders.Add(param);
            cmd.Parameters.AddWithValue(param, ids[i]);
        }
        cmd.CommandText = $"UPDATE syncStatus SET selected = 0 WHERE articleID IN ({string.Join(",", placeholders)});";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns all pending (unselected) sync statuses.
    /// </summary>
    public List<SyncStatusRow> GetAll()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM syncStatus;";

        var result = new List<SyncStatusRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SyncStatusRow
            {
                ArticleID = reader.GetString(reader.GetOrdinal("articleID")),
                Key = reader.GetString(reader.GetOrdinal("key")),
                Flag = reader.GetInt32(reader.GetOrdinal("flag")) != 0,
                Selected = reader.GetInt32(reader.GetOrdinal("selected")) != 0,
            });
        }
        return result;
    }

    /// <summary>
    /// Resets all selected flags (e.g., on startup to handle interrupted syncs).
    /// </summary>
    public void ResetAllSelected()
    {
        using var conn = OpenConnection();
        ExecuteNonQuery(conn, "UPDATE syncStatus SET selected = 0;");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        return DatabaseManager.CreateSyncConnection(_accountId);
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ValidateKey(string key)
    {
        if (key != "read" && key != "starred" && key != "deleted" && key != "new")
            throw new ArgumentException(
                $"Invalid sync status key '{key}'. Must be one of: read, starred, deleted, new.",
                nameof(key));
    }
}

// ── Row Type ────────────────────────────────────────────────────────────

/// <summary>
/// Represents a row in the syncStatus table.
/// </summary>
public class SyncStatusRow
{
    public string ArticleID { get; set; } = string.Empty;

    /// <summary>
    /// One of: "read", "starred", "deleted", "new".
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public bool Flag { get; set; }
    public bool Selected { get; set; }
}
