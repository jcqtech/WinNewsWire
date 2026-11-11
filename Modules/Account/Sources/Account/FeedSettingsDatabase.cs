using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinNewsWire.Articles;
using WinNewsWire.Database;
using WinNewsWire.Web;

namespace WinNewsWire.Account;

public sealed class FeedSettingsDatabase : IDisposable
{
    public enum Column
    {
        FeedID, HomePageUrl, IconUrl, FaviconUrl, EditedName, ContentHash,
        NewArticleNotificationsEnabled, ReaderViewAlwaysEnabled, Authors,
        ConditionalGetInfoLastModified, ConditionalGetInfoEtag, ConditionalGetInfoDate,
        CacheControlInfoDateCreated, CacheControlInfoMaxAge, ExternalID, FolderRelationship,
        LastCheckDate, LastResponseCode,
    }

    public sealed record Row(string FeedID, string? HomePageUrl = null, string? IconUrl = null, string? FaviconUrl = null,
        string? EditedName = null, string? ContentHash = null, bool NewArticleNotificationsEnabled = false,
        bool ReaderViewAlwaysEnabled = false, IReadOnlySet<Author>? Authors = null, ConditionalGetInfo? ConditionalGetInfo = null,
        DateTime? ConditionalGetInfoDate = null, CacheControlInfo? CacheControlInfo = null, string? ExternalID = null,
        Dictionary<string, string>? FolderRelationship = null, DateTime? LastCheckDate = null, int? LastResponseCode = null);

    internal static readonly DateTime ReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly DatabaseQueue _queue;
    public string DatabasePath { get; }

