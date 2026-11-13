using Microsoft.Data.Sqlite;
using WinNewsWire.Web;

namespace WinNewsWire.Images;

public sealed record DownloadedImageRecord(string Url, long ByteCount,
    (uint Width, uint Height)? PixelSize, ConditionalGetInfo? ConditionalGet, DateTimeOffset? NextCheck = null);

public sealed record DownloadedImageValidation(DateTimeOffset NextCheck, ConditionalGetInfo? ConditionalGet);

public sealed record DownloadedImageUsage(string Url, long ByteCount, DateTimeOffset LastAccessed);

internal static class DownloadedImageTable
{
    internal static void EnsureValidationColumn(SqliteConnection connection)
    {
        bool exists = false;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(downloadedImage);";
            using var rows = command.ExecuteReader();
            while (rows.Read())
                if (rows.GetString(1) == "nextCheck") exists = true;
        }
        if (exists) return;
        using var migration = connection.CreateCommand();
        migration.CommandText = "ALTER TABLE downloadedImage ADD COLUMN nextCheck REAL NOT NULL DEFAULT 0;";
        migration.ExecuteNonQuery();
    }

    internal static DownloadedImageValidation? FetchValidation(SqliteConnection connection, string url)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT nextCheck,lastModified,etag FROM downloadedImage WHERE url=$url;";
        command.Parameters.AddWithValue("$url", url);
        using var rows = command.ExecuteReader();
        if (!rows.Read()) return null;
        string? modified = rows.IsDBNull(1) ? null : rows.GetString(1);
        string? etag = rows.IsDBNull(2) ? null : rows.GetString(2);
        return new(DateTimeOffset.UnixEpoch.AddSeconds(rows.GetDouble(0)),
            modified is null && etag is null ? null : new(modified, etag));
    }

    internal static void SaveNextCheck(SqliteConnection connection, string url, DateTimeOffset nextCheck)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE downloadedImage SET nextCheck=$next WHERE url=$url;";
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$next", (nextCheck - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }

    internal static IReadOnlyList<DownloadedImageUsage> FetchUsageByLeastRecentAccess(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url,byteCount,lastAccessed FROM downloadedImage ORDER BY lastAccessed,url;";
        using var rows = command.ExecuteReader();
        var result = new List<DownloadedImageUsage>();
        while (rows.Read())
            result.Add(new(rows.GetString(0), rows.GetInt64(1), DateTimeOffset.UnixEpoch.AddSeconds(rows.GetDouble(2))));
        return result;
    }

    internal static void SaveAccessDate(SqliteConnection connection, string url, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE downloadedImage SET lastAccessed=$accessed WHERE url=$url;";
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$accessed", (now - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }

    internal static void Delete(SqliteConnection connection, IReadOnlyList<string> urls)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var chunk in urls.Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var parameters = chunk.Select((url, index) =>
            {
                var name = "$url" + index;
                command.Parameters.AddWithValue(name, url);
                return name;
            });
            command.CommandText = $"DELETE FROM downloadedImage WHERE url IN ({string.Join(',', parameters)});";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal static IReadOnlyList<(string Url, (uint Width, uint Height)? PixelSize)> FetchPixelSizes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url, pixelWidth, pixelHeight FROM downloadedImage;";
        using var rows = command.ExecuteReader();
        var result = new List<(string, (uint, uint)?)>();
        while (rows.Read())
            result.Add((rows.GetString(0), rows.IsDBNull(1) || rows.IsDBNull(2) ? null
                : (checked((uint)rows.GetInt64(1)), checked((uint)rows.GetInt64(2)))));
        return result;
    }

    internal static void Save(SqliteConnection connection, DownloadedImageRecord row, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO downloadedImage (url,byteCount,pixelWidth,pixelHeight,lastAccessed,etag,lastModified,nextCheck)
            VALUES ($url,$bytes,$width,$height,$accessed,$etag,$modified,$next);
            """;
        command.Parameters.AddWithValue("$url", row.Url);
        command.Parameters.AddWithValue("$bytes", row.ByteCount);
        command.Parameters.AddWithValue("$width", (object?)row.PixelSize?.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)row.PixelSize?.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$accessed", (now - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.Parameters.AddWithValue("$etag", (object?)row.ConditionalGet?.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$modified", (object?)row.ConditionalGet?.LastModified ?? DBNull.Value);
        var next = row.NextCheck ?? DownloadedImageCache.CalculateNextCheck(null, row.ConditionalGet is not null, now);
        command.Parameters.AddWithValue("$next", (next - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }
}
