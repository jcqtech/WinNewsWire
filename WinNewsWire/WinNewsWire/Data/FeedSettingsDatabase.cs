using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WinNewsWire.Data;

/// <summary>
/// Manages the Feed Settings database (FeedSettings.db).
/// One instance per account.
/// Date columns use Apple's reference date epoch (2001-01-01).
/// </summary>
public sealed class FeedSettingsDatabase : IDisposable
{
    private readonly string _accountId;

    public FeedSettingsDatabase(string accountId)
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
            CREATE TABLE IF NOT EXISTS feedSettings (
                feedURL                         TEXT PRIMARY KEY,
                feedID                          TEXT NOT NULL DEFAULT '',
                homePageURL                     TEXT,
                iconURL                         TEXT,
                faviconURL                      TEXT,
                editedName                      TEXT,
                contentHash                     TEXT,
                newArticleNotificationsEnabled  INTEGER NOT NULL DEFAULT 0,
                readerViewAlwaysEnabled         INTEGER NOT NULL DEFAULT 0,
                authors                         TEXT,
                conditionalGetInfoLastModified  TEXT,
                conditionalGetInfoEtag          TEXT,
                conditionalGetInfoDate          REAL,
                cacheControlInfoDateCreated     REAL,
                cacheControlInfoMaxAge          REAL,
                externalID                      TEXT,
                folderRelationship              TEXT,
                lastCheckDate                   REAL
            );");
    }

    // ── CRUD ────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts or replaces a complete feed settings row.
    /// </summary>
    public void InsertOrReplace(FeedSettingsRow row)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO feedSettings
                (feedURL, feedID, homePageURL, iconURL, faviconURL, editedName,
                 contentHash, newArticleNotificationsEnabled, readerViewAlwaysEnabled,
                 authors, conditionalGetInfoLastModified, conditionalGetInfoEtag,
                 conditionalGetInfoDate, cacheControlInfoDateCreated, cacheControlInfoMaxAge,
                 externalID, folderRelationship, lastCheckDate)
            VALUES
                ($feedURL, $feedID, $homePageURL, $iconURL, $faviconURL, $editedName,
                 $contentHash, $notifications, $readerView,
                 $authors, $condLastModified, $condEtag,
                 $condDate, $cacheCreated, $cacheMaxAge,
                 $externalID, $folderRelationship, $lastCheckDate);";

        cmd.Parameters.AddWithValue("$feedURL", row.FeedURL);
        cmd.Parameters.AddWithValue("$feedID", row.FeedID);
        cmd.Parameters.AddWithValue("$homePageURL", (object?)row.HomePageURL ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$iconURL", (object?)row.IconURL ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$faviconURL", (object?)row.FaviconURL ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$editedName", (object?)row.EditedName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contentHash", (object?)row.ContentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$notifications", row.NewArticleNotificationsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$readerView", row.ReaderViewAlwaysEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$authors", (object?)row.AuthorsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$condLastModified", (object?)row.ConditionalGetInfoLastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$condEtag", (object?)row.ConditionalGetInfoEtag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$condDate",
            row.ConditionalGetInfoDate.HasValue ? DateConversion.ToAppleReferenceDate(row.ConditionalGetInfoDate.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$cacheCreated",
            row.CacheControlInfoDateCreated.HasValue ? DateConversion.ToAppleReferenceDate(row.CacheControlInfoDateCreated.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$cacheMaxAge", (object?)row.CacheControlInfoMaxAge ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$externalID", (object?)row.ExternalID ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folderRelationship", (object?)row.FolderRelationshipJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lastCheckDate",
            row.LastCheckDate.HasValue ? DateConversion.ToAppleReferenceDate(row.LastCheckDate.Value) : DBNull.Value);

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Ensures a feed row exists with minimal data (INSERT OR IGNORE).
    /// </summary>
    public void EnsureFeedExists(string feedUrl, string feedId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO feedSettings (feedURL, feedID)
            VALUES ($feedURL, $feedID);";
        cmd.Parameters.AddWithValue("$feedURL", feedUrl);
        cmd.Parameters.AddWithValue("$feedID", feedId);
        cmd.ExecuteNonQuery();
    }

    public FeedSettingsRow? Get(string feedUrl)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM feedSettings WHERE feedURL = $feedURL;";
        cmd.Parameters.AddWithValue("$feedURL", feedUrl);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    public List<FeedSettingsRow> GetAll()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM feedSettings;";

        var result = new List<FeedSettingsRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(ReadRow(reader));
        return result;
    }

    public void Delete(string feedUrl)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM feedSettings WHERE feedURL = $feedURL;";
        cmd.Parameters.AddWithValue("$feedURL", feedUrl);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates conditional GET info for a feed.
    /// </summary>
    public void UpdateConditionalGetInfo(string feedUrl, string? lastModified, string? etag, DateTimeOffset? date)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE feedSettings SET
                conditionalGetInfoLastModified = $lastModified,
                conditionalGetInfoEtag = $etag,
                conditionalGetInfoDate = $date
            WHERE feedURL = $feedURL;";
        cmd.Parameters.AddWithValue("$feedURL", feedUrl);
        cmd.Parameters.AddWithValue("$lastModified", (object?)lastModified ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$etag", (object?)etag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$date",
            date.HasValue ? DateConversion.ToAppleReferenceDate(date.Value) : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates cache control info for a feed.
    /// </summary>
    public void UpdateCacheControlInfo(string feedUrl, DateTimeOffset? dateCreated, double? maxAge)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE feedSettings SET
                cacheControlInfoDateCreated = $dateCreated,
                cacheControlInfoMaxAge = $maxAge
            WHERE feedURL = $feedURL;";
        cmd.Parameters.AddWithValue("$feedURL", feedUrl);
        cmd.Parameters.AddWithValue("$dateCreated",
            dateCreated.HasValue ? DateConversion.ToAppleReferenceDate(dateCreated.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$maxAge", (object?)maxAge ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates the last check date for a feed.
    /// </summary>
    public void UpdateLastCheckDate(string feedUrl, DateTimeOffset date)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE feedSettings SET lastCheckDate = $date WHERE feedURL = $feedURL;";
        cmd.Parameters.AddWithValue("$feedURL", feedUrl);
        cmd.Parameters.AddWithValue("$date", DateConversion.ToAppleReferenceDate(date));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Updates the content hash for a feed.
    /// </summary>
    public void UpdateContentHash(string feedUrl, string? contentHash)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE feedSettings SET contentHash = $hash WHERE feedURL = $feedURL;";
        cmd.Parameters.AddWithValue("$feedURL", feedUrl);
        cmd.Parameters.AddWithValue("$hash", (object?)contentHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        return DatabaseManager.CreateFeedSettingsConnection(_accountId);
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static FeedSettingsRow ReadRow(SqliteDataReader reader)
    {
        return new FeedSettingsRow
        {
            FeedURL = reader.GetString(reader.GetOrdinal("feedURL")),
            FeedID = reader.GetString(reader.GetOrdinal("feedID")),
            HomePageURL = GetNullableString(reader, "homePageURL"),
            IconURL = GetNullableString(reader, "iconURL"),
            FaviconURL = GetNullableString(reader, "faviconURL"),
            EditedName = GetNullableString(reader, "editedName"),
            ContentHash = GetNullableString(reader, "contentHash"),
            NewArticleNotificationsEnabled = reader.GetInt32(reader.GetOrdinal("newArticleNotificationsEnabled")) != 0,
            ReaderViewAlwaysEnabled = reader.GetInt32(reader.GetOrdinal("readerViewAlwaysEnabled")) != 0,
            AuthorsJson = GetNullableString(reader, "authors"),
            ConditionalGetInfoLastModified = GetNullableString(reader, "conditionalGetInfoLastModified"),
            ConditionalGetInfoEtag = GetNullableString(reader, "conditionalGetInfoEtag"),
            ConditionalGetInfoDate = GetNullableDouble(reader, "conditionalGetInfoDate") is double condDate
                ? DateConversion.FromAppleReferenceDate(condDate)
                : null,
            CacheControlInfoDateCreated = GetNullableDouble(reader, "cacheControlInfoDateCreated") is double cacheDate
                ? DateConversion.FromAppleReferenceDate(cacheDate)
                : null,
            CacheControlInfoMaxAge = GetNullableDouble(reader, "cacheControlInfoMaxAge"),
            ExternalID = GetNullableString(reader, "externalID"),
            FolderRelationshipJson = GetNullableString(reader, "folderRelationship"),
            LastCheckDate = GetNullableDouble(reader, "lastCheckDate") is double lastCheck
                ? DateConversion.FromAppleReferenceDate(lastCheck)
                : null,
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static double? GetNullableDouble(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }
}

// ── Row Type ────────────────────────────────────────────────────────────

/// <summary>
/// Represents a row in the feedSettings table.
/// Date fields are exposed as DateTimeOffset (converted from Apple epoch on read).
/// </summary>
public class FeedSettingsRow
{
    public string FeedURL { get; set; } = string.Empty;
    public string FeedID { get; set; } = string.Empty;
    public string? HomePageURL { get; set; }
    public string? IconURL { get; set; }
    public string? FaviconURL { get; set; }
    public string? EditedName { get; set; }
    public string? ContentHash { get; set; }
    public bool NewArticleNotificationsEnabled { get; set; }
    public bool ReaderViewAlwaysEnabled { get; set; }

    /// <summary>
    /// Raw JSON string for authors (JSON array of Author objects).
    /// </summary>
    public string? AuthorsJson { get; set; }

    public string? ConditionalGetInfoLastModified { get; set; }
    public string? ConditionalGetInfoEtag { get; set; }
    public DateTimeOffset? ConditionalGetInfoDate { get; set; }
    public DateTimeOffset? CacheControlInfoDateCreated { get; set; }
    public double? CacheControlInfoMaxAge { get; set; }
    public string? ExternalID { get; set; }

    /// <summary>
    /// Raw JSON string for folder relationships (JSON object: { "FolderName": "externalId" }).
    /// </summary>
    public string? FolderRelationshipJson { get; set; }

    public DateTimeOffset? LastCheckDate { get; set; }

    // ── JSON convenience methods ────────────────────────────────────────

    /// <summary>
    /// Deserializes the authors JSON into a list of AuthorRow objects.
    /// </summary>
    public List<AuthorRow>? GetAuthors()
    {
        if (string.IsNullOrEmpty(AuthorsJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<AuthorRow>>(AuthorsJson, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes a list of AuthorRow objects to JSON and stores in AuthorsJson.
    /// </summary>
    public void SetAuthors(List<AuthorRow>? authors)
    {
        AuthorsJson = authors != null
            ? JsonSerializer.Serialize(authors, _jsonOptions)
            : null;
    }

    /// <summary>
    /// Deserializes the folder relationship JSON into a dictionary.
    /// </summary>
    public Dictionary<string, string>? GetFolderRelationship()
    {
        if (string.IsNullOrEmpty(FolderRelationshipJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(FolderRelationshipJson, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes a folder relationship dictionary to JSON.
    /// </summary>
    public void SetFolderRelationship(Dictionary<string, string>? relationship)
    {
        FolderRelationshipJson = relationship != null
            ? JsonSerializer.Serialize(relationship, _jsonOptions)
            : null;
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
