using Microsoft.Data.Sqlite;
using WinNewsWire.Database;

namespace WinNewsWire.ArticlesDatabase;

public enum ArticleRetentionStyle { PreserveHistory, FeedBased, SyncSystem }

public sealed record ArticleCleanupResult(int ArticlesDeleted, int StatusesDeleted, bool ReachedRecentCutoff, bool PreservedHistory);

public sealed partial class ArticlesDatabaseStore
{
    public Task<ArticleCleanupResult> CleanupDatabaseAtStartupAsync(IEnumerable<string> subscribedFeedIDs,
        ArticleRetentionStyle retentionStyle, TimeProvider? clock = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscribedFeedIDs);
        if (!Enum.IsDefined(retentionStyle)) throw new ArgumentOutOfRangeException(nameof(retentionStyle));
        var subscribed = subscribedFeedIDs.ToHashSet(StringComparer.Ordinal);
        clock ??= TimeProvider.System;
        return Queue.RunAsync(connection =>
        {
            ct.ThrowIfCancellationRequested();
            if (retentionStyle == ArticleRetentionStyle.PreserveHistory)
                return Task.FromResult(new ArticleCleanupResult(0, 0, true, true));

            var now = clock.GetUtcNow().UtcDateTime;
            using var transaction = connection.BeginTransaction();
            int deletedArticles = 0;
            bool reachedRecentCutoff = true;
            if (retentionStyle == ArticleRetentionStyle.SyncSystem)
            {
                long started = clock.GetTimestamp();
                foreach (int days in new[] { 365, 300, 225, 150, 90 })
                {
                    ct.ThrowIfCancellationRequested();
                    var ids = RetentionIDs(connection, """
                        SELECT a.articleID FROM articles a JOIN statuses s ON a.articleID=s.articleID
                        WHERE s.dateArrived < @cutoff AND s.read=1 AND s.starred=0;
                        """, now.AddDays(-days), ct);
                    deletedArticles += DeleteRetainedArticleBodies(connection, ids, ct);
                    if (days != 90 && clock.GetElapsedTime(started) > TimeSpan.FromSeconds(2))
                    {
                        reachedRecentCutoff = false;
                        break;
                    }
                }
            }

            var deletedStatuses = new HashSet<string>(StringComparer.Ordinal);
            if (subscribed.Count > 0)
            {
                var removed = new List<string>();
                using (var query = connection.CreateCommand())
                {
                    query.CommandText = "SELECT articleID,feedID FROM articles;";
                    using var rows = query.ExecuteReader();
                    while (rows.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!subscribed.Contains(rows.GetString(1))) removed.Add(rows.GetString(0));
                    }
                }
                deletedArticles += DeleteRetainedArticleBodies(connection, removed, ct);
                deletedStatuses.UnionWith(removed);
            }

            string statusCondition = retentionStyle == ArticleRetentionStyle.SyncSystem ? "AND read=1" : "";
            int statusDays = retentionStyle == ArticleRetentionStyle.SyncSystem ? 180 : 30;
            deletedStatuses.UnionWith(RetentionIDs(connection, $"""
                SELECT articleID FROM statuses WHERE dateArrived < @cutoff AND starred=0 {statusCondition}
                    AND NOT EXISTS (SELECT 1 FROM articles a WHERE a.articleID=statuses.articleID);
                """, now.AddDays(-statusDays), ct));
            int deletedStatusCount = 0;
            foreach (var batch in deletedStatuses.Chunk(500))
            {
                ct.ThrowIfCancellationRequested();
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM statuses WHERE articleID IN (" +
                    string.Join(",", batch.Select((_, index) => "@id" + index)) + ");";
                for (int index = 0; index < batch.Length; index++) delete.Parameters.AddWithValue("@id" + index, batch[index]);
                deletedStatusCount += delete.ExecuteNonQuery();
            }
            if (deletedArticles > 0)
            {
                connection.Execute("DELETE FROM authors WHERE NOT EXISTS (SELECT 1 FROM authorsLookup l WHERE l.authorID=authors.authorID);");
                connection.Execute("DELETE FROM attachments WHERE NOT EXISTS (SELECT 1 FROM attachmentsLookup l WHERE l.attachmentID=attachments.attachmentID);");
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            if (deletedArticles > 0) _articlesCache.Clear();
            _statuses.Remove(deletedStatuses);
            return Task.FromResult(new ArticleCleanupResult(deletedArticles, deletedStatusCount, reachedRecentCutoff, false));
        });
    }

    private static List<string> RetentionIDs(SqliteConnection connection, string sql, DateTime cutoff, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@cutoff", cutoff.ToUnixTimestamp());
        using var rows = command.ExecuteReader();
        var ids = new List<string>();
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            ids.Add(rows.GetString(0));
        }
        return ids;
    }

    private static int DeleteRetainedArticleBodies(SqliteConnection connection, IEnumerable<string> ids, CancellationToken ct)
    {
        int deleted = 0;
        foreach (var batch in ids.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            var parameters = string.Join(",", batch.Select((_, index) => "@id" + index));
            command.CommandText = $"DELETE FROM articles WHERE articleID IN ({parameters});";
            for (int index = 0; index < batch.Length; index++) command.Parameters.AddWithValue("@id" + index, batch[index]);
            deleted += command.ExecuteNonQuery();
            command.CommandText = $"""
                DELETE FROM authorsLookup WHERE articleID IN ({parameters});
                DELETE FROM attachmentsLookup WHERE articleID IN ({parameters});
                """;
            command.ExecuteNonQuery();
        }
        return deleted;
    }
}
