using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WinNewsWire.Account;
using WinNewsWire.Core;
using WinNewsWire.Parsers;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.ReaderAPI;

public sealed partial class ReaderAPIAccountDelegate
{
    private const string ReadingList = "user/-/state/com.google/reading-list";
    private const string ReadState = "user/-/state/com.google/read";
    private const string StarredState = "user/-/state/com.google/starred";

    public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.RefreshAll, () => RefreshAllCoreAsync(account, progress, ct));

    private async Task RefreshAllCoreAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        API.AccountSettings = account.Settings;
        await API.EnsureAuthenticatedAsync(ct);
        await account.LogActivityAsync(ActivityKind.RefreshFeedList, async () =>
        {
            var tags = await API.RetrieveTagsAsync(ct);
            var subscriptions = await API.RetrieveSubscriptionsAsync(ct);
            if (tags is { Tags: null } || subscriptions is { Subscriptions: null })
                throw new InvalidDataException("Reader API returned incomplete subscription data.");
            SyncStructure(account, tags?.Tags, subscriptions?.Subscriptions);
            API.CommitConditionalGetInfo();
        });
        await RemoteSyncHelpers.SendBeforeRefreshAsync(account, ct);
        var start = DateTime.UtcNow;
        ReaderAPIItemIDs? snapshot = null;
        var downloadedIDs = new HashSet<string>(StringComparer.Ordinal);
        if (!ShouldConserveQuota())
        {
            snapshot = await account.LogActivityAsync(ActivityKind.FetchArticleIDs,
                () => API.RetrieveItemIDsAsync(ReadingList, since: account.Settings.LastArticleFetchStartTime ?? start.AddMonths(-3), ct: ct),
                successMessage: result => $"{result.IDs.Count} article IDs");
            var mapped = await MapIDsForStorageAsync(account, snapshot.IDs);
            downloadedIDs.UnionWith(mapped.Values);
            await account.Database.CreateStatusesIfNeededAsync(downloadedIDs);
            await RefreshArticleStatusReturningCountAsync(account, ct, respectQuota: false);
            foreach (var id in _initialFeeds.ToList())
            {
                if (account.FlattenedFeeds().Any(feed => feed.ExternalID == id))
                {
                    var initial = await API.RetrieveItemIDsAsync(id, since: start.AddMonths(-3), ct: ct);
                    var initialIDs = await MapIDsForStorageAsync(account, initial.IDs);
                    downloadedIDs.UnionWith(initialIDs.Values);
                    await account.Database.CreateStatusesIfNeededAsync(initialIDs.Values);
                }
                _initialFeeds.Remove(id);
            }
        }
        downloadedIDs.UnionWith(await account.Database.FetchMissingArticleIDsAsync(start.AddMonths(-3)));
        foreach (var batch in downloadedIDs.Chunk(1000))
            await account.LogActivityAsync(ActivityKind.RefreshMissingArticles, async () =>
            {
                var entries = await API.RetrieveEntriesAsync(batch, ct);
                await SaveEntriesAsync(account, entries);
                return entries.Count;
            }, successMessage: count => $"{count} articles");
        if (snapshot is not null) account.Settings.LastArticleFetchStartTime = snapshot.ServerDate ?? start;
        await account.RecalculateUnreadCountsAsync();
        progress?.Report(new ProgressInfo(0, 1, 1));
        account.Settings.LastRefreshCompletedDate = DateTime.UtcNow;
    }

    internal void SyncStructure(Account.Account account, IReadOnlyList<ReaderAPITag>? tags, IReadOnlyList<ReaderAPISubscription>? subscriptions)
    {
        account.PerformBatchUpdate(() =>
        {
            if (tags is not null)
            {
                var folders = tags.Where(tag => API.Variant == ReaderAPIVariant.Inoreader ? tag.Type == "folder" : tag.FolderName is not null).ToList();
                var ids = folders.Select(folder => folder.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var obsolete in account.Folders.Where(folder => folder.ExternalID is not null && !ids.Contains(folder.ExternalID)).ToList())
                {
                    foreach (var feed in obsolete.Feeds) account.AddFeedToContainer(feed, null);
                    account.RemoveFolder(obsolete);
                }
                foreach (var tag in folders)
                {
                    var folder = account.Folders.FirstOrDefault(folder => folder.ExternalID == tag.Id)
                        ?? account.AddFolder(tag.FolderName ?? "None");
                    folder.ExternalID = tag.Id;
                    folder.Name = tag.FolderName ?? "None";
                }
            }
            if (subscriptions is null) return;
            var current = account.FlattenedFeeds().ToList();
            var desired = new Dictionary<string, Feed>(StringComparer.Ordinal);
            foreach (var subscription in subscriptions)
            {
                var feed = current.FirstOrDefault(item => item.ExternalID == subscription.Id || item.Url == subscription.Url)
                    ?? account.CreateFeedModel(subscription.Url, DecodeFullwidth(subscription.Title), subscription.Id);
                if (DecodeFullwidth(subscription.Title) is { Length: > 0 } name) feed.Name = name;
                feed.ExternalID = subscription.Id;
                feed.HomePageUrl = subscription.HomePageUrl;
                feed.IconUrl = subscription.IconUrl;
                feed.FolderRelationship = new();
                desired[subscription.Id] = feed;
            }
            account.TopLevelFeeds.Clear();
            foreach (var folder in account.Folders) folder.Feeds.Clear();
            foreach (var subscription in subscriptions)
            {
                var feed = desired[subscription.Id];
                bool filed = false;
                foreach (var category in subscription.Categories ?? [])
                {
                    var folder = account.Folders.FirstOrDefault(folder => folder.ExternalID == category.Id);
                    if (folder is null) continue;
                    account.AddFeedToContainer(feed, folder);
                    feed.FolderRelationship![category.Id] = subscription.Id;
                    filed = true;
                }
                if (!filed) account.AddFeedToContainer(feed, null);
            }
        });
    }

    public Task RefreshArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => RefreshArticleStatusReturningCountAsync(account, ct, respectQuota: true);

    private async Task<int> RefreshArticleStatusReturningCountAsync(Account.Account account, CancellationToken ct, bool respectQuota)
    {
        if (respectQuota && ShouldConserveQuota()) return 0;
        IReadOnlySet<string>? unread = null, starred = null;
        Exception? error = null;
        try
        {
            var ids = await API.RetrieveItemIDsAsync(ReadingList, excludedTag: ReadState, ct: ct);
            unread = (await MapIDsForStorageAsync(account, ids.IDs)).Values.ToHashSet();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException)
        {
            if (API.RateLimiter.IsRateLimitError(ex)) throw;
            Trace.TraceError($"Reader API unread refresh failed: {ex.Message}");
            error = ex;
        }
        try
        {
            var ids = await API.RetrieveItemIDsAsync(StarredState, ct: ct);
            starred = (await MapIDsForStorageAsync(account, ids.IDs)).Values.ToHashSet();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException)
        {
            Trace.TraceError($"Reader API starred refresh failed: {ex.Message}");
            error = ex;
        }
        var counts = await RemoteSyncHelpers.ReconcileStatusesAsync(account, unread, starred);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return counts.UnreadAdded + counts.UnreadRemoved + counts.StarredAdded + counts.StarredRemoved;
    }

    private bool ShouldConserveQuota()
    {
        if (API.UsageLimits is not { } usage || usage.ResetDate <= DateTime.UtcNow || usage.Zone1Usage < usage.Zone1Limit * 0.9) return false;
        Trace.TraceWarning($"Reader API is deferring status downloads until its quota resets at {usage.ResetDate:O}.");
        return true;
    }

    private async Task<Dictionary<string, string>> MapIDsForStorageAsync(Account.Account account, IEnumerable<string> ids)
    {
        const string prefix = "tag:google.com,2005:reader/item/";
        var canonical = ids.Select(id =>
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal)) return ReaderAPIEntry.UniqueID(id, API.Variant);
            if (API.Variant != ReaderAPIVariant.TheOldReader && ulong.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unsigned))
                return unchecked((long)unsigned).ToString(CultureInfo.InvariantCulture);
            return id;
        }).Distinct(StringComparer.Ordinal).ToArray();
        var longForms = canonical.ToDictionary(id => id, id => ReaderAPICaller.ItemID(id, API.Variant), StringComparer.Ordinal);
        var existing = await account.Database.FetchExistingStatusIDsAsync(canonical.Concat(longForms.Values));
        return canonical.ToDictionary(id => id, id => !existing.Contains(id) && existing.Contains(longForms[id]) ? longForms[id] : id, StringComparer.Ordinal);
    }

    private async Task SaveEntriesAsync(Account.Account account, IReadOnlyList<ReaderAPIEntry> entries)
    {
        var mapped = await MapIDsForStorageAsync(account, entries.Select(entry => entry.Id));
        var feeds = account.FlattenedFeeds().GroupBy(feed => feed.ExternalID ?? feed.FeedID, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var articles = new List<Articles.Article>();
        foreach (var entry in entries)
        {
            if (entry.Origin?.StreamId is not { } stream)
            {
                Trace.TraceWarning($"Reader API article {entry.Id} has no originating feed and was not stored.");
                continue;
            }
            var canonical = entry.UniqueID(API.Variant);
            var item = new ParsedItem(mapped[canonical], canonical, stream, null, entry.Alternate?.FirstOrDefault()?.Href,
                DecodeFullwidth(entry.Title), null, entry.Content?.Content ?? entry.Summary?.Content, null, null,
                entry.Summary?.Content, entry.ImageUrl, null, entry.ParseDatePublished(),
                entry.Updated is > 0 ? DateTime.UnixEpoch.AddSeconds(entry.Updated.Value) : null,
                entry.Author is null ? null : new HashSet<ParsedAuthor> { new(DecodeFullwidth(entry.Author), null, null, null) }, null, entry.Attachments);
            articles.Add(RemoteSyncHelpers.ArticleFromParsedItem(account, item, feeds.GetValueOrDefault(stream)?.FeedID ?? stream, defaultRead: true));
        }
        var added = await account.Database.UpdateArticlesAsync(articles);
        var addedIDs = added.Select(article => article.ArticleID).ToHashSet(StringComparer.Ordinal);
        var newEntries = entries.Where(entry => entry.Categories is not null && addedIDs.Contains(mapped[entry.UniqueID(API.Variant)])).ToList();
        var unread = newEntries.Where(entry => !entry.Categories!.Contains(ReadState)).Select(entry => mapped[entry.UniqueID(API.Variant)]).ToHashSet();
        var starred = newEntries.Where(entry => entry.Categories!.Contains(StarredState)).Select(entry => mapped[entry.UniqueID(API.Variant)]).ToHashSet();
        await RemoteSyncHelpers.ReconcileStatusesAsync(account, unread, starred, unreadIsComplete: false, starredIsComplete: false);
        if (added.Count > 0) account.RaiseNewArticlesDownloaded(added);
    }

    internal static string? DecodeFullwidth(string? text)
        => text?.Replace('\uFF06', '&').Replace('\uFF1C', '<').Replace('\uFF1E', '>');
}
