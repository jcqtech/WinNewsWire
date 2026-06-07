using WinNewsWire.SyncDatabase;

namespace WinNewsWire.Account;

/// <summary>Helpers for remote <see cref="IAccountDelegate"/>s flushing their
/// <see cref="Account.SyncDatabase"/> pending queue to a service.</summary>
public static class RemoteSyncHelpers
{
    /// <summary>
    /// Pulls the next batch of pending statuses from <paramref name="account"/>'s SyncDatabase,
    /// groups them by (key, flag), invokes <paramref name="send"/> per group, then deletes the
    /// successful entries. Failed groups are left selected=1 to be retried on the next pass —
    /// callers should call <see cref="SyncDatabaseStore.ResetAllSelectedAsync"/> on hard
    /// failures (e.g. auth expired) so they're picked up again.
    /// </summary>
    /// <param name="send">Invoked once per (key, flag) group with the article IDs.
    /// Should throw on failure — exceptions are caught and the group is skipped from
    /// the delete step.</param>
    public static async Task FlushPendingAsync(
        Account account,
        Func<SyncStatus.SyncKey, bool, IReadOnlyList<string>, CancellationToken, Task> send,
        CancellationToken ct = default)
    {
        var pending = await account.SyncDatabase.SelectForProcessingAsync();
        if (pending.Count == 0) return;

        var sentIDs = new List<string>();
        foreach (var group in pending.GroupBy(s => (s.Key, s.Flag)))
        {
            var ids = group.Select(g => g.ArticleID).ToList();
            try
            {
                await send(group.Key.Key, group.Key.Flag, ids, ct);
                sentIDs.AddRange(ids);
            }
            catch
            {
                // Leave this group as selected so SelectForProcessing skips it next call,
                // but we don't delete — operator can call ResetAllSelectedAsync to retry.
            }
        }
        if (sentIDs.Count > 0)
            await account.SyncDatabase.DeleteSelectedAsync(sentIDs);
    }
}
