using Microsoft.Data.Sqlite;
using WinNewsWire.Web;

namespace WinNewsWire.Images;

public sealed record DownloadedImageRecord(string Url, long ByteCount,
    (uint Width, uint Height)? PixelSize, ConditionalGetInfo? ConditionalGet);

public sealed record DownloadedImageUsage(string Url, long ByteCount, DateTimeOffset LastAccessed);

internal static class DownloadedImageTable
{
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
            INSERT OR REPLACE INTO downloadedImage (url,byteCount,pixelWidth,pixelHeight,lastAccessed,etag,lastModified)
            VALUES ($url,$bytes,$width,$height,$accessed,$etag,$modified);
            """;
        command.Parameters.AddWithValue("$url", row.Url);
        command.Parameters.AddWithValue("$bytes", row.ByteCount);
        command.Parameters.AddWithValue("$width", (object?)row.PixelSize?.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)row.PixelSize?.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$accessed", (now - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.Parameters.AddWithValue("$etag", (object?)row.ConditionalGet?.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$modified", (object?)row.ConditionalGet?.LastModified ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
