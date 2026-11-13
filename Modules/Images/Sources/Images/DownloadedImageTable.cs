using Microsoft.Data.Sqlite;
using WinNewsWire.Web;

namespace WinNewsWire.Images;

public sealed record DownloadedImageRecord(string Url, long ByteCount,
    (uint Width, uint Height)? PixelSize, ConditionalGetInfo? ConditionalGet);

internal static class DownloadedImageTable
{
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
