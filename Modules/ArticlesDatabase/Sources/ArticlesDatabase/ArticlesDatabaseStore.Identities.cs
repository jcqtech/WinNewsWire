using WinNewsWire.Articles;
using WinNewsWire.Database;

namespace WinNewsWire.ArticlesDatabase;

public sealed record ArticleIdentity(string FeedID, string UniqueID, DateTime LogicalDate);

public sealed partial class ArticlesDatabaseStore
{
    public Task<HashSet<Article>> FetchArticlesByUniqueIDsAsync(string feedID, IEnumerable<string> uniqueIDs,
        CancellationToken ct = default)
    {
        var ids = uniqueIDs.Distinct(StringComparer.Ordinal).ToArray();
        return Queue.RunAsync(connection =>
        {
            var articles = new HashSet<Article>();
            foreach (var batch in ids.Chunk(499))
            {
                ct.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown,
                        a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified,
                        s.read, s.starred, s.dateArrived
                    FROM articles a JOIN statuses s ON a.articleID = s.articleID
                    WHERE a.feedID = @feed AND a.uniqueID IN ({string.Join(",", batch.Select((_, i) => $"@id{i}"))});
                    """;
                command.Parameters.AddWithValue("@feed", feedID);
                for (int i = 0; i < batch.Length; ++i) command.Parameters.AddWithValue($"@id{i}", batch[i]);
                articles.UnionWith(ReadAll(command));
            }
            return Task.FromResult(articles);
        });
    }

    public Task<IReadOnlyList<ArticleIdentity>> FetchArticleIdentitiesAsync(IEnumerable<string> feedIDs,
        CancellationToken ct = default)
    {
        var ids = feedIDs.Distinct(StringComparer.Ordinal).ToArray();
        return Queue.RunAsync(connection =>
        {
            var identities = new List<ArticleIdentity>();
            foreach (var batch in ids.Chunk(500))
            {
                ct.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT a.feedID, a.uniqueID, COALESCE(a.datePublished, a.dateModified, s.dateArrived)
                    FROM articles a JOIN statuses s ON a.articleID = s.articleID
                    WHERE a.feedID IN ({string.Join(",", batch.Select((_, i) => $"@feed{i}"))});
                    """;
                for (int i = 0; i < batch.Length; ++i) command.Parameters.AddWithValue($"@feed{i}", batch[i]);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    identities.Add(new(reader.GetString(0), reader.GetString(1), reader.GetUnixTime(2)));
                }
            }
            return Task.FromResult<IReadOnlyList<ArticleIdentity>>(identities.OrderByDescending(item => item.LogicalDate)
                .ThenBy(item => item.FeedID, StringComparer.Ordinal).ThenBy(item => item.UniqueID, StringComparer.Ordinal).ToArray());
        });
    }
}
