using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace WinNewsWire.Data;

/// <summary>
/// Manages the global Error Log database (Errors.db).
/// Singleton — not per-account.
/// Date column uses Apple's reference date epoch (2001-01-01).
/// </summary>
public sealed class ErrorLogDatabase : IDisposable
{
    private static readonly Lazy<ErrorLogDatabase> _instance = new(() => new ErrorLogDatabase());

    /// <summary>
    /// Maximum number of error entries to retain.
    /// </summary>
    public const int MaxErrors = 200;

    private ErrorLogDatabase()
    {
        InitializeSchema();
        PruneErrors();
    }

    public static ErrorLogDatabase Instance => _instance.Value;

    public void Dispose()
    {
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        ExecuteNonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS errors (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                date         REAL NOT NULL,
                sourceName   TEXT NOT NULL,
                sourceID     INTEGER NOT NULL,
                operation    TEXT NOT NULL DEFAULT '',
                fileName     TEXT NOT NULL DEFAULT '',
                functionName TEXT NOT NULL DEFAULT '',
                lineNumber   INTEGER NOT NULL DEFAULT 0,
                errorMessage TEXT NOT NULL
            );");
    }

    // ── CRUD ────────────────────────────────────────────────────────────

    public void LogError(ErrorRow error)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO errors
                (date, sourceName, sourceID, operation, fileName, functionName, lineNumber, errorMessage)
            VALUES
                ($date, $sourceName, $sourceID, $operation, $fileName, $functionName, $lineNumber, $errorMessage);";

        cmd.Parameters.AddWithValue("$date", DateConversion.ToAppleReferenceDate(error.Date));
        cmd.Parameters.AddWithValue("$sourceName", error.SourceName);
        cmd.Parameters.AddWithValue("$sourceID", error.SourceID);
        cmd.Parameters.AddWithValue("$operation", error.Operation);
        cmd.Parameters.AddWithValue("$fileName", error.FileName);
        cmd.Parameters.AddWithValue("$functionName", error.FunctionName);
        cmd.Parameters.AddWithValue("$lineNumber", error.LineNumber);
        cmd.Parameters.AddWithValue("$errorMessage", error.ErrorMessage);

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Convenience method: logs an error with caller info.
    /// </summary>
    public void LogError(
        string errorMessage,
        string sourceName,
        int sourceId = 0,
        string operation = "",
        [System.Runtime.CompilerServices.CallerFilePath] string fileName = "",
        [System.Runtime.CompilerServices.CallerMemberName] string functionName = "",
        [System.Runtime.CompilerServices.CallerLineNumber] int lineNumber = 0)
    {
        LogError(new ErrorRow
        {
            Date = DateTimeOffset.UtcNow,
            SourceName = sourceName,
            SourceID = sourceId,
            Operation = operation,
            FileName = System.IO.Path.GetFileName(fileName),
            FunctionName = functionName,
            LineNumber = lineNumber,
            ErrorMessage = errorMessage,
        });
    }

    public List<ErrorRow> GetAllErrors()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM errors ORDER BY id DESC;";

        var result = new List<ErrorRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(ReadRow(reader));
        return result;
    }

    public List<ErrorRow> GetRecentErrors(int count)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM errors ORDER BY id DESC LIMIT {count};";

        var result = new List<ErrorRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(ReadRow(reader));
        return result;
    }

    /// <summary>
    /// Prunes the error table to MaxErrors entries, keeping the most recent.
    /// </summary>
    public void PruneErrors()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM errors WHERE id NOT IN (
                SELECT id FROM errors ORDER BY id DESC LIMIT $limit
            );";
        cmd.Parameters.AddWithValue("$limit", MaxErrors);
        cmd.ExecuteNonQuery();
    }

    public void ClearAll()
    {
        using var conn = OpenConnection();
        ExecuteNonQuery(conn, "DELETE FROM errors;");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SqliteConnection OpenConnection()
    {
        return DatabaseManager.CreateErrorLogConnection();
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static ErrorRow ReadRow(SqliteDataReader reader)
    {
        return new ErrorRow
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Date = DateConversion.FromAppleReferenceDate(reader.GetDouble(reader.GetOrdinal("date"))),
            SourceName = reader.GetString(reader.GetOrdinal("sourceName")),
            SourceID = reader.GetInt32(reader.GetOrdinal("sourceID")),
            Operation = reader.GetString(reader.GetOrdinal("operation")),
            FileName = reader.GetString(reader.GetOrdinal("fileName")),
            FunctionName = reader.GetString(reader.GetOrdinal("functionName")),
            LineNumber = reader.GetInt32(reader.GetOrdinal("lineNumber")),
            ErrorMessage = reader.GetString(reader.GetOrdinal("errorMessage")),
        };
    }
}

// ── Row Type ────────────────────────────────────────────────────────────

/// <summary>
/// Represents a row in the errors table.
/// </summary>
public class ErrorRow
{
    public long Id { get; set; }
    public DateTimeOffset Date { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public int SourceID { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FunctionName { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}
