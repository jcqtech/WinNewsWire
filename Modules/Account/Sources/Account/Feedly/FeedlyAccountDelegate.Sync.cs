using System.Diagnostics;
using System.Text.Json;
using WinNewsWire.Account;
using WinNewsWire.Core;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.Feedly;

public sealed partial class FeedlyAccountDelegate
{
    internal const int MaxStreamPageCount = 40;
    internal const int MaxArticleDownloadChunksPerSync = 20;
    private const int ArticleDownloadChunkSize = 1000;
    private const int StreamFetchDaysLimit = 90;
    private static readonly TimeSpan MinimumFeedRefreshInterval = TimeSpan.FromHours(24.5);
    private sealed record StreamIDs(HashSet<string> IDs, bool Truncated);
    private readonly object _statusSyncGate = new();
    private DateTimeOffset? _lastNoChangeSyncDate;
    private long _queuedStatusVersion;

    public void ArticleStatusesQueued()
    {
        lock (_statusSyncGate)
        {
            _lastNoChangeSyncDate = null;
            _queuedStatusVersion++;
        }
    }

    public async Task<bool> SyncArticleStatusAsync(Account.Account account, CancellationToken ct = default)
    {
        if (_api.RateLimiter.ShouldSkip()) return false;
        long version;
        lock (_statusSyncGate)
        {
            if (_lastNoChangeSyncDate is { } last && _clock.GetUtcNow() - last < TimeSpan.FromMinutes(30)) return false;
            version = _queuedStatusVersion;
        }
        try
        {
            var sent = await SendArticleStatusReturningCountAsync(account, ct);
            var counts = await RefreshStatusReturningCountsAsync(account, ct, includeStarred: false);
            var changed = sent > 0 || counts.TotalChanged > 0;
            lock (_statusSyncGate)
                _lastNoChangeSyncDate = !changed && version == _queuedStatusVersion ? _clock.GetUtcNow() : null;
            return changed;
        }
        catch (Exception error) when (_api.RateLimiter.IsRateLimitError(error))
        {
            if (!_api.RateLimiter.ShouldSkip()) _api.RateLimiter.NoteRateLimited(error);
            return false;
        }
    }

