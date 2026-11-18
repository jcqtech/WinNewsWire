namespace WinNewsWire.ArticlesDatabase;

public sealed partial class ArticlesDatabaseStore
{
    public Task<string[]> FetchPendingPublisherDeletionsAsync() => Queue.RunAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT articleID FROM syncArticleDeletions;";
        using var rows = command.ExecuteReader();
        var ids = new List<string>();
        while (rows.Read()) ids.Add(rows.GetString(0));
        return Task.FromResult(ids.ToArray());
    });

    public Task AcknowledgePublisherDeletionsAsync(IEnumerable<string> articleIDs) => Queue.RunAsync(connection =>
    {
        using var transaction = connection.BeginTransaction();
        foreach (var id in articleIDs)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM syncArticleDeletions WHERE articleID=@id;";
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return Task.CompletedTask;
    });

    public Task<int> DeleteSynchronizedArticlesAsync(IEnumerable<string> articleIDs, CancellationToken ct = default)
    {
        var ids = articleIDs.Distinct(StringComparer.Ordinal).ToArray();
        return Queue.RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            int deleted = DeleteRetainedArticleBodies(connection, ids, ct);
            transaction.Commit();
            foreach (var id in ids) _articlesCache.TryRemove(id, out _);
            return Task.FromResult(deleted);
        });
    }
}
