using Microsoft.Data.Sqlite;

namespace WinNewsWire.Images;

public sealed record ArticleThumbnailRecord
{
    public string ArticleId { get; }
    public IReadOnlyList<string> ThumbnailUrls { get; }

    public ArticleThumbnailRecord(string articleId, IEnumerable<string> thumbnailUrls)
    {
        ArgumentException.ThrowIfNullOrEmpty(articleId);
        ArgumentNullException.ThrowIfNull(thumbnailUrls);
        var urls = thumbnailUrls.ToArray();
        foreach (var url in urls)
        {
            ArgumentException.ThrowIfNullOrEmpty(url);
            if (url.Contains('\n') || url.Contains('\r'))
                throw new ArgumentException("Thumbnail URLs must not contain unencoded line breaks.", nameof(thumbnailUrls));
        }
        ArticleId = articleId;
        ThumbnailUrls = Array.AsReadOnly(urls);
    }

    public bool Equals(ArticleThumbnailRecord? other)
        => other is not null && ArticleId == other.ArticleId && ThumbnailUrls.SequenceEqual(other.ThumbnailUrls, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ArticleId, StringComparer.Ordinal);
        foreach (var url in ThumbnailUrls) hash.Add(url, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

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
            command.CommandText = $"SELECT articleID, thumbnailURLs FROM articleThumbnailURLs WHERE articleID IN ({string.Join(',', parameters)});";
            using var rows = command.ExecuteReader();
            while (rows.Read())
                records.Add(new(rows.GetString(0), rows.IsDBNull(1) ? [] : rows.GetString(1).Split('\n')));
        }
        return records;
    }

    internal static void Save(SqliteConnection connection, IReadOnlyList<ArticleThumbnailRecord> records, DateTimeOffset now)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO articleThumbnailURLs (articleID,thumbnailURLs,lastChecked) VALUES ($id,$url,$checked);";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var url = command.Parameters.Add("$url", SqliteType.Text);
        command.Parameters.AddWithValue("$checked", (now - DateTimeOffset.UnixEpoch).TotalSeconds);
        foreach (var record in records)
        {
            id.Value = record.ArticleId;
            url.Value = record.ThumbnailUrls.Count == 0 ? DBNull.Value : string.Join('\n', record.ThumbnailUrls);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal static void RemoveExpired(SqliteConnection connection, DateTimeOffset cutoff)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM articleThumbnailURLs WHERE lastChecked < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", (cutoff - DateTimeOffset.UnixEpoch).TotalSeconds);
        command.ExecuteNonQuery();
    }
}