    public FeedSettingsDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        _queue = new(databasePath);
        try
        {
            _queue.Run(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS feedSettings (
                        feedURL TEXT PRIMARY KEY, feedID TEXT NOT NULL DEFAULT '', homePageURL TEXT, iconURL TEXT, faviconURL TEXT,
                        editedName TEXT, contentHash TEXT, newArticleNotificationsEnabled INTEGER NOT NULL DEFAULT 0,
                        readerViewAlwaysEnabled INTEGER NOT NULL DEFAULT 0, authors TEXT, conditionalGetInfoLastModified TEXT,
                        conditionalGetInfoEtag TEXT, conditionalGetInfoDate REAL, cacheControlInfoDateCreated REAL,
                        cacheControlInfoMaxAge REAL, externalID TEXT, folderRelationship TEXT, lastCheckDate REAL, lastResponseCode INTEGER);
                    """;
                command.ExecuteNonQuery();
                command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('feedSettings') WHERE name = 'lastResponseCode';";
                if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                {
                    command.CommandText = "ALTER TABLE feedSettings ADD COLUMN lastResponseCode INTEGER;";
                    command.ExecuteNonQuery();
                }
            });
        }
        catch { _queue.Dispose(); throw; }
    }

    public bool IsEmpty => _queue.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM feedSettings LIMIT 1;";
        return command.ExecuteScalar() is null;
    });

    public void EnsureFeedExists(string feedUrl, string feedID) => _queue.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO feedSettings (feedURL, feedID) VALUES (@url, @id);";
        command.Parameters.AddWithValue("@url", feedUrl);
        command.Parameters.AddWithValue("@id", feedID);
        command.ExecuteNonQuery();
    });

    public void InsertRow(string feedUrl, IReadOnlyDictionary<Column, object?> values)
        => _queue.Run(connection => InsertRow(connection, feedUrl, values));

    public void InsertRows(IEnumerable<(string Url, Row Settings)> rows) => _queue.Run(connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var (url, settings) in rows) InsertRow(connection, url, Values(settings));
        transaction.Commit();
    });

    private static void InsertRow(SqliteConnection connection, string feedUrl, IReadOnlyDictionary<Column, object?> values)
    {
        using var command = connection.CreateCommand();
        var names = values.Keys.Select(Name).ToArray();
        command.CommandText = $"INSERT OR REPLACE INTO feedSettings ({string.Join(",", names.Prepend("feedURL"))}) VALUES ({string.Join(",", names.Select(n => "@" + n).Prepend("@url"))});";
        command.Parameters.AddWithValue("@url", feedUrl);
        foreach (var (column, value) in values) command.Parameters.AddWithValue("@" + Name(column), value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public Dictionary<string, Row> AllRows() => _queue.Run(connection =>
    {
        var result = new Dictionary<string, Row>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM feedSettings;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? Text(Column column) => reader.IsDBNull(reader.GetOrdinal(Name(column))) ? null : reader.GetString(reader.GetOrdinal(Name(column)));
            double? Number(Column column) => reader.IsDBNull(reader.GetOrdinal(Name(column))) ? null : reader.GetDouble(reader.GetOrdinal(Name(column)));
            DateTime? Date(Column column) => Number(column) is { } seconds ? ReferenceDate.AddSeconds(seconds) : null;
            var modified = Text(Column.ConditionalGetInfoLastModified);
            var etag = Text(Column.ConditionalGetInfoEtag);
            var cacheDate = Date(Column.CacheControlInfoDateCreated);
            var maxAge = Number(Column.CacheControlInfoMaxAge);
            var authors = Text(Column.Authors);
            var relationships = Text(Column.FolderRelationship);
            var responseCode = Number(Column.LastResponseCode);
            result[reader.GetString(reader.GetOrdinal("feedURL"))] = new(
                Text(Column.FeedID) ?? "", Text(Column.HomePageUrl), Text(Column.IconUrl), Text(Column.FaviconUrl),
                Text(Column.EditedName), Text(Column.ContentHash),
                (Number(Column.NewArticleNotificationsEnabled) ?? 0) != 0, (Number(Column.ReaderViewAlwaysEnabled) ?? 0) != 0,
                authors is null ? null : Author.FromJson(authors),
                modified is null && etag is null ? null : new(modified, etag), Date(Column.ConditionalGetInfoDate),
                cacheDate is { } created && maxAge is { } age ? new(created, TimeSpan.FromSeconds(age)) : null,
                Text(Column.ExternalID), relationships is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(relationships),
                Date(Column.LastCheckDate), responseCode is { } code ? checked((int)code) : null);
        }
        return result;
    });

    public void SetString(string? value, string feedUrl, Column column) => Set(feedUrl, new Dictionary<Column, object?> { [column] = value });
    public void SetBool(bool value, string feedUrl, Column column) => Set(feedUrl, new Dictionary<Column, object?> { [column] = value ? 1 : 0 });
    public void SetInt(int? value, string feedUrl, Column column) => Set(feedUrl, new Dictionary<Column, object?> { [column] = value });
    public void SetDate(DateTime? value, string feedUrl, Column column) => Set(feedUrl, new Dictionary<Column, object?> { [column] = Seconds(value) });
    public void SetConditionalGetInfo(ConditionalGetInfo? info, string feedUrl) => Set(feedUrl, new Dictionary<Column, object?>
    {
        [Column.ConditionalGetInfoLastModified] = info?.LastModified, [Column.ConditionalGetInfoEtag] = info?.ETag,
    });
    public void SetCacheControlInfo(CacheControlInfo? info, string feedUrl) => Set(feedUrl, new Dictionary<Column, object?>
    {
        [Column.CacheControlInfoDateCreated] = Seconds(info?.DateCreated), [Column.CacheControlInfoMaxAge] = info?.MaxAge.TotalSeconds,
    });
    public void SetAuthors(IReadOnlySet<Author>? authors, string feedUrl) => SetString(authors is null ? null : Author.ToJson(authors), feedUrl, Column.Authors);
    public void SetFolderRelationship(IReadOnlyDictionary<string, string>? relationship, string feedUrl)
        => SetString(relationship is null ? null : JsonSerializer.Serialize(relationship), feedUrl, Column.FolderRelationship);

    private void Set(string feedUrl, IReadOnlyDictionary<Column, object?> values) => _queue.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE feedSettings SET {string.Join(",", values.Keys.Select(c => $"{Name(c)} = @{Name(c)}"))} WHERE feedURL = @url;";
        command.Parameters.AddWithValue("@url", feedUrl);
        foreach (var (column, value) in values) command.Parameters.AddWithValue("@" + Name(column), value ?? DBNull.Value);
        if (command.ExecuteNonQuery() == 0) throw new InvalidOperationException($"No settings exist for {feedUrl}.");
    });

    public void DeleteSettings(string feedUrl) => _queue.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM feedSettings WHERE feedURL = @url;";
        command.Parameters.AddWithValue("@url", feedUrl);
        command.ExecuteNonQuery();
    });

    public void DeleteSettingsForFeedsNotIn(IReadOnlySet<string> feedUrls)
    {
        if (feedUrls.Count == 0) return;
        _queue.Run(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TEMP TABLE IF NOT EXISTS retainedFeedURLs (feedURL TEXT PRIMARY KEY); DELETE FROM retainedFeedURLs;";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO retainedFeedURLs VALUES (@url);";
            var parameter = command.Parameters.Add("@url", SqliteType.Text);
            foreach (var url in feedUrls) { parameter.Value = url; command.ExecuteNonQuery(); }
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM feedSettings WHERE feedURL NOT IN (SELECT feedURL FROM retainedFeedURLs); DROP TABLE retainedFeedURLs;";
            command.ExecuteNonQuery();
            transaction.Commit();
        });
    }

    public Task VacuumAsync(CancellationToken ct = default) => _queue.VacuumAsync(ct);

    internal static Dictionary<Column, object?> Values(Row row) => new()
    {
        [Column.FeedID] = row.FeedID, [Column.HomePageUrl] = row.HomePageUrl, [Column.IconUrl] = row.IconUrl,
        [Column.FaviconUrl] = row.FaviconUrl, [Column.EditedName] = row.EditedName, [Column.ContentHash] = row.ContentHash,
        [Column.NewArticleNotificationsEnabled] = row.NewArticleNotificationsEnabled ? 1 : 0,
        [Column.ReaderViewAlwaysEnabled] = row.ReaderViewAlwaysEnabled ? 1 : 0,
        [Column.Authors] = row.Authors is null ? null : Author.ToJson(row.Authors),
        [Column.ConditionalGetInfoLastModified] = row.ConditionalGetInfo?.LastModified,
        [Column.ConditionalGetInfoEtag] = row.ConditionalGetInfo?.ETag, [Column.ConditionalGetInfoDate] = Seconds(row.ConditionalGetInfoDate),
        [Column.CacheControlInfoDateCreated] = Seconds(row.CacheControlInfo?.DateCreated),
        [Column.CacheControlInfoMaxAge] = row.CacheControlInfo?.MaxAge.TotalSeconds, [Column.ExternalID] = row.ExternalID,
        [Column.FolderRelationship] = row.FolderRelationship is null ? null : JsonSerializer.Serialize(row.FolderRelationship),
        [Column.LastCheckDate] = Seconds(row.LastCheckDate), [Column.LastResponseCode] = row.LastResponseCode,
    };

    private static double? Seconds(DateTime? date) => date is { } value ? (value.ToUniversalTime() - ReferenceDate).TotalSeconds : null;
    private static string Name(Column column)
    {
        if (!Enum.IsDefined(column)) throw new ArgumentOutOfRangeException(nameof(column));
        var name = column.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..].Replace("Url", "URL", StringComparison.Ordinal);
    }

    public void Dispose() => _queue.Dispose();
}
