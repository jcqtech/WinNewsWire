using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace WinNewsWire.Data;

/// <summary>
/// Manages database file paths and connection creation for all four database types.
/// </summary>
public static class DatabaseManager
{
    private static readonly string _appDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNewsWire");

    /// <summary>
    /// Returns the account data folder, creating it if it doesn't exist.
    /// </summary>
    public static string GetAccountFolder(string accountId)
    {
        string folder = Path.Combine(_appDataFolder, "Accounts", accountId);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Returns the global app data folder, creating it if it doesn't exist.
    /// </summary>
    public static string GetAppDataFolder()
    {
        Directory.CreateDirectory(_appDataFolder);
        return _appDataFolder;
    }

    /// <summary>
    /// Creates a connection to the Articles database (DB.sqlite3).
    /// PRAGMA: synchronous=1
    /// </summary>
    public static SqliteConnection CreateArticlesConnection(string accountId)
    {
        string path = Path.Combine(GetAccountFolder(accountId), "DB.sqlite3");
        var conn = CreateConnection(path);
        ExecutePragmas(conn, "PRAGMA synchronous = 1;");
        return conn;
    }

    /// <summary>
    /// Creates a connection to the Feed Settings database (FeedSettings.db).
    /// PRAGMA: synchronous=1, journal_mode=WAL
    /// </summary>
    public static SqliteConnection CreateFeedSettingsConnection(string accountId)
    {
        string path = Path.Combine(GetAccountFolder(accountId), "FeedSettings.db");
        var conn = CreateConnection(path);
        ExecutePragmas(conn, "PRAGMA synchronous = 1;", "PRAGMA journal_mode = WAL;");
        return conn;
    }

    /// <summary>
    /// Creates a connection to the Sync database (Sync.sqlite3).
    /// PRAGMA: synchronous=1
    /// </summary>
    public static SqliteConnection CreateSyncConnection(string accountId)
    {
        string path = Path.Combine(GetAccountFolder(accountId), "Sync.sqlite3");
        var conn = CreateConnection(path);
        ExecutePragmas(conn, "PRAGMA synchronous = 1;");
        return conn;
    }

    /// <summary>
    /// Creates a connection to the global Error Log database (Errors.db).
    /// PRAGMA: synchronous=1, journal_mode=WAL
    /// </summary>
    public static SqliteConnection CreateErrorLogConnection()
    {
        string path = Path.Combine(GetAppDataFolder(), "Errors.db");
        var conn = CreateConnection(path);
        ExecutePragmas(conn, "PRAGMA synchronous = 1;", "PRAGMA journal_mode = WAL;");
        return conn;
    }

    private static SqliteConnection CreateConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        // Set busy timeout for concurrent access
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();

        return connection;
    }

    private static void ExecutePragmas(SqliteConnection connection, params string[] pragmas)
    {
        foreach (var pragma in pragmas)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = pragma;
            cmd.ExecuteNonQuery();
        }
    }
}
