using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using WinNewsWire.Articles;
using WinNewsWire.Database;

namespace WinNewsWire.ArticlesDatabase;

/// <summary>
/// Port of <c>ArticlesDatabase</c>. Provides storage and retrieval of Article/ArticleStatus/Author
/// tuples backed by SQLite (FTS4 for search). All writes are serialized via DatabaseQueue.
/// </summary>
public sealed partial class ArticlesDatabaseStore : IDisposable
{
    private readonly StatusesTable _statuses = new();
    private readonly ConcurrentDictionary<string, Article> _articlesCache = new(StringComparer.Ordinal);
    public string AccountID { get; }
    public DatabaseQueue Queue { get; }

    public ArticlesDatabaseStore(string filePath, string accountID)
    {
        AccountID = accountID;
        Queue = new DatabaseQueue(filePath);
        try
        {
            Queue.Run(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = Constants.CreateStatements;
                cmd.ExecuteNonQuery();
                EnsureSearchIndex(c);
            });
        }
        catch { Queue.Dispose(); throw; }
    }

    private void EnsureSearchIndex(SqliteConnection connection)
    {
        while (true)
        {
            using var query = connection.CreateCommand();
            query.CommandText = @"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                FROM articles a JOIN statuses s ON a.articleID = s.articleID
                WHERE a.searchRowID IS NULL OR NOT EXISTS (SELECT 1 FROM search WHERE rowid = a.searchRowID)
                LIMIT 500;";
            var articles = ReadAll(query);
            if (articles.Count == 0) return;
            using var transaction = connection.BeginTransaction();
            foreach (var article in articles) SearchTable.IndexArticle(connection, article);
            transaction.Commit();
        }
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
        => Queue.RunAsync(c => Task.FromResult(FetchArticles(c, feedID)));

    public Task<HashSet<Article>> FetchArticlesByIDsAsync(IEnumerable<string> articleIDs)
    {
        var ids = articleIDs.Distinct(StringComparer.Ordinal).ToArray();
        return Queue.RunAsync(c => Task.FromResult(FetchArticlesByIDs(c, ids)));
    }

    public Task<HashSet<Article>> ApplyExternalChangesAsync(IEnumerable<string> articleIDs)
    {
        var ids = articleIDs.Distinct(StringComparer.Ordinal).ToArray();
        return Queue.RunAsync(connection =>
        {
            foreach (var id in ids) _articlesCache.TryRemove(id, out _);
            var changed = FetchArticlesByIDs(connection, ids);
            InvalidateArticlesCache(changed);
            foreach (var article in changed) _articlesCache[article.ArticleID] = article;
            return Task.FromResult(changed);
        });
    }

    private HashSet<Article> FetchArticlesByIDs(SqliteConnection connection, IEnumerable<string> ids)
    {
        var result = new HashSet<Article>();
        foreach (var chunk in ids.Chunk(500))
        {
            using var command = connection.CreateCommand();
            var parameters = string.Join(",", chunk.Select((_, i) => $"@id{i}"));
            command.CommandText = $@"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown,
                a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                FROM articles a JOIN statuses s ON a.articleID = s.articleID WHERE a.articleID IN ({parameters});";
            for (int i = 0; i < chunk.Length; i++) command.Parameters.AddWithValue($"@id{i}", chunk[i]);
            result.UnionWith(ReadAll(command));
        }
        return result;
    }

    public void RemapFeedIDs(IReadOnlyDictionary<string, string> replacements)
    {
        if (replacements.Count > 0) Queue.Run(c => RemapFeedIDs(c, replacements));
    }

    public Task RemapFeedIDsAsync(IReadOnlyDictionary<string, string> replacements)
        => replacements.Count == 0 ? Task.CompletedTask : Queue.RunAsync(c =>
        {
            RemapFeedIDs(c, replacements);
            return Task.CompletedTask;
        });

    private void RemapFeedIDs(SqliteConnection connection, IReadOnlyDictionary<string, string> replacements)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var (oldID, newID) in replacements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE articles SET feedID = @new WHERE feedID = @old;";
            command.Parameters.AddWithValue("@old", oldID);
            command.Parameters.AddWithValue("@new", newID);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        _articlesCache.Clear();
    }

    private HashSet<Article> FetchArticles(SqliteConnection connection, string feedID)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                                 FROM articles a JOIN statuses s ON a.articleID = s.articleID WHERE a.feedID = @f;";
        cmd.Parameters.AddWithValue("@f", feedID);
        return ReadAll(cmd);
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
                         WHERE a.feedID IN ({placeholders})";
            // Unbounded results are sets; sorting full HTML rows would inflate SQLite's native working set.
            if (limit is not null) sql += $" ORDER BY a.datePublished DESC LIMIT {limit.Value}";
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
                         WHERE a.feedID IN ({placeholders}) AND s.read = 0";
            if (limit is not null) sql += $" ORDER BY a.datePublished DESC LIMIT {limit.Value}";
            cmd.CommandText = sql + ";";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            return Task.FromResult(ReadAll(cmd));
        });
    }

    public Task<HashSet<Article>> FetchStarredAsync(IEnumerable<string> feedIDs, int? limit = null)
    {
        var ids = feedIDs.ToList();
        if (ids.Count == 0) return Task.FromResult(new HashSet<Article>());
        return Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            var where = ids.Count == 0 ? "" : "WHERE a.feedID IN (" + string.Join(",", ids.Select((_, i) => $"@f{i}")) + ") AND ";
            var sql = @$"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                         FROM articles a JOIN statuses s ON a.articleID = s.articleID
                         {(where.Length == 0 ? "WHERE " : where)} s.starred = 1";
            if (limit is not null) sql += $" ORDER BY a.datePublished DESC LIMIT {limit.Value}";
            cmd.CommandText = sql + ";";
            for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
            return Task.FromResult(ReadAll(cmd));
        });
    }

    private HashSet<Article> ReadAll(SqliteCommand cmd)
    {
        var set = new HashSet<Article>();
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
                var status = _statuses.Get(
                    articleID,
                    r.GetInt32(13) != 0,
                    r.GetInt32(14) != 0,
                    r.GetUnixTime(15));
                if (_articlesCache.TryGetValue(articleID, out var cached) && ReferenceEquals(cached.Status, status))
                {
                    set.Add(cached);
                    continue;
                }
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
                    r.GetUnixTimeOrNull(11),
                    r.GetUnixTimeOrNull(12),
                    status));
            }
        }

        Dictionary<string, HashSet<Author>>? authorsByArticle = null;
        Dictionary<string, HashSet<Attachment>>? attachmentsByArticle = null;
        if (staged.Count > 0 && cmd.Connection is { } conn)
        {
            authorsByArticle = new Dictionary<string, HashSet<Author>>();
            attachmentsByArticle = new Dictionary<string, HashSet<Attachment>>();
            foreach (var batch in staged.Chunk(500))
            {
                var placeholders = string.Join(",", batch.Select((_, i) => $"@a{i}"));
                using (var ac = conn.CreateCommand())
                {
                    ac.CommandText = $@"SELECT al.articleID, a.authorID, a.name, a.url, a.avatarURL, a.emailAddress
                                        FROM authorsLookup al JOIN authors a ON al.authorID = a.authorID
                                        WHERE al.articleID IN ({placeholders});";
                    for (int i = 0; i < batch.Length; i++)
                        ac.Parameters.AddWithValue($"@a{i}", batch[i].articleID);
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
                }
                using var attCmd = conn.CreateCommand();
                attCmd.CommandText = $@"SELECT al.articleID, a.attachmentID, a.url, a.mimeType, a.title, a.sizeInBytes, a.durationInSeconds
                                        FROM attachmentsLookup al JOIN attachments a ON al.attachmentID = a.attachmentID
                                        WHERE al.articleID IN ({placeholders});";
                for (int i = 0; i < batch.Length; i++)
                    attCmd.Parameters.AddWithValue($"@a{i}", batch[i].articleID);
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
                        attR.IsDBNull(6) ? null : (long?)attR.GetInt64(6));
                    if (attachment is null) continue;
                    if (!attachmentsByArticle.TryGetValue(aid, out var setForAttachment))
                        attachmentsByArticle[aid] = setForAttachment = new HashSet<Attachment>();
                    setForAttachment.Add(attachment);
                }
            }
        }

        foreach (var s in staged)
        {
            HashSet<Author>? authors = null;
            authorsByArticle?.TryGetValue(s.articleID, out authors);
            HashSet<Attachment>? attachments = null;
            attachmentsByArticle?.TryGetValue(s.articleID, out attachments);
            var article = new Article(
                AccountID, s.articleID, s.feedID, s.uniqueID,
                s.title, s.contentHtml, s.contentText, s.markdown,
                s.url, s.externalURL, s.summary, s.imageURL,
                s.datePublished, s.dateModified,
                authors, attachments, s.status);
            _articlesCache[s.articleID] = article;
            set.Add(article);
        }
        return set;
    }

    public Task<IReadOnlyList<Article>> UpdateArticlesAsync(IEnumerable<Article> articles)
    {
        var list = articles.ToList();
        if (list.Count == 0) return Task.FromResult<IReadOnlyList<Article>>(Array.Empty<Article>());
        return Queue.RunAsync(c =>
        {
            using var tx = c.BeginTransaction();
            var inserted = UpsertArticles(c, list);
            tx.Commit();
            InvalidateArticlesCache(list);
            return Task.FromResult(inserted);
        });
    }

    private IReadOnlyList<Article> UpsertArticles(SqliteConnection c, IReadOnlyList<Article> list, bool ensureStatuses = true)
    {
            var newArticles = new List<Article>();
            if (list.Count == 0) return newArticles;
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
                var status = ensureStatuses ? _statuses.EnsureStatus(c, a.Status) : a.Status;
                if (!existing.Contains(a.ArticleID)) newArticles.Add(WithStatus(a, status));
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO articles
                        (articleID, feedID, uniqueID, title, contentHTML, contentText, markdown, url, externalURL, summary, imageURL, bannerImageURL, datePublished, dateModified, searchRowID)
                        VALUES (@articleID, @feedID, @uniqueID, @title, @contentHTML, @contentText, @markdown, @url, @externalURL, @summary, @imageURL, NULL, @datePublished, @dateModified, NULL)
                        ON CONFLICT(articleID) DO UPDATE SET
                            feedID = excluded.feedID, uniqueID = excluded.uniqueID, title = excluded.title,
                            contentHTML = excluded.contentHTML, contentText = excluded.contentText, markdown = excluded.markdown,
                            url = excluded.url, externalURL = excluded.externalURL, summary = excluded.summary,
                            imageURL = excluded.imageURL,
                            datePublished = COALESCE(excluded.datePublished, articles.datePublished),
                            dateModified = COALESCE(excluded.dateModified, articles.dateModified);";
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
                    cmd.Parameters.AddWithValue("@datePublished", (object?)a.DatePublished?.ToUnixTimestamp() ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@dateModified", (object?)a.DateModified?.ToUnixTimestamp() ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
                using (var clear = c.CreateCommand())
                {
                    clear.CommandText = "DELETE FROM authorsLookup WHERE articleID = @id; DELETE FROM attachmentsLookup WHERE articleID = @id;";
                    clear.Parameters.AddWithValue("@id", a.ArticleID);
                    clear.ExecuteNonQuery();
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
                SearchTable.IndexArticle(c, a);
            }
        return newArticles;
    }

    private void InvalidateArticlesCache(IReadOnlyCollection<Article> articles)
    {
        if (articles.Count == 0) return;
        foreach (var article in articles) _articlesCache.TryRemove(article.ArticleID, out _);
        var authors = articles.SelectMany(article => article.Authors ?? Enumerable.Empty<Author>()).GroupBy(author => author.AuthorID)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        var attachments = articles.SelectMany(article => article.Attachments ?? Enumerable.Empty<Attachment>()).GroupBy(attachment => attachment.AttachmentID)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        if (authors.Count == 0 && attachments.Count == 0) return;
        Func<Author, bool> changedAuthor = old => authors.TryGetValue(old.AuthorID, out var next)
            && (old.Name, old.Url, old.AvatarUrl, old.EmailAddress) != (next.Name, next.Url, next.AvatarUrl, next.EmailAddress);
        Func<Attachment, bool> changedAttachment = old => attachments.TryGetValue(old.AttachmentID, out var next)
            && (old.URL, old.MimeType, old.Title, old.SizeInBytes, old.DurationInSeconds)
                != (next.URL, next.MimeType, next.Title, next.SizeInBytes, next.DurationInSeconds);
        foreach (var (id, cached) in _articlesCache)
        {
            if (authors.Count > 0 && AnyChanged(cached.Authors, changedAuthor)
                || attachments.Count > 0 && AnyChanged(cached.Attachments, changedAttachment))
                _articlesCache.TryRemove(id, out _);
        }
    }

    private static bool AnyChanged<T>(IReadOnlySet<T>? values, Func<T, bool> changed)
    {
        if (values is HashSet<T> set)
        {
            foreach (var value in set)
                if (changed(value)) return true;
        }
        else if (values is not null)
        {
            foreach (var value in values)
                if (changed(value)) return true;
        }
        return false;
    }

    private static Article WithStatus(Article article, ArticleStatus status)
        => new(article.AccountID, article.ArticleID, article.FeedID, article.UniqueID, article.Title,
            article.ContentHtml, article.ContentText, article.Markdown, article.RawLink, article.RawExternalLink,
            article.Summary, article.RawImageLink, article.DatePublished, article.DateModified, article.Authors,
            article.Attachments, status);

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

    public Task<HashSet<string>> FetchExistingArticleIDsAsync(IEnumerable<string> articleIDs)
        => FetchExistingIDsAsync(articleIDs, "articles");

    public Task<HashSet<string>> FetchExistingStatusIDsAsync(IEnumerable<string> articleIDs)
        => FetchExistingIDsAsync(articleIDs, "statuses");

    private Task<HashSet<string>> FetchExistingIDsAsync(IEnumerable<string> articleIDs, string table)
    {
        var ids = articleIDs.Distinct().ToList();
        return Queue.RunAsync(c =>
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in ids.Chunk(500))
            {
                using var command = c.CreateCommand();
                command.CommandText = $"SELECT articleID FROM {table} WHERE articleID IN ({string.Join(",", batch.Select((_, i) => $"@id{i}"))});";
                for (int i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"@id{i}", batch[i]);
                using var rows = command.ExecuteReader();
                while (rows.Read()) result.Add(rows.GetString(0));
            }
            return Task.FromResult(result);
        });
    }

    public Task<HashSet<string>> FetchStarredArticleIDsAsync(IEnumerable<string> feedIDs)
    {
        var ids = feedIDs.ToList();
        var result = new HashSet<string>();
        if (ids.Count == 0) return Task.FromResult(result);
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

    public Task<HashSet<string>> MarkAsync(IEnumerable<string> articleIDs, ArticleStatus.Key key, bool value)
    {
        var ids = articleIDs.Distinct().ToList();
        if (ids.Count == 0) return Task.FromResult(new HashSet<string>());
        if (!Enum.IsDefined(key)) throw new ArgumentOutOfRangeException(nameof(key));
        return Queue.RunAsync(c => Task.FromResult(_statuses.Mark(c, ids, key, value)));
    }

    public Task CreateStatusesIfNeededAsync(IEnumerable<string> articleIDs, bool defaultRead = true)
    {
        var ids = articleIDs.Distinct(StringComparer.Ordinal).ToArray();
        return Queue.RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var batch in ids.Chunk(500))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO statuses (articleID, read, starred, dateArrived) VALUES "
                    + string.Join(",", batch.Select((_, i) => $"(@id{i}, @read, 0, @arrived)")) + ";";
                for (int i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"@id{i}", batch[i]);
                command.Parameters.AddWithValue("@read", defaultRead);
                command.Parameters.AddWithValue("@arrived", DateTime.UtcNow.ToUnixTimestamp());
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return Task.CompletedTask;
        });
    }

    public Task<HashSet<string>> FetchMissingArticleIDsAsync(DateTime cutoff)
        => Queue.RunAsync(connection =>
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT s.articleID FROM statuses s LEFT JOIN articles a ON a.articleID = s.articleID
                WHERE a.articleID IS NULL AND (s.dateArrived >= @cutoff OR s.starred = 1);";
            command.Parameters.AddWithValue("@cutoff", cutoff.ToUnixTimestamp());
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return Task.FromResult(result);
        });

    public Task<HashSet<string>> FetchStatusArticleIDsAsync(ArticleStatus.Key key, bool value)
    {
        if (!Enum.IsDefined(key)) throw new ArgumentOutOfRangeException(nameof(key));
        return Queue.RunAsync(connection =>
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT articleID FROM statuses WHERE {(key == ArticleStatus.Key.Read ? "read" : "starred")} = @value;";
            command.Parameters.AddWithValue("@value", value);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return Task.FromResult(result);
        });
    }

    public Task RepairStatusesAsync()
        => Queue.RunAsync(c =>
        {
            _statuses.Repair(c);
            return Task.CompletedTask;
        });

    public void EmptyCaches()
    {
        EmptyArticleCache();
        _statuses.EmptyCache();
    }

    public void EmptyArticleCache() => _articlesCache.Clear();
    public void TrimArticleCache(IReadOnlySet<string> retainedIDs)
    {
        foreach (var (id, _) in _articlesCache)
            if (!retainedIDs.Contains(id)) _articlesCache.TryRemove(id, out _);
    }

    public Task EmptyArticleCacheAsync() => Queue.RunAsync(_ =>
    {
        _articlesCache.Clear();
        return Task.CompletedTask;
    });

    public Task<HashSet<Article>> FetchTodayArticlesAsync(IEnumerable<string> feedIDs, int? limit = null)
    {
        var ids = feedIDs.Distinct().ToList();
        if (ids.Count == 0) return Task.FromResult(new HashSet<Article>());
        ArgumentOutOfRangeException.ThrowIfNegative(limit ?? 0);
        return Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $@"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                FROM articles a JOIN statuses s ON a.articleID = s.articleID
                WHERE a.feedID IN ({string.Join(",", ids.Select((_, i) => $"@f{i}"))})
                AND COALESCE(a.datePublished, s.dateArrived) > @cutoff";
            AddTodayParameters(cmd, ids);
            if (limit is not null)
            {
                cmd.CommandText += " ORDER BY COALESCE(a.datePublished, s.dateArrived) DESC LIMIT @limit";
                cmd.Parameters.AddWithValue("@limit", limit.Value);
            }
            return Task.FromResult(ReadAll(cmd));
        });
    }

    public Task<int> FetchUnreadCountForTodayAsync(IEnumerable<string> feedIDs)
        => CountTodayAsync(feedIDs, unreadOnly: true);

    public Task<int> FetchTodayArticlesCountAsync(IEnumerable<string> feedIDs)
        => CountTodayAsync(feedIDs, unreadOnly: false);

    private Task<int> CountTodayAsync(IEnumerable<string> feedIDs, bool unreadOnly)
    {
        var ids = feedIDs.Distinct().ToList();
        if (ids.Count == 0) return Task.FromResult(0);
        return Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $@"SELECT COUNT(*) FROM articles a JOIN statuses s ON a.articleID = s.articleID
                WHERE a.feedID IN ({string.Join(",", ids.Select((_, i) => $"@f{i}"))})
                AND COALESCE(a.datePublished, s.dateArrived) > @cutoff";
            if (unreadOnly) cmd.CommandText += " AND s.read = 0";
            AddTodayParameters(cmd, ids);
            return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar()));
        });
    }

    private static void AddTodayParameters(SqliteCommand cmd, IReadOnlyList<string> ids)
    {
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddHours(-24).ToUnixTimestamp());
        for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@f{i}", ids[i]);
    }

    public Task<HashSet<Article>> SearchAsync(string query, IEnumerable<string>? feedIDs = null, int? limit = null)
    {
        var searchString = SearchTable.SqliteSearchString(query);
        var ids = feedIDs?.Distinct().ToList();
        if (searchString.Length == 0 || ids is { Count: 0 }) return Task.FromResult(new HashSet<Article>());
        ArgumentOutOfRangeException.ThrowIfNegative(limit ?? 0);
        return Queue.RunAsync(c =>
        {
            using var cmd = c.CreateCommand();
            var feedFilter = "";
            if (ids is not null)
                feedFilter = " AND a.feedID IN (" + string.Join(",", ids.Select((_, i) => $"@f{i}")) + ")";
            cmd.CommandText = @$"SELECT a.articleID, a.feedID, a.uniqueID, a.title, a.contentHTML, a.contentText, a.markdown, a.url, a.externalURL, a.summary, a.imageURL, a.datePublished, a.dateModified, s.read, s.starred, s.dateArrived
                                 FROM search, articles a JOIN statuses s ON a.articleID = s.articleID
                                 WHERE search MATCH @q AND a.searchRowID = search.rowid{feedFilter}";
            if (limit is not null)
            {
                cmd.CommandText += " ORDER BY COALESCE(a.datePublished, a.dateModified, s.dateArrived) DESC LIMIT @limit";
                cmd.Parameters.AddWithValue("@limit", limit.Value);
            }
            cmd.Parameters.AddWithValue("@q", searchString);
            if (ids is not null)
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
            _statuses.Remove(toDelete);
            foreach (var id in toDelete) _articlesCache.TryRemove(id, out _);
            return Task.FromResult(toDelete.Count);
        });
    }

    public void Dispose()
    {
        Queue.Dispose();
        _articlesCache.Clear();
    }
}
