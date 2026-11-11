using WinNewsWire.Database;

namespace WinNewsWire.ArticlesDatabase;

public sealed record ArticleCounts(long TotalCount, long UnreadCount, long StarredCount, long StatusesCount);

public sealed partial class ArticlesDatabaseStore
{
    public Task VacuumAsync(CancellationToken ct = default) => Queue.VacuumAsync(ct);

    public Task<Dictionary<string, DateTime>> FetchLastUpdateDatesAsync(CancellationToken ct = default)
        => Queue.RunAsync(connection =>
        {
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT a.feedID, MAX(COALESCE(a.datePublished, a.dateModified, s.dateArrived))
                FROM articles a JOIN statuses s ON a.articleID = s.articleID
                GROUP BY a.feedID;
                """;
            using var reader = command.ExecuteReader();
            var dates = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (!reader.IsDBNull(1)) dates.Add(reader.GetString(0), reader.GetUnixTime(1));
            }
            return Task.FromResult(dates);
        });

    public Task<ArticleCounts> FetchArticleCountsAsync(IEnumerable<string> feedIDs, CancellationToken ct = default)
    {
        var ids = feedIDs.Distinct(StringComparer.Ordinal).ToArray();
        return Queue.RunAsync(connection =>
        {
            ct.ThrowIfCancellationRequested();
            using var transaction = connection.BeginTransaction(deferred: true);
            long total = 0, unread = 0, starred = 0;
            foreach (var batch in ids.Chunk(500))
            {
                ct.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT COUNT(*), COALESCE(SUM(CASE WHEN s.read = 0 THEN 1 ELSE 0 END), 0),
                        COALESCE(SUM(CASE WHEN s.starred = 1 THEN 1 ELSE 0 END), 0)
                    FROM articles a JOIN statuses s ON a.articleID = s.articleID
                    WHERE a.feedID IN ({string.Join(",", batch.Select((_, index) => $"@feed{index}"))});
                    """;
                for (int i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"@feed{i}", batch[i]);
                using var row = command.ExecuteReader();
                if (!row.Read()) throw new InvalidDataException("The database did not return article counts.");
                total += row.GetInt64(0);
                unread += row.GetInt64(1);
                starred += row.GetInt64(2);
            }
            using var statuses = connection.CreateCommand();
            statuses.CommandText = "SELECT COUNT(*) FROM statuses;";
            var statusCount = Convert.ToInt64(statuses.ExecuteScalar());
            transaction.Commit();
            return Task.FromResult(new ArticleCounts(total, unread, starred, statusCount));
        });
    }
}