    public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.RefreshAll, () => RefreshAllCoreAsync(account, progress, ct), successMessage: RefreshAllMessage);

    private async Task<RefreshAllSummary> RefreshAllCoreAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (AccessToken is null && !await ReauthorizeAsync(ct))
            throw new CredentialsException("Sign in to Feedly before refreshing this account.");
        _api.RateLimiter.ThrowIfLimited();
        var start = DateTime.UtcNow;
        int sent = 0;
        try { sent = await SendArticleStatusReturningCountAsync(account, ct); }
        catch (ArticleStatusSyncException error)
        {
            if (_api.RateLimiter.IsRateLimitError(error)) throw;
            Trace.TraceError($"Continuing Feedly refresh with unsent statuses: {error.Message}");
        }
        var feedChanges = await RefreshFeedListAsync(account, ct);

        var user = AccessToken?.Username ?? throw new CredentialsException("Feedly did not return an access token.");
        var previousFetch = account.Settings.LastArticleFetchStartTime;
        var cutoff = start.AddDays(-StreamFetchDaysLimit);
        var since = previousFetch is { } last && last > cutoff ? last : cutoff;
        var all = await account.LogActivityAsync(ActivityKind.FetchArticleIDs,
            () => CollectStreamIDsAsync(FeedlyResourceIds.GlobalAllFor(user), null, since, ct),
            detail: "All articles", successMessage: result => $"{result.IDs.Count} article IDs");
        await account.Database.CreateStatusesIfNeededAsync(all.IDs);
        var statusChanges = await RefreshStatusReturningCountsAsync(account, ct);

        var updated = account.Settings.PendingArticleRefreshIDs.ToHashSet(StringComparer.Ordinal);
        if (previousFetch is not null) updated.UnionWith(all.IDs);
        account.Settings.PendingArticleRefreshIDs = updated.ToArray();
        var missing = await account.Database.FetchMissingArticleIDsAsync(cutoff);
        var download = updated.Concat(missing.Except(updated)).ToList();
        var downloaded = await DownloadEntriesAsync(account, download, ct);
        var individual = await RefreshIndividualFeedsAsync(account, ct);
        if (!all.Truncated)
            account.Settings.LastArticleFetchStartTime = start.AddMinutes(-5);
        account.Settings.LastRefreshCompletedDate = DateTime.UtcNow;
        await account.RecalculateUnreadCountsAsync();
        progress?.Report(new ProgressInfo(0, 1, 1));
        return new()
        {
            StatusesSent = sent, FeedListChanges = feedChanges, StatusRefreshCounts = statusChanges,
            ArticlesDownloaded = downloaded, NewArticlesFromFeedRefresh = individual,
        };
    }

    public Task RefreshArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => RefreshStatusReturningCountsAsync(account, ct);

    private Task<StatusRefreshCounts> RefreshStatusReturningCountsAsync(Account.Account account, CancellationToken ct, bool includeStarred = true)
        => account.LogActivityAsync(ActivityKind.RefreshArticleStatuses, () => RefreshArticleStatusCoreAsync(account, ct, includeStarred),
            successMessage: RefreshStatusMessage, durationIsSignificant: counts => counts.TotalChanged > 0);

    private async Task<StatusRefreshCounts> RefreshArticleStatusCoreAsync(Account.Account account, CancellationToken ct, bool includeStarred)
    {
        var user = AccessToken?.Username ?? throw new CredentialsException("Sign in to Feedly before refreshing article status.");
        StreamIDs? unread = null, starred = null;
        Exception? error = null;
        try { unread = await CollectStreamIDsAsync(FeedlyResourceIds.GlobalAllFor(user), true, DateTime.UtcNow.AddDays(-StreamFetchDaysLimit), ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            if (_api.RateLimiter.IsRateLimitError(ex)) throw;
            Trace.TraceError($"Feedly unread status refresh failed: {ex.Message}");
            error = ex;
        }
        try
        {
            if (includeStarred) starred = await CollectStreamIDsAsync(FeedlyResourceIds.GlobalSavedFor(user), null, null, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            Trace.TraceError($"Feedly starred status refresh failed: {ex.Message}");
            error = ex;
        }
        var counts = await RemoteSyncHelpers.ReconcileStatusesAsync(account, unread?.IDs, starred?.IDs,
            unreadIsComplete: unread?.Truncated == false, starredIsComplete: starred?.Truncated == false);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return new(counts.UnreadAdded, counts.UnreadRemoved, counts.StarredAdded, counts.StarredRemoved);
    }

    private Task<FeedListChanges> RefreshFeedListAsync(Account.Account account, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.RefreshFeedList, async () =>
    {
        var previousFolders = account.Folders.ToHashSet();
        var previousFeeds = account.FlattenedFeeds().ToHashSet();
        var names = previousFeeds.ToDictionary(feed => feed.FeedID, feed => feed.NameForDisplay, StringComparer.Ordinal);
        var collections = await _api.GetCollectionsAsync(ct);
        var pairs = FeedlyFolderReconciliation.MirrorCollectionsAsFolders(account, collections);
        await FeedlyFolderReconciliation.SyncFeedsForCollectionFoldersAsync(account, pairs, previousFeeds);
        account.PerformBatchUpdate(account.TopLevelFeeds.Clear);
        var folders = account.Folders.ToHashSet();
        var feeds = account.FlattenedFeeds().ToHashSet();
        return new FeedListChanges(folders.Except(previousFolders).Count(), previousFolders.Except(folders).Count(),
            feeds.Except(previousFeeds).Count(), previousFeeds.Except(feeds).Count(),
            feeds.Count(feed => names.TryGetValue(feed.FeedID, out var before) && before != feed.NameForDisplay));
    }, successMessage: FeedListMessage, durationIsSignificant: changes => changes.TotalChanged > 0);

    private async Task<StreamIDs> CollectStreamIDsAsync(string stream, bool? unreadOnly, DateTime? newerThan, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        for (int i = 0; i < MaxStreamPageCount; i++)
        {
            var page = await _api.GetStreamIdsAsync(stream, continuation, newerThan, unreadOnly, ct: ct);
            if (page?.Ids is null) throw new InvalidDataException("Feedly returned no stream IDs.");
            ids.UnionWith(page.Ids);
            continuation = page.Continuation;
            if (string.IsNullOrEmpty(continuation)) return new(ids, false);
            if (!visited.Add(continuation)) throw new InvalidDataException("Feedly returned a repeated stream continuation.");
        }
        Trace.TraceInformation("Feedly reached its stream page limit; absence is not authoritative and the watermark is retained.");
        return new(ids, true);
    }

    internal async Task<int> DownloadEntriesAsync(Account.Account account, IReadOnlyList<string> articleIDs, CancellationToken ct)
    {
        var completed = new HashSet<string>(StringComparer.Ordinal);
        int added = 0;
        try
        {
            var chunks = articleIDs.Chunk(ArticleDownloadChunkSize).Take(MaxArticleDownloadChunksPerSync);
            foreach (var chunk in chunks)
            {
                added += await account.LogActivityAsync(ActivityKind.RefreshMissingArticles, async () =>
                    await SaveEntriesAsync(account, await _api.GetEntriesAsync(chunk, ct), restoreNewUnread: false),
                    detail: ActivityLogger.Shared.NextTaskNumberString(), successMessage: count => $"{count} new articles");
                completed.UnionWith(chunk);
            }
        }
        finally
        {
            if (completed.Count > 0)
                account.Settings.PendingArticleRefreshIDs = account.Settings.PendingArticleRefreshIDs.Except(completed).ToArray();
        }
        if (articleIDs.Count > MaxArticleDownloadChunksPerSync * ArticleDownloadChunkSize)
            Trace.TraceInformation("Feedly reached its per-refresh article download limit; missing bodies remain queued for the next refresh.");
        return added;
    }

    private async Task<int> RefreshIndividualFeedsAsync(Account.Account account, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = account.FlattenedFeeds().Where(feed => feed.LastCheckDate is null || now - feed.LastCheckDate >= MinimumFeedRefreshInterval)
            .OrderBy(feed => feed.LastCheckDate ?? DateTime.MinValue).Take(5).ToList();
        int added = 0;
        foreach (var feed in due)
        {
            var previous = feed.LastCheckDate;
            feed.LastCheckDate = now;
            try
            {
                var result = await account.LogActivityAsync(ActivityKind.RefreshFeedContent(feed.Url),
                    () => SyncStreamContentsAsync(account, ResourceID(feed), previous ?? now.AddDays(-StreamFetchDaysLimit), ct),
                    detail: feed.NameForDisplay, successMessage: result => $"{result.Added} new articles");
                added += result.Added;
                if (!result.Complete) feed.LastCheckDate = previous;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
            {
                Trace.TraceError($"Feedly could not refresh {feed.NameForDisplay}: {ex.Message}");
                if (_api.RateLimiter.IsRateLimitError(ex)) throw;
            }
        }
        return added;
    }

    private async Task<(bool Complete, int Added)> SyncStreamContentsAsync(Account.Account account, string stream, DateTime newerThan, CancellationToken ct)
    {
        string? continuation = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        int added = 0;
        for (int i = 0; i < MaxStreamPageCount; i++)
        {
            var page = await _api.GetStreamContentsAsync(stream, continuation, newerThan, count: 100, ct: ct);
            if (page?.Items is null) throw new InvalidDataException("Feedly returned no stream contents.");
            added += await SaveEntriesAsync(account, page.Items, restoreNewUnread: true);
            continuation = page.Continuation;
            if (string.IsNullOrEmpty(continuation)) return (true, added);
            if (!visited.Add(continuation)) throw new InvalidDataException("Feedly returned a repeated content continuation.");
        }
        Trace.TraceInformation("Feedly reached its feed-content page limit; the feed's previous fetch date is retained.");
        return (false, added);
    }

    private static async Task<int> SaveEntriesAsync(Account.Account account, IEnumerable<FeedlyEntry> entries, bool restoreNewUnread)
    {
        var items = entries.DistinctBy(entry => entry.Id).ToList();
        var feeds = account.FlattenedFeeds().GroupBy(feed => ResourceID(feed), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var articles = new List<Article>();
        foreach (var entry in items)
        {
            var parsed = new FeedlyEntryParser(entry).ParsedItemRepresentation;
            if (parsed is null)
            {
                Trace.TraceWarning($"Feedly article {entry.Id} has no originating feed and was not stored.");
                continue;
            }
            var feedID = feeds.GetValueOrDefault(parsed.FeedUrl)?.FeedID ?? parsed.FeedUrl;
            articles.Add(RemoteSyncHelpers.ArticleFromParsedItem(account, parsed, feedID, defaultRead: true));
        }
        var added = await account.Database.UpdateArticlesAsync(articles);
        if (restoreNewUnread)
        {
            var addedIDs = added.Select(article => article.ArticleID).ToHashSet();
            var unread = items.Where(entry => entry.Unread && addedIDs.Contains(entry.Id)).Select(entry => entry.Id).ToHashSet();
            await RemoteSyncHelpers.ReconcileStatusesAsync(account, unread, null, unreadIsComplete: false);
        }
        if (added.Count > 0) account.RaiseNewArticlesDownloaded(added);
        return added.Count;
    }
}
