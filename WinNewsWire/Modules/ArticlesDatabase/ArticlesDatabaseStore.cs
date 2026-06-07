using Microsoft.Data.Sqlite;
using WinNewsWire.Articles;
using WinNewsWire.Database;

namespace WinNewsWire.ArticlesDatabase;

/// <summary>
/// Port of <c>ArticlesDatabase</c>. Provides storage and retrieval of Article/ArticleStatus/Author
/// tuples backed by SQLite (FTS4 for search). All writes are serialized via DatabaseQueue.
/// </summary>
public sealed class ArticlesDatabaseStore : IDisposable
{
    public string AccountID { get; }
    public DatabaseQueue Queue { get; }

    public ArticlesDatabaseStore(string filePath, string accountID)
    {
        AccountID = accountID;
        Queue = new DatabaseQueue(filePath);
        Queue.Run(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = Constants.CreateStatements;
            cmd.ExecuteNonQuery();
        });
    }

    public Task<int> UnreadCountAsync(IEnumerable<string> feedIDs)
    {
        var ids = feedIDs.ToList();
        if (ids.Count == 0) return Task.FromResult(0);
        return Queue.RunAsync(c =>
        {
            var placeholders = string.Join(",", ids.Select((_, i) => $"@f{i}"));
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM articles a JOIN statuses s ON a.articleID = s.articleID WHERE a.feedID IN ({placeholders}) AND s.read = 0;";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            var o = cmd.ExecuteScalar();
            return Task.FromResult(Convert.ToInt32(o));
        });
    }

    public Task<Dictionary<string, int>> UnreadCountsByFeedAsync(IEnumerable<string> feedIDs)
    {
        var ids = feedIDs.ToList();
        var dict = new Dictionary<string, int>();
        if (ids.Count == 0) return Task.FromResult(dict);
        return Queue.RunAsync(c =>
        {
            var placeholders = string.Join(",", ids.Select((_, i) => $"@f{i}"));
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT a.feedID, COUNT(*) FROM articles a JOIN statuses s ON a.articleID = s.articleID WHERE a.feedID IN ({placeholders}) AND s.read = 0 GROUP BY a.feedID;";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) dict[r.GetString(0)] = r.GetInt32(1);
            return Task.FromResult(dict);
        });
    }

    public Task<HashSet<Article>> FetchArticlesAsync(string feedID)
    {
        return Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                                 FROM articles a JOIN statuses s ON a.articleID = s.articleID WHERE a.feedID = @f;";
            cmd.Parameters.AddWithValue("@f", feedID);
            return Task.FromResult(ReadAll(cmd));
        });
    }

    public Task<HashSet<Article>> FetchArticlesAsync(IEnumerable<string> feedIDs, int? limit = null)
    {
        var ids = feedIDs.ToList();
        if (ids.Count == 0) return Task.FromResult(new HashSet<Article>());
        return Queue.RunAsync(c =>
        {
            var placeholders = string.Join(",", ids.Select((_, i) => $"@f{i}"));
            using var cmd = c.CreateCommand();
            var sql = @$"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                         FROM articles a JOIN statuses s ON a.articleID = s.articleID
                         WHERE a.feedID IN ({placeholders})
                         ORDER BY a.datePublished DESC";
            if (limit is not null) sql += $" LIMIT {limit.Value}";
            cmd.CommandText = sql + ";";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            return Task.FromResult(ReadAll(cmd));
        });
    }

    public Task<HashSet<Article>> FetchUnreadArticlesAsync(IEnumerable<string> feedIDs, int? limit = null)
    {
        var ids = feedIDs.ToList();
        if (ids.Count == 0) return Task.FromResult(new HashSet<Article>());
        return Queue.RunAsync(c =>
        {
            var placeholders = string.Join(",", ids.Select((_, i) => $"@f{i}"));
            using var cmd = c.CreateCommand();
            var sql = @$"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                         FROM articles a JOIN statuses s ON a.articleID = s.articleID
                         WHERE a.feedID IN ({placeholders}) AND s.read = 0
                         ORDER BY a.datePublished DESC";
            if (limit is not null) sql += $" LIMIT {limit.Value}";
            cmd.CommandText = sql + ";";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            return Task.FromResult(ReadAll(cmd));
        });
    }

    public Task<HashSet<Article>> FetchStarredAsync(IEnumerable<string> feedIDs, int? limit = null)
    {
        var ids = feedIDs.ToList();
        return Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            var where = ids.Count == 0 ? "" : "WHERE a.feedID IN (" + string.Join(",", ids.Select((_, i) => $"@f{i}")) + ") AND ";
            var sql = @$"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                         FROM articles a JOIN statuses s ON a.articleID = s.articleID
                         {(where.Length == 0 ? "WHERE " : where)} s.starred = 1
                         ORDER BY a.datePublished DESC";
            if (limit is not null) sql += $" LIMIT {limit.Value}";
            cmd.CommandText = sql + ";";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            return Task.FromResult(ReadAll(cmd));
        });
    }

    private HashSet<Article> ReadAll(SqliteCommand cmd)
    {
        // Stage the main rows first so we can run a follow-up authors query on the same
        // connection. (Authors are written via authorsLookup but never joined in the main
        // SELECTs; we attach them here so Article.Authors round-trips through the database.)
        var staged = new List<(string articleID, string feedID, string uniqueID, string? title,
            string? contentHtml, string? contentText, string? markdown, string? url, string? externalURL,
            string? summary, string? imageURL, DateTime? datePublished, DateTime? dateModified,
            ArticleStatus status)>();
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var articleID = r.GetString(0);
                var status = new ArticleStatus(
                    articleID,
                    r.GetInt32(13) != 0,
                    r.GetInt32(14) != 0,
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(15)).UtcDateTime);
                staged.Add((
                    articleID,
                    r.GetString(1),
                    r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetString(8),
                    r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetString(10),
                    r.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(11)).UtcDateTime,
                    r.IsDBNull(12) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(12)).UtcDateTime,
                    status));
            }
        }

        Dictionary<string, HashSet<Author>>? authorsByArticle = null;
        Dictionary<string, HashSet<Attachment>>? attachmentsByArticle = null;
        if (staged.Count > 0 && cmd.Connection is { } conn)
        {
            authorsByArticle = new Dictionary<string, HashSet<Author>>();
            using var ac = conn.CreateCommand();
            var placeholders = string.Join(",", staged.Select((_, i) => $"@a{i}"));
            ac.CommandText = $@"SELECT al.articleID, a.authorID, a.name, a.url, a.avatarURL, a.emailAddress
                                FROM authorsLookup al JOIN authors a ON al.authorID = a.authorID
                                WHERE al.articleID IN ({placeholders});";
            for (int i = 0; i < staged.Count; i++)
                ac.Parameters.AddWithValue($"@a{i}", staged[i].articleID);
            using var ar = ac.ExecuteReader();
            while (ar.Read())
            {
                var aid = ar.GetString(0);
                var author = Author.Create(
                    ar.GetString(1),
                    ar.IsDBNull(2) ? null : ar.GetString(2),
                    ar.IsDBNull(3) ? null : ar.GetString(3),
                    ar.IsDBNull(4) ? null : ar.GetString(4),
                    ar.IsDBNull(5) ? null : ar.GetString(5));
                if (author is null) continue;
                if (!authorsByArticle.TryGetValue(aid, out var setForArticle))
                    authorsByArticle[aid] = setForArticle = new HashSet<Author>();
                setForArticle.Add(author);
            }

            attachmentsByArticle = new Dictionary<string, HashSet<Attachment>>();
            using var attCmd = conn.CreateCommand();
            attCmd.CommandText = $@"SELECT al.articleID, a.attachmentID, a.url, a.mimeType, a.title, a.sizeInBytes, a.durationInSeconds
                                    FROM attachmentsLookup al JOIN attachments a ON al.attachmentID = a.attachmentID
                                    WHERE al.articleID IN ({placeholders});";
            for (int i = 0; i < staged.Count; i++)
                attCmd.Parameters.AddWithValue($"@a{i}", staged[i].articleID);
            using var attR = attCmd.ExecuteReader();
            while (attR.Read())
            {
                var aid = attR.GetString(0);
                var attachment = Attachment.Create(
                    attR.GetString(1),
                    attR.IsDBNull(2) ? null : attR.GetString(2),
                    attR.IsDBNull(3) ? null : attR.GetString(3),
                    attR.IsDBNull(4) ? null : attR.GetString(4),
                    attR.IsDBNull(5) ? null : (long?)attR.GetInt64(5),
                    attR.IsDBNull(6) ? null : (int?)attR.GetInt32(6));
                if (attachment is null) continue;
                if (!attachmentsByArticle.TryGetValue(aid, out var setForAttachment))
                    attachmentsByArticle[aid] = setForAttachment = new HashSet<Attachment>();
                setForAttachment.Add(attachment);
            }
        }

        var set = new HashSet<Article>();
        foreach (var s in staged)
        {
            HashSet<Author>? authors = null;
            authorsByArticle?.TryGetValue(s.articleID, out authors);
            HashSet<Attachment>? attachments = null;
            attachmentsByArticle?.TryGetValue(s.articleID, out attachments);
            set.Add(new Article(
                AccountID, s.articleID, s.feedID, s.uniqueID,
                s.title, s.contentHtml, s.contentText, s.markdown,
                s.url, s.externalURL, s.summary, s.imageURL,
                s.datePublished, s.dateModified,
                authors, attachments, s.status));
        }
        return set;
    }

    public async Task<IReadOnlyList<Article>> UpdateArticlesAsync(IEnumerable<Article> articles)
    {
        var list = articles.ToList();
        if (list.Count == 0) return Array.Empty<Article>();
        var newArticles = new List<Article>();
        await Queue.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();

            // Determine which articleIDs are not yet present so callers can post
            // "new article" notifications. Done once before the INSERT OR REPLACE
            // below otherwise every article would look new.
            var existing = new HashSet<string>();
            using (var probe = c.CreateCommand())
            {
                var placeholders = string.Join(",", list.Select((_, i) => $"@p{i}"));
                probe.CommandText = $"SELECT articleID FROM articles WHERE articleID IN ({placeholders});";
                for (int i = 0; i < list.Count; i++)
                    probe.Parameters.AddWithValue($"@p{i}", list[i].ArticleID);
                using var reader = probe.ExecuteReader();
                while (reader.Read()) existing.Add(reader.GetString(0));
            }

            foreach (var a in list)
            {
                if (!existing.Contains(a.ArticleID)) newArticles.Add(a);
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"INSERT OR REPLACE INTO articles
                        (articleID, feedID, uniqueID, title, contentHTML, contentText, markdown, url, externalURL, summary, imageURL, bannerImageURL, datePublished, dateModified, searchRowID)
                        VALUES (@articleID, @feedID, @uniqueID, @title, @contentHTML, @contentText, @markdown, @url, @externalURL, @summary, @imageURL, NULL, @datePublished, @dateModified, NULL);";
                    cmd.Parameters.AddWithValue("@articleID", a.ArticleID);
                    cmd.Parameters.AddWithValue("@feedID", a.FeedID);
                    cmd.Parameters.AddWithValue("@uniqueID", a.UniqueID);
                    cmd.Parameters.AddWithValue("@title", (object?)a.Title ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@contentHTML", (object?)a.ContentHtml ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@contentText", (object?)a.ContentText ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@markdown", (object?)a.Markdown ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@url", (object?)a.RawLink ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@externalURL", (object?)a.RawExternalLink ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@summary", (object?)a.Summary ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@imageURL", (object?)a.RawImageLink ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@datePublished", (object?)a.DatePublished?.ToUnixSeconds() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@dateModified", (object?)a.DateModified?.ToUnixSeconds() ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"INSERT OR IGNORE INTO statuses (articleID, read, starred, dateArrived) VALUES (@id, @r, @s, @d);";
                    cmd.Parameters.AddWithValue("@id", a.ArticleID);
                    cmd.Parameters.AddWithValue("@r", a.Status.Read ? 1 : 0);
                    cmd.Parameters.AddWithValue("@s", a.Status.Starred ? 1 : 0);
                    cmd.Parameters.AddWithValue("@d", a.Status.DateArrived.ToUnixSeconds());
                    cmd.ExecuteNonQuery();
                }
                if (a.Authors is not null)
                {
                    foreach (var au in a.Authors)
                    {
                        using var ac = c.CreateCommand();
                        ac.CommandText = @"INSERT OR REPLACE INTO authors (authorID, name, url, avatarURL, emailAddress) VALUES (@i,@n,@u,@a,@e);
                                           INSERT OR IGNORE INTO authorsLookup (authorID, articleID) VALUES (@i, @aid);";
                        ac.Parameters.AddWithValue("@i", au.AuthorID);
                        ac.Parameters.AddWithValue("@n", (object?)au.Name ?? DBNull.Value);
                        ac.Parameters.AddWithValue("@u", (object?)au.Url ?? DBNull.Value);
                        ac.Parameters.AddWithValue("@a", (object?)au.AvatarUrl ?? DBNull.Value);
                        ac.Parameters.AddWithValue("@e", (object?)au.EmailAddress ?? DBNull.Value);
                        ac.Parameters.AddWithValue("@aid", a.ArticleID);
                        ac.ExecuteNonQuery();
                    }
                }
                if (a.Attachments is not null)
                {
                    foreach (var att in a.Attachments)
                    {
                        using var attc = c.CreateCommand();
                        attc.CommandText = @"INSERT OR REPLACE INTO attachments (attachmentID, url, mimeType, title, sizeInBytes, durationInSeconds) VALUES (@i,@u,@m,@t,@s,@d);
                                             INSERT OR IGNORE INTO attachmentsLookup (attachmentID, articleID) VALUES (@i, @aid);";
                        attc.Parameters.AddWithValue("@i", att.AttachmentID);
                        attc.Parameters.AddWithValue("@u", (object?)att.URL ?? DBNull.Value);
                        attc.Parameters.AddWithValue("@m", (object?)att.MimeType ?? DBNull.Value);
                        attc.Parameters.AddWithValue("@t", (object?)att.Title ?? DBNull.Value);
                        attc.Parameters.AddWithValue("@s", (object?)att.SizeInBytes ?? DBNull.Value);
                        attc.Parameters.AddWithValue("@d", (object?)att.DurationInSeconds ?? DBNull.Value);
                        attc.Parameters.AddWithValue("@aid", a.ArticleID);
                        attc.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
            return Task.CompletedTask;
        });
        return newArticles;
    }

    public Task<HashSet<string>> FetchUnreadArticleIDsAsync(IEnumerable<string> feedIDs)
    {
        var ids = feedIDs.ToList();
        var result = new HashSet<string>();
        if (ids.Count == 0) return Task.FromResult(result);
        return Queue.RunAsync(c =>
        {
            var placeholders = string.Join(",", ids.Select((_, i) => $"@f{i}"));
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT a.articleID FROM articles a JOIN statuses s ON a.articleID = s.articleID WHERE a.feedID IN ({placeholders}) AND s.read = 0;";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
            return Task.FromResult(result);
        });
    }

    public Task<HashSet<string>> FetchStarredArticleIDsAsync(IEnumerable<string> feedIDs)
    {
        var ids = feedIDs.ToList();
        var result = new HashSet<string>();
        return Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            var where = ids.Count == 0
                ? "WHERE s.starred = 1"
                : "WHERE a.feedID IN (" + string.Join(",", ids.Select((_, i) => $"@f{i}")) + ") AND s.starred = 1";
            cmd.CommandText = $"SELECT a.articleID FROM articles a JOIN statuses s ON a.articleID = s.articleID {where};";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(r.GetString(0));
            return Task.FromResult(result);
        });
    }

    public Task MarkAsync(IEnumerable<string> articleIDs, ArticleStatus.Key key, bool value)
    {
        var ids = articleIDs.ToList();
        if (ids.Count == 0) return Task.CompletedTask;
        var column = key == ArticleStatus.Key.Read ? "read" : "starred";
        return Queue.RunAsync(c =>
        {
            var placeholders = string.Join(",", ids.Select((_, i) => $"@i{i}"));
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE statuses SET {column} = @v WHERE articleID IN ({placeholders});";
            cmd.Parameters.AddWithValue("@v", value ? 1 : 0);
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@i{i}", ids[i]);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        });
    }

    public Task<HashSet<Article>> SearchAsync(string query, IEnumerable<string>? feedIDs = null, int? limit = 100)
    {
        return Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            var feedFilter = "";
            var ids = feedIDs?.ToList() ?? new();
            if (ids.Count > 0)
                feedFilter = " AND a.feedID IN (" + string.Join(",", ids.Select((_, i) => $"@f{i}")) + ")";
            cmd.CommandText = @$"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                                 FROM search, articles a JOIN statuses s ON a.articleID = s.articleID
                                 WHERE search MATCH @q AND a.searchRowID = search.rowid{feedFilter}
                                 ORDER BY a.datePublished DESC LIMIT {limit ?? 100};";
            cmd.Parameters.AddWithValue("@q", query);
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            return Task.FromResult(ReadAll(cmd));
        });
    }

    /// <summary>
    /// Delete articles older than retentionDays that are not starred.
    /// Port of NNW's article retention cleanup.
    /// </summary>
    public async Task<int> CleanupOldArticlesAsync(int retentionDays = 90)
    {
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToUnixSeconds();
        return await Queue.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();

            // Find article IDs to delete: arrived before cutoff AND not starred
            using var findCmd = c.CreateCommand();
            findCmd.CommandText = @"
                SELECT s.articleID FROM statuses s
                WHERE s.dateArrived < @cutoff AND s.starred = 0;";
            findCmd.Parameters.AddWithValue("@cutoff", cutoff);

            var toDelete = new List<string>();
            using (var r = findCmd.ExecuteReader())
            {
                while (r.Read()) toDelete.Add(r.GetString(0));
            }

            if (toDelete.Count == 0)
            {
                tx.Commit();
                return Task.FromResult(0);
            }

            var placeholders = string.Join(",", toDelete.Select((_, i) => $"@d{i}"));

            // Delete from articles (triggers search cleanup via trigger)
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM articles WHERE articleID IN ({placeholders});";
                for (int i = 0; i < toDelete.Count; i++) cmd.Parameters.AddWithValue($"@d{i}", toDelete[i]);
                cmd.ExecuteNonQuery();
            }

            // Delete from lookup tables
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM authorsLookup WHERE articleID IN ({placeholders});";
                for (int i = 0; i < toDelete.Count; i++) cmd.Parameters.AddWithValue($"@d{i}", toDelete[i]);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM attachmentsLookup WHERE articleID IN ({placeholders});";
                for (int i = 0; i < toDelete.Count; i++) cmd.Parameters.AddWithValue($"@d{i}", toDelete[i]);
                cmd.ExecuteNonQuery();
            }

            // Delete from statuses
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM statuses WHERE articleID IN ({placeholders});";
                for (int i = 0; i < toDelete.Count; i++) cmd.Parameters.AddWithValue($"@d{i}", toDelete[i]);
                cmd.ExecuteNonQuery();
            }

            // Clean up orphaned authors (no remaining lookup references)
            c.Execute("DELETE FROM authors WHERE authorID NOT IN (SELECT DISTINCT authorID FROM authorsLookup);");

            // Clean up orphaned attachments (no remaining lookup references)
            c.Execute("DELETE FROM attachments WHERE attachmentID NOT IN (SELECT DISTINCT attachmentID FROM attachmentsLookup);");

            // Clean up orphaned statuses (statuses without matching articles)
            c.Execute("DELETE FROM statuses WHERE articleID NOT IN (SELECT articleID FROM articles);");

            tx.Commit();
            return Task.FromResult(toDelete.Count);
        });
    }

    public void Dispose() => Queue.Dispose();
}
