using WinNewsWire.SyncDatabase;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WinNewsWire.Articles;

namespace WinNewsWire.Account;

/// <summary>Helpers for remote <see cref="IAccountDelegate"/>s flushing their
/// <see cref="Account.SyncDatabase"/> pending queue to a service.</summary>
public static class RemoteSyncHelpers
{
    private static readonly ConditionalWeakTable<Account, SemaphoreSlim> Gates = new();

    /// <summary>
    /// Pulls the next batch of pending statuses from <paramref name="account"/>'s SyncDatabase,
    /// groups them by (key, flag), invokes <paramref name="send"/> per group, then deletes the
    /// successful (article ID, key) pairs. Failed groups remain queued for retry.
    /// </summary>
    /// <param name="send">Invoked once per (key, flag) group with the article IDs.
    /// Should throw on failure — exceptions are caught and the group is skipped from
    /// the delete step.</param>
    public static async Task<int> FlushPendingAsync(
        Account account,
        Func<SyncStatus.SyncKey, bool, IReadOnlyList<string>, CancellationToken, Task> send,
        CancellationToken ct = default,
        Func<SyncStatus.SyncKey, bool, int>? batchSize = null)
    {
        var gate = Gates.GetValue(account, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var pending = await account.SyncDatabase.SelectForProcessingAsync();
            int sent = 0;
            try
            {
                var errors = new List<Exception>();
                bool rateLimited = false;
                foreach (var group in pending.GroupBy(s => (s.Key, s.Flag)))
                {
                    int limit = batchSize?.Invoke(group.Key.Key, group.Key.Flag) ?? 100;
                    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
                    var batches = group.Select(g => g.ArticleID).Chunk(limit).ToList();
                    for (int i = 0; i < batches.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            await send(group.Key.Key, group.Key.Flag, batches[i], ct);
                            await account.SyncDatabase.DeleteSelectedAsync(batches[i], group.Key.Key);
                            sent += batches[i].Length;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException
                            or InvalidOperationException or System.Text.Json.JsonException or FormatException
                            or Secrets.CredentialsException)
                        {
                            Trace.TraceError($"Could not sync {group.Key.Key} statuses for {account.AccountID}: {ex}");
                            errors.Add(ex);
                            rateLimited = ex is SyncRateLimitException
                                or HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
                            break;
                        }
                    }
                    if (rateLimited) break;
                }
                if (errors.Count > 0) throw new ArticleStatusSyncException(errors);
                return sent;
            }
            finally
            {
                foreach (var group in pending.GroupBy(status => status.Key))
                    await account.SyncDatabase.ResetSelectedAsync(group.Select(status => status.ArticleID), group.Key);
            }
        }
        finally { gate.Release(); }
    }

    public static async Task SendBeforeRefreshAsync(Account account, CancellationToken ct)
    {
        try { await account.Delegate.SendArticleStatusAsync(account, ct); }
        catch (ArticleStatusSyncException ex)
        {
            Trace.TraceError($"Continuing article refresh with unsent status changes for {account.AccountID}: {ex}");
        }
    }

    public static async Task<(int UnreadAdded, int UnreadRemoved, int StarredAdded, int StarredRemoved)> ReconcileStatusesAsync(
        Account account, IReadOnlySet<string>? serverUnread, IReadOnlySet<string>? serverStarred,
        bool unreadIsComplete = true, bool starredIsComplete = true)
    {
        int unreadAdded = 0, unreadRemoved = 0, starredAdded = 0, starredRemoved = 0;
        await account.StatusMutationGate.WaitAsync();
        try
        {
            if (serverUnread is not null)
            {
                await account.Database.CreateStatusesIfNeededAsync(serverUnread);
                var pendingRead = await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Read);
                unreadAdded = (await account.Database.MarkAsync(serverUnread.Except(pendingRead), ArticleStatus.Key.Read, false)).Count;
                if (unreadIsComplete)
                {
                    var localUnread = await account.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Read, false);
                    unreadRemoved = (await account.Database.MarkAsync(localUnread.Except(serverUnread).Except(pendingRead), ArticleStatus.Key.Read, true)).Count;
                }
            }
            if (serverStarred is not null)
            {
                await account.Database.CreateStatusesIfNeededAsync(serverStarred);
                var pendingStarred = await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Starred);
                starredAdded = (await account.Database.MarkAsync(serverStarred.Except(pendingStarred), ArticleStatus.Key.Starred, true)).Count;
                if (starredIsComplete)
                {
                    var localStarred = await account.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Starred, true);
                    starredRemoved = (await account.Database.MarkAsync(localStarred.Except(serverStarred).Except(pendingStarred), ArticleStatus.Key.Starred, false)).Count;
                }
            }
        }
        finally { account.StatusMutationGate.Release(); }
        return (unreadAdded, unreadRemoved, starredAdded, starredRemoved);
    }

    internal static Article ArticleFromParsedItem(Account account, Parsers.ParsedItem item, string feedID, bool defaultRead)
    {
        var id = item.SyncServiceId ?? Article.CalculatedArticleID(feedID, item.UniqueId);
        return new(account.AccountID, id, feedID, item.UniqueId, item.Title, item.ContentHtml, item.ContentText, item.Markdown,
            item.Url, item.ExternalUrl, item.Summary, item.ImageUrl, item.DatePublished, item.DateModified,
            item.Authors?.Select(a => Author.Create(null, a.Name, a.Url, a.AvatarUrl, a.EmailAddress)).OfType<Author>().ToHashSet(),
            item.Attachments?.Select(a => Attachment.Create(null, a.Url, a.MimeType, a.Title, a.SizeInBytes, a.DurationInSeconds,
                a.Width, a.Height, a.Medium switch
                {
                    Parsers.ParsedAttachment.AttachmentMedium.Image => Attachment.AttachmentMedium.Image,
                    Parsers.ParsedAttachment.AttachmentMedium.Audio => Attachment.AttachmentMedium.Audio,
                    Parsers.ParsedAttachment.AttachmentMedium.Video => Attachment.AttachmentMedium.Video,
                    Parsers.ParsedAttachment.AttachmentMedium.Document => Attachment.AttachmentMedium.Document,
                    Parsers.ParsedAttachment.AttachmentMedium.Executable => Attachment.AttachmentMedium.Executable,
                    _ => (Attachment.AttachmentMedium?)null
                })).OfType<Attachment>().ToArray(),
            new ArticleStatus(id, defaultRead, false, DateTime.UtcNow), item.ThumbnailUrl);
    }
}

public sealed class ArticleStatusSyncException(IEnumerable<Exception> errors)
    : AggregateException("Some article status changes could not be sent and remain queued for retry.", errors);
