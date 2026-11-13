using WinNewsWire.Articles;
using WinNewsWire.Parsers;
using WinNewsWire.Database;

namespace WinNewsWire.ArticlesDatabase;

public sealed partial class ArticlesDatabaseStore
{
    public Task<ArticleChanges> UpdateAsync(IEnumerable<ParsedItem> parsedItems, string feedID, bool deleteOlder = false)
    {
        var items = parsedItems.Distinct().ToList();
        if (items.Count == 0) return Task.FromResult(new ArticleChanges());
        return Queue.RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var existing = FetchArticles(connection, feedID).ToDictionary(a => a.ArticleID);
            var byUniqueId = existing.Values.ToLookup(article => article.UniqueID, StringComparer.Ordinal);
            var currentUniqueIds = items.Select(item => item.UniqueId).ToHashSet(StringComparer.Ordinal);
            var claimedIds = new HashSet<string>(StringComparer.Ordinal);
            var incoming = new List<Article>();
            var now = DateTime.UtcNow;
            foreach (var item in items)
            {
                var id = item.SyncServiceId ?? Article.CalculatedArticleID(feedID, item.UniqueId);
                existing.TryGetValue(id, out var previous);
                if (item.SyncServiceId is null && previous is null)
                {
                    previous = FindExistingIdentity(byUniqueId, item.UniqueId);
                    if (previous is null && item.LegacyUniqueId is { } legacy && !currentUniqueIds.Contains(legacy))
                        previous = FindExistingIdentity(byUniqueId, legacy);
                    if (previous is not null)
                    {
                        id = previous.ArticleID;
                    }
                }
                if (!claimedIds.Add(id))
                    throw new InvalidDataException($"Multiple parsed items claim the same article '{id}'.");
                var status = previous?.Status ?? _statuses.EnsureStatus(connection, new ArticleStatus(id,
                    read: item.DatePublished < now.AddSeconds(-ArticleStatus.StaleIntervalSeconds),
                    starred: false, dateArrived: now));
                var published = item.DatePublished ?? item.DateModified;
                if (published > now.AddDays(1)) published = null;
                var modified = item.DateModified > now.AddDays(1) ? null : item.DateModified;
                published = DatabaseDate(published);
                modified = DatabaseDate(modified);
                var authors = item.Authors?.Select(a => Author.Create(null, a.Name, a.Url, a.AvatarUrl, a.EmailAddress))
                    .OfType<Author>().ToHashSet();
                var attachments = item.Attachments?.Select(a => Attachment.Create(null, a.Url, a.MimeType, a.Title, a.SizeInBytes, a.DurationInSeconds,
                    a.Width, a.Height, a.Medium switch
                    {
                        ParsedAttachment.AttachmentMedium.Image => Attachment.AttachmentMedium.Image,
                        ParsedAttachment.AttachmentMedium.Audio => Attachment.AttachmentMedium.Audio,
                        ParsedAttachment.AttachmentMedium.Video => Attachment.AttachmentMedium.Video,
                        ParsedAttachment.AttachmentMedium.Document => Attachment.AttachmentMedium.Document,
                        ParsedAttachment.AttachmentMedium.Executable => Attachment.AttachmentMedium.Executable,
                        _ => (Attachment.AttachmentMedium?)null
                    }))
                    .OfType<Attachment>().ToHashSet();
                incoming.Add(new Article(AccountID, id, feedID, item.UniqueId,
                    item.Title, item.ContentHtml, item.ContentText, item.Markdown, item.Url, item.ExternalUrl,
                    item.Summary, item.ImageUrl, published ?? previous?.DatePublished, modified ?? previous?.DateModified,
                    authors is { Count: > 0 } ? authors : null, attachments is { Count: > 0 } ? attachments : null, status));
            }

            var added = incoming.Where(a => !existing.ContainsKey(a.ArticleID)).ToHashSet();
            var updated = incoming.Where(a => existing.TryGetValue(a.ArticleID, out var previous) && !a.Equals(previous)).ToHashSet();
            UpsertArticles(connection, added.Concat(updated).ToList(), ensureStatuses: false);

            var incomingIDs = incoming.Select(a => a.ArticleID).ToHashSet();
            var deleted = deleteOlder
                ? existing.Values.Where(a => !a.Status.Starred && a.Status.DateArrived < now.AddDays(-30)
                    && !incomingIDs.Contains(a.ArticleID)).ToHashSet()
                : new HashSet<Article>();
            foreach (var batch in deleted.Select(a => a.ArticleID).Chunk(500))
            {
                using var command = connection.CreateCommand();
                var placeholders = string.Join(",", batch.Select((_, i) => $"@id{i}"));
                command.CommandText = $@"DELETE FROM articles WHERE articleID IN ({placeholders});
                    DELETE FROM authorsLookup WHERE articleID IN ({placeholders});
                    DELETE FROM attachmentsLookup WHERE articleID IN ({placeholders});";
                for (int i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"@id{i}", batch[i]);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            InvalidateArticlesCache(added.Concat(updated).ToArray());
            foreach (var article in deleted) _articlesCache.TryRemove(article.ArticleID, out _);
            return Task.FromResult(new ArticleChanges(
                added.Count > 0 ? added : null, updated.Count > 0 ? updated : null, deleted.Count > 0 ? deleted : null));
        });
    }

    private static DateTime? DatabaseDate(DateTime? date)
        => date is null ? null : DateTime.UnixEpoch.AddSeconds(date.Value.ToUnixTimestamp());

    private static Article? FindExistingIdentity(ILookup<string, Article> articles, string uniqueId)
    {
        using var candidates = articles[uniqueId].GetEnumerator();
        if (!candidates.MoveNext()) return null;
        var article = candidates.Current;
        if (candidates.MoveNext()) throw new InvalidDataException($"Multiple stored articles have the same feed-scoped identity '{uniqueId}'.");
        return article;
    }
}
