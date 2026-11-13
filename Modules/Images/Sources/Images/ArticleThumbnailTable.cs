using Microsoft.Data.Sqlite;

namespace WinNewsWire.Images;

public sealed record ArticleThumbnailRecord(string ArticleId, string? ThumbnailUrl);

internal static class ArticleThumbnailTable
{
    internal static IReadOnlyList<ArticleThumbnailRecord> Fetch(SqliteConnection connection, string[] articleIds)
    {
        var records = new List<ArticleThumbnailRecord>();
        foreach (var chunk in articleIds.Chunk(500))
        {
            using var command = connection.CreateCommand();
            var parameters = chunk.Select((id, index) =>
            {
                var name = "$id" + index;
                command.Parameters.AddWithValue(name, id);
                return name;
            });
            command.CommandText = $"SELECT articleID, thumbnailURL FROM articleThumbnail WHERE articleID IN ({string.Join(',', parameters)});";
            using var rows = command.ExecuteReader();
            while (rows.Read())
                records.Add(new(rows.GetString(0), rows.IsDBNull(1) ? null : rows.GetString(1)));
        }
        return records;
    }

    internal static void Save(SqliteConnection connection, IReadOnlyList<ArticleThumbnailRecord> records, DateTimeOffset now)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO articleThumbnail (articleID,thumbnailURL,lastChecked) VALUES ($id,$url,$checked);";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var url = command.Parameters.Add("$url", SqliteType.Text);
        command.Parameters.AddWithValue("$checked", (now - DateTimeOffset.UnixEpoch).TotalSeconds);
        foreach (var record in records)
        {
            id.Value = record.ArticleId;
            url.Value = (object?)record.ThumbnailUrl ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal static void RemoveExpired(SqliteConnection connection, DateTimeOffset cutoff)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM articleThumbnail WHERE lastChecked < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", (cutoff - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }
}
