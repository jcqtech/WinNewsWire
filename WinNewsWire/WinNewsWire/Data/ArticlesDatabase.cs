using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace WinNewsWire.Data;

/// <summary>
/// Manages the Articles database (DB.sqlite3) — articles, statuses, authors,
/// authorsLookup, full-text search (FTS4), indexes, and triggers.
/// One instance per account.
/// </summary>
public sealed class ArticlesDatabase : IDisposable
{
    private readonly string _accountId;

    public ArticlesDatabase(string accountId)
    {
        _accountId = accountId;
        InitializeSchema();
    }

    public void Dispose()
    {
    }

    // ── Schema ──────────────────────────────────────────────────────────

    private void InitializeSchema()
    {
        using var conn = OpenConnection();

        // Core tables
        ExecuteNonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS articles (
                articleID       TEXT NOT NULL PRIMARY KEY,
                feedID          TEXT NOT NULL,
                uniqueID        TEXT NOT NULL,
                title           TEXT,
                contentHTML     TEXT,
                contentText     TEXT,
                markdown        TEXT,
                url             TEXT,
                externalURL     TEXT,
                summary         TEXT,
                imageURL        TEXT,
                bannerImageURL  TEXT,
                datePublished   DATE,
                dateModified    DATE,
                searchRowID     INTEGER
            );");

        ExecuteNonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS statuses (
                articleID    TEXT NOT NULL PRIMARY KEY,
                read         BOOL NOT NULL DEFAULT 0,
                starred      BOOL NOT NULL DEFAULT 0,
                dateArrived  DATE NOT NULL DEFAULT 0
            );");

        ExecuteNonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS authors (
                authorID     TEXT NOT NULL PRIMARY KEY,
                name         TEXT,
                url          TEXT,
                avatarURL    TEXT,
                emailAddress TEXT
            );");

        ExecuteNonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS authorsLookup (
                authorID  TEXT NOT NULL,
                articleID TEXT NOT NULL,
                PRIMARY KEY(authorID, articleID)
            );");

        // Indexes
        ExecuteNonQuery(conn, @"
            CREATE INDEX IF NOT EXISTS articles_feedID_datePublished_articleID
                ON articles (feedID, datePublished, articleID);");

        ExecuteNonQuery(conn, @"
            CREATE INDEX IF NOT EXISTS statuses_starred_index
                ON statuses (starred);");

        ExecuteNonQuery(conn, @"
            CREATE INDEX IF NOT EXISTS articles_searchRowID
                ON articles(searchRowID);");

        // FTS4 virtual table
        ExecuteNonQuery(conn, @"
            CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts4(title, body);");

        // Trigger: auto-delete search row when article is deleted
        ExecuteNonQuery(conn, @"
            CREATE TRIGGER IF NOT EXISTS articles_after_delete_trigger_delete_search_text
                AFTER DELETE ON articles
                BEGIN
                    DELETE FROM search WHERE rowid = OLD.searchRowID;
                END;");

        // Drop legacy tables/indexes from older schema versions
        ExecuteNonQuery(conn, "DROP TABLE IF EXISTS tags;");
        ExecuteNonQuery(conn, "DROP INDEX IF EXISTS tags_tagName_index;");
        ExecuteNonQuery(conn, "DROP INDEX IF EXISTS articles_feedID_index;");
        ExecuteNonQuery(conn, "DROP INDEX IF EXISTS statuses_read_index;");
        ExecuteNonQuery(conn, "DROP TABLE IF EXISTS attachments;");
        ExecuteNonQuery(conn, "DROP TABLE IF EXISTS attachmentsLookup;");

        // Migrations: add columns if they don't exist (for imported older DBs)
        MigrateAddColumnIfMissing(conn, "articles", "searchRowID", "INTEGER");
        MigrateAddColumnIfMissing(conn, "articles", "markdown", "TEXT");
    }

    // ── Articles ────────────────────────────────────────────────────────

    public void InsertOrReplaceArticle(ArticleRow article)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO articles
                (articleID, feedID, uniqueID, title, contentHTML, contentText,
                 markdown, url, externalURL, summary, imageURL, bannerImageURL,
                 datePublished, dateModified, searchRowID)
            VALUES
                ($articleID, $feedID, $uniqueID, $title, $contentHTML, $contentText,
                 $markdown, $url, $externalURL, $summary, $imageURL, $bannerImageURL,
                 $datePublished, $dateModified, $searchRowID);";

        cmd.Parameters.AddWithValue("$articleID", article.ArticleID);
        cmd.Parameters.AddWithValue("$feedID", article.FeedID);
        cmd.Parameters.AddWithValue("$uniqueID", article.UniqueID);
        cmd.Parameters.AddWithValue("$title", (object?)article.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contentHTML", (object?)article.ContentHTML ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$contentText", (object?)article.ContentText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$markdown", (object?)article.Markdown ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)article.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$externalURL", (object?)article.ExternalURL ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$summary", (object?)article.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$imageURL", (object?)article.ImageURL ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bannerImageURL", (object?)article.BannerImageURL ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$datePublished",
            article.DatePublished.HasValue ? DateConversion.ToUnixTimestamp(article.DatePublished.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$dateModified",
            article.DateModified.HasValue ? DateConversion.ToUnixTimestamp(article.DateModified.Value) : DBNull.Value);
        cmd.Parameters.AddWithValue("$searchRowID", (object?)article.SearchRowID ?? DBNull.Value);

        cmd.ExecuteNonQuery();
    }

    public void InsertOrReplaceArticles(IEnumerable<ArticleRow> articles)
    {
        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();

        foreach (var article in articles)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO articles
                    (articleID, feedID, uniqueID, title, contentHTML, contentText,
                     markdown, url, externalURL, summary, imageURL, bannerImageURL,
                     datePublished, dateModified, searchRowID)
                VALUES
                    ($articleID, $feedID, $uniqueID, $title, $contentHTML, $contentText,
                     $markdown, $url, $externalURL, $summary, $imageURL, $bannerImageURL,
                     $datePublished, $dateModified, $searchRowID);";

            cmd.Parameters.AddWithValue("$articleID", article.ArticleID);
            cmd.Parameters.AddWithValue("$feedID", article.FeedID);
            cmd.Parameters.AddWithValue("$uniqueID", article.UniqueID);
            cmd.Parameters.AddWithValue("$title", (object?)article.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$contentHTML", (object?)article.ContentHTML ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$contentText", (object?)article.ContentText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$markdown", (object?)article.Markdown ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$url", (object?)article.Url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$externalURL", (object?)article.ExternalURL ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$summary", (object?)article.Summary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$imageURL", (object?)article.ImageURL ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bannerImageURL", (object?)article.BannerImageURL ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$datePublished",
                article.DatePublished.HasValue ? DateConversion.ToUnixTimestamp(article.DatePublished.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("$dateModified",
                article.DateModified.HasValue ? DateConversion.ToUnixTimestamp(article.DateModified.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("$searchRowID", (object?)article.SearchRowID ?? DBNull.Value);

            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public ArticleRow? GetArticle(string articleId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM articles WHERE articleID = $articleID;";
        cmd.Parameters.AddWithValue("$articleID", articleId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadArticleRow(reader) : null;
    }

    public List<ArticleRow> GetArticlesByFeed(string feedId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM articles
            WHERE feedID = $feedID
            ORDER BY datePublished DESC;";
        cmd.Parameters.AddWithValue("$feedID", feedId);

        return ReadArticleRows(cmd);
    }

    public List<ArticleRow> GetArticlesByIds(IEnumerable<string> articleIds)
    {
        using var conn = OpenConnection();
        var ids = new List<string>(articleIds);
        if (ids.Count == 0) return new List<ArticleRow>();

        using var cmd = conn.CreateCommand();
        var placeholders = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            var param = $"$id{i}";
            placeholders.Add(param);
            cmd.Parameters.AddWithValue(param, ids[i]);
        }
        cmd.CommandText = $"SELECT * FROM articles WHERE articleID IN ({string.Join(",", placeholders)});";

        return ReadArticleRows(cmd);
    }

    public void DeleteArticles(IEnumerable<string> articleIds)
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
        cmd.CommandText = $"DELETE FROM articles WHERE articleID IN ({string.Join(",", placeholders)});";
        cmd.ExecuteNonQuery();
    }

    public void DeleteArticlesNotInFeeds(IEnumerable<string> activeFeedIds)
    {
        using var conn = OpenConnection();
        var feedIds = new List<string>(activeFeedIds);
        if (feedIds.Count == 0)
        {
            ExecuteNonQuery(conn, "DELETE FROM articles;");
            return;
        }

        using var cmd = conn.CreateCommand();
        var placeholders = new List<string>();
        for (int i = 0; i < feedIds.Count; i++)
        {
            var param = $"$fid{i}";
            placeholders.Add(param);
            cmd.Parameters.AddWithValue(param, feedIds[i]);
        }
        cmd.CommandText = $"DELETE FROM articles WHERE feedID NOT IN ({string.Join(",", placeholders)});";
        cmd.ExecuteNonQuery();
    }

    // ── Statuses ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a status row if one doesn't already exist (INSERT OR IGNORE).
    /// </summary>
    public void EnsureStatusExists(string articleId, DateTimeOffset dateArrived)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO statuses (articleID, read, starred, dateArrived)
            VALUES ($articleID, 0, 0, $dateArrived);";
        cmd.Parameters.AddWithValue("$articleID", articleId);
        cmd.Parameters.AddWithValue("$dateArrived", DateConversion.ToUnixTimestamp(dateArrived));
        cmd.ExecuteNonQuery();
    }

    public void EnsureStatusesExist(IEnumerable<(string ArticleId, DateTimeOffset DateArrived)> items)
    {
        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();

        foreach (var (articleId, dateArrived) in items)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO statuses (articleID, read, starred, dateArrived)
                VALUES ($articleID, 0, 0, $dateArrived);";
            cmd.Parameters.AddWithValue("$articleID", articleId);
            cmd.Parameters.AddWithValue("$dateArrived", DateConversion.ToUnixTimestamp(dateArrived));
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public StatusRow? GetStatus(string articleId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM statuses WHERE articleID = $articleID;";
        cmd.Parameters.AddWithValue("$articleID", articleId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadStatusRow(reader) : null;
    }

    public Dictionary<string, StatusRow> GetStatuses(IEnumerable<string> articleIds)
    {
        using var conn = OpenConnection();
        var ids = new List<string>(articleIds);
        var result = new Dictionary<string, StatusRow>();
        if (ids.Count == 0) return result;

        using var cmd = conn.CreateCommand();
        var placeholders = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            var param = $"$id{i}";
            placeholders.Add(param);
            cmd.Parameters.AddWithValue(param, ids[i]);
        }
        cmd.CommandText = $"SELECT * FROM statuses WHERE articleID IN ({string.Join(",", placeholders)});";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = ReadStatusRow(reader);
            result[row.ArticleID] = row;
        }
        return result;
    }

    /// <summary>
    /// Updates a status column (read or starred) for the given article IDs.
    /// </summary>
    public void UpdateStatusFlag(IEnumerable<string> articleIds, string key, bool value)
    {
        if (key != "read" && key != "starred")
            throw new ArgumentException("Key must be 'read' or 'starred'.", nameof(key));

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
        // key is validated above, safe to interpolate
        cmd.CommandText = $"UPDATE statuses SET {key} = $val WHERE articleID IN ({string.Join(",", placeholders)});";
        cmd.Parameters.AddWithValue("$val", value ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetStarredArticleIds()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT articleID FROM statuses WHERE starred = 1;";

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// Deletes orphan statuses older than ~6 months that are not starred
    /// and have no corresponding article.
    /// </summary>
    public void CleanupOldStatuses()
    {
        const double staleIntervalSeconds = 183.0 * 24 * 60 * 60; // ~6 months
        double cutoff = DateConversion.ToUnixTimestamp(DateTimeOffset.UtcNow) - staleIntervalSeconds;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM statuses
            WHERE starred = 0
              AND dateArrived < $cutoff
              AND articleID NOT IN (SELECT articleID FROM articles);";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.ExecuteNonQuery();
    }

    // ── Authors ─────────────────────────────────────────────────────────

    public void InsertOrReplaceAuthor(AuthorRow author)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO authors (authorID, name, url, avatarURL, emailAddress)
            VALUES ($authorID, $name, $url, $avatarURL, $emailAddress);";
        cmd.Parameters.AddWithValue("$authorID", author.AuthorID);
        cmd.Parameters.AddWithValue("$name", (object?)author.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$url", (object?)author.Url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$avatarURL", (object?)author.AvatarURL ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$emailAddress", (object?)author.EmailAddress ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void InsertOrReplaceAuthorLookup(string authorId, string articleId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO authorsLookup (authorID, articleID)
            VALUES ($authorID, $articleID);";
        cmd.Parameters.AddWithValue("$authorID", authorId);
        cmd.Parameters.AddWithValue("$articleID", articleId);
        cmd.ExecuteNonQuery();
    }

    public List<AuthorRow> GetAuthorsForArticle(string articleId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT a.* FROM authors a
            INNER JOIN authorsLookup al ON a.authorID = al.authorID
            WHERE al.articleID = $articleID;";
        cmd.Parameters.AddWithValue("$articleID", articleId);

        var result = new List<AuthorRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AuthorRow
            {
                AuthorID = reader.GetString(reader.GetOrdinal("authorID")),
                Name = reader.IsDBNull(reader.GetOrdinal("name")) ? null : reader.GetString(reader.GetOrdinal("name")),
                Url = reader.IsDBNull(reader.GetOrdinal("url")) ? null : reader.GetString(reader.GetOrdinal("url")),
                AvatarURL = reader.IsDBNull(reader.GetOrdinal("avatarURL")) ? null : reader.GetString(reader.GetOrdinal("avatarURL")),
                EmailAddress = reader.IsDBNull(reader.GetOrdinal("emailAddress")) ? null : reader.GetString(reader.GetOrdinal("emailAddress"))
            });
        }
        return result;
    }

    public void DeleteAuthorsForArticles(IEnumerable<string> articleIds)
    {
        using var conn = OpenConnection();
        var ids = new List<string>(articleIds);
        if (ids.Count == 0) return;

        using var transaction = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        var placeholders = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            var param = $"$id{i}";
            placeholders.Add(param);
            cmd.Parameters.AddWithValue(param, ids[i]);
        }
        cmd.CommandText = $"DELETE FROM authorsLookup WHERE articleID IN ({string.Join(",", placeholders)});";
        cmd.ExecuteNonQuery();

        // Clean up orphaned authors (authors not referenced by any lookup)
        ExecuteNonQuery(conn, @"
            DELETE FROM authors
            WHERE authorID NOT IN (SELECT DISTINCT authorID FROM authorsLookup);");

        transaction.Commit();
    }

    // ── Full-Text Search ────────────────────────────────────────────────

    /// <summary>
    /// Indexes an article for full-text search. Inserts into the FTS4 search table
    /// and updates articles.searchRowID.
    /// </summary>
    public void IndexArticleForSearch(string articleId, string? title, string? body)
    {
        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = "INSERT INTO search (title, body) VALUES ($title, $body);";
        insertCmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        insertCmd.ExecuteNonQuery();

        using var rowIdCmd = conn.CreateCommand();
        rowIdCmd.CommandText = "SELECT last_insert_rowid();";
        long searchRowId = (long)rowIdCmd.ExecuteScalar()!;

        using var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = "UPDATE articles SET searchRowID = $searchRowID WHERE articleID = $articleID;";
        updateCmd.Parameters.AddWithValue("$searchRowID", searchRowId);
        updateCmd.Parameters.AddWithValue("$articleID", articleId);
        updateCmd.ExecuteNonQuery();

        transaction.Commit();
    }

    /// <summary>
    /// Updates an existing search index entry for an article.
    /// </summary>
    public void UpdateSearchIndex(long searchRowId, string? title, string? body)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE search SET title = $title, body = $body WHERE rowid = $rowid;";
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rowid", searchRowId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns article IDs matching a full-text search query.
    /// </summary>
    public List<string> SearchArticles(string query)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT a.articleID FROM articles a
            WHERE a.searchRowID IN (
                SELECT rowid FROM search WHERE search MATCH $query
            );";
        cmd.Parameters.AddWithValue("$query", query);

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>
    /// Returns article IDs that have not yet been indexed (searchRowID IS NULL).
    /// </summary>
    public List<string> GetUnindexedArticleIds()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT articleID FROM articles WHERE searchRowID IS NULL;";

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    // ── Retention & Cleanup ─────────────────────────────────────────────

    /// <summary>
    /// Deletes articles older than approximately 90 days (for sync accounts).
    /// </summary>
    public void DeleteOldArticlesForSyncAccount()
    {
        const double ninetyDaysInSeconds = 90.0 * 24 * 60 * 60;
        double cutoff = DateConversion.ToUnixTimestamp(DateTimeOffset.UtcNow) - ninetyDaysInSeconds;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM articles WHERE datePublished < $cutoff
            AND articleID NOT IN (SELECT articleID FROM statuses WHERE starred = 1);";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs VACUUM to reclaim space.
    /// </summary>
    public void Vacuum()
    {
        using var conn = OpenConnection();
        ExecuteNonQuery(conn, "VACUUM;");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        return DatabaseManager.CreateArticlesConnection(_accountId);
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void MigrateAddColumnIfMissing(SqliteConnection conn, string table, string column, string type)
    {
        // Check if column exists via PRAGMA table_info
        using var infoCmd = conn.CreateCommand();
        infoCmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = infoCmd.ExecuteReader();

        bool found = false;
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
        }
        reader.Close();

        if (!found)
        {
            ExecuteNonQuery(conn, $"ALTER TABLE {table} ADD COLUMN {column} {type};");
        }
    }

    private static List<ArticleRow> ReadArticleRows(SqliteCommand cmd)
    {
        var result = new List<ArticleRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(ReadArticleRow(reader));
        return result;
    }

    private static ArticleRow ReadArticleRow(SqliteDataReader reader)
    {
        return new ArticleRow
        {
            ArticleID = reader.GetString(reader.GetOrdinal("articleID")),
            FeedID = reader.GetString(reader.GetOrdinal("feedID")),
            UniqueID = reader.GetString(reader.GetOrdinal("uniqueID")),
            Title = GetNullableString(reader, "title"),
            ContentHTML = GetNullableString(reader, "contentHTML"),
            ContentText = GetNullableString(reader, "contentText"),
            Markdown = GetNullableString(reader, "markdown"),
            Url = GetNullableString(reader, "url"),
            ExternalURL = GetNullableString(reader, "externalURL"),
            Summary = GetNullableString(reader, "summary"),
            ImageURL = GetNullableString(reader, "imageURL"),
            BannerImageURL = GetNullableString(reader, "bannerImageURL"),
            DatePublished = GetNullableDouble(reader, "datePublished") is double dp
                ? DateConversion.FromUnixTimestamp(dp)
                : null,
            DateModified = GetNullableDouble(reader, "dateModified") is double dm
                ? DateConversion.FromUnixTimestamp(dm)
                : null,
            SearchRowID = GetNullableLong(reader, "searchRowID")
        };
    }

    private static StatusRow ReadStatusRow(SqliteDataReader reader)
    {
        return new StatusRow
        {
            ArticleID = reader.GetString(reader.GetOrdinal("articleID")),
            Read = reader.GetInt32(reader.GetOrdinal("read")) != 0,
            Starred = reader.GetInt32(reader.GetOrdinal("starred")) != 0,
            DateArrived = DateConversion.FromUnixTimestamp(reader.GetDouble(reader.GetOrdinal("dateArrived")))
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

    private static long? GetNullableLong(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
}

// ── Row Types ──────────────────────────────────────────────────────────

/// <summary>
/// Represents a row in the articles table.
/// </summary>
public class ArticleRow
{
    public string ArticleID { get; set; } = string.Empty;
    public string FeedID { get; set; } = string.Empty;
    public string UniqueID { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? ContentHTML { get; set; }
    public string? ContentText { get; set; }
    public string? Markdown { get; set; }
    public string? Url { get; set; }
    public string? ExternalURL { get; set; }
    public string? Summary { get; set; }
    public string? ImageURL { get; set; }
    public string? BannerImageURL { get; set; }
    public DateTimeOffset? DatePublished { get; set; }
    public DateTimeOffset? DateModified { get; set; }
    public long? SearchRowID { get; set; }
}

/// <summary>
/// Represents a row in the statuses table.
/// </summary>
public class StatusRow
{
    public string ArticleID { get; set; } = string.Empty;
    public bool Read { get; set; }
    public bool Starred { get; set; }
    public DateTimeOffset DateArrived { get; set; }
}

/// <summary>
/// Represents a row in the authors table.
/// </summary>
public class AuthorRow
{
    public string AuthorID { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string? AvatarURL { get; set; }
    public string? EmailAddress { get; set; }
}
