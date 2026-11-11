using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace WinNewsWire.HTMLMetadata;

internal static class HtmlMetadataTable
{
    internal const string CreateSql = """
        CREATE TABLE IF NOT EXISTS metadata (
          url TEXT PRIMARY KEY NOT NULL, lastChecked REAL NOT NULL,
          statusCode INTEGER NOT NULL DEFAULT 200, favicons TEXT, appleTouchIcons TEXT,
          feedLinks TEXT, openGraphImages TEXT, twitterImageURL TEXT);
        """;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal sealed record Entry(HtmlMetadataRecord Record, DateTimeOffset LastChecked, int StatusCode)
    {
        public bool IsFailure => StatusCode == 0 || StatusCode is >= 400 and <= 499;
    }

    internal static Entry? Fetch(SqliteConnection connection, string url)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT lastChecked, statusCode, favicons, appleTouchIcons, feedLinks, openGraphImages, twitterImageURL FROM metadata WHERE url = $url;";
        command.Parameters.AddWithValue("$url", url);
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        return new(new HtmlMetadataRecord(url, Decode<HtmlMetadataRecord.Favicon>(row, 2),
            Decode<HtmlMetadataRecord.AppleTouchIcon>(row, 3), Decode<HtmlMetadataRecord.FeedLink>(row, 4),
            Decode<HtmlMetadataRecord.OpenGraphImage>(row, 5), row.IsDBNull(6) ? null : row.GetString(6)),
            DateTimeOffset.UnixEpoch.AddSeconds(row.GetDouble(0)), row.GetInt32(1));
    }

    internal static void Save(SqliteConnection connection, HtmlMetadataRecord record, int statusCode, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO metadata (url,lastChecked,statusCode,favicons,appleTouchIcons,feedLinks,openGraphImages,twitterImageURL)
            VALUES ($url,$checked,$status,$favicons,$touch,$feeds,$images,$twitter);
            """;
        command.Parameters.AddWithValue("$url", record.Url);
        command.Parameters.AddWithValue("$checked", (now - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.Parameters.AddWithValue("$status", statusCode);
        command.Parameters.AddWithValue("$favicons", Encode(record.Favicons));
        command.Parameters.AddWithValue("$touch", Encode(record.AppleTouchIcons));
        command.Parameters.AddWithValue("$feeds", Encode(record.FeedLinks));
        command.Parameters.AddWithValue("$images", Encode(record.OpenGraphImages));
        command.Parameters.AddWithValue("$twitter", (object?)record.TwitterImageUrl ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal static void NoteFailure(SqliteConnection connection, string url, int statusCode, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata (url,lastChecked,statusCode) VALUES ($url,$checked,$status)
            ON CONFLICT(url) DO UPDATE SET lastChecked=excluded.lastChecked,statusCode=excluded.statusCode
            WHERE statusCode >= 400 OR statusCode = 0;
            """;
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$checked", (now - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.Parameters.AddWithValue("$status", statusCode);
        command.ExecuteNonQuery();
    }

    internal static void RemoveExpired(SqliteConnection connection, DateTimeOffset cutoff)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metadata WHERE lastChecked < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", (cutoff - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }

    private static object Encode<T>(IReadOnlyList<T> values) => values.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(values, JsonOptions);
    private static T[] Decode<T>(SqliteDataReader row, int ordinal)
    {
        if (row.IsDBNull(ordinal)) return [];
        try { return JsonSerializer.Deserialize<T[]>(row.GetString(ordinal), JsonOptions) ?? []; }
        catch (JsonException error) { throw new InvalidDataException("Stored HTML metadata contains invalid JSON.", error); }
    }
}
