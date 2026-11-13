using WinNewsWire.Account;
using WinNewsWire.Core;
using WinNewsWire.ActivityLog;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;
using WinNewsWire.FeedFinder;

namespace WinNewsWire.Feedbin;

/// <summary>Port of <c>FeedbinAccountDelegate</c> (core sync loop).</summary>
public sealed partial class FeedbinAccountDelegate : IAccountDelegate
{
    public AccountType Type => AccountType.Feedbin;
    public bool SupportsRemoteSync => true;
    public bool SupportsOpmlImport => true;
    public string? CredentialUsername => API.Credentials?.Username;
    public FeedbinAPICaller API { get; }
    private readonly Dictionary<long, Feed> _feedsByServiceID = new();
    private DateTime? _lastBackdateStartTime;

    public FeedbinAccountDelegate(Credentials? credentials = null, HttpClient? http = null)
    {
        API = new FeedbinAPICaller(http) { Credentials = credentials };
    }

    public Task<bool> ValidateCredentialsAsync(CancellationToken ct = default)
        => API.ValidateCredentialsAsync(ct);

    public void CredentialsChanged(Credentials credentials) => API.Credentials = credentials;
    public void Dispose() => API.Dispose();

    public async Task ImportOpmlAsync(Account.Account account, Parsers.ParserData data, CancellationToken ct = default)
    {
        var result = await API.ImportOpmlAsync(data.Data, ct);
        while (!result.Complete)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            result = await API.RetrieveOpmlImportResultAsync(result.ImportResultID, ct);
        }
    }

    public async Task<Feed?> CreateFeedAsync(Account.Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
    {
        var requested = urlOrSite;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        FeedbinSubscription sub;
        while (true)
        {
            if (!visited.Add(requested)) throw new InvalidDataException("Feedbin returned the same subscription choice repeatedly.");
            var result = await API.CreateSubscriptionAsync(requested, ct);
            if (result is FeedbinCreateSubscriptionResult.Created created) { sub = created.Subscription; break; }
            if (result is FeedbinCreateSubscriptionResult.AlreadySubscribed) throw new InvalidOperationException("Already subscribed to this feed.");
            if (result is FeedbinCreateSubscriptionResult.NotFound) throw new FeedNotFoundException();
            var choices = ((FeedbinCreateSubscriptionResult.MultipleChoice)result).Choices;
            var best = FeedSpecifier.BestFeed(choices.Select((choice, i) => new FeedSpecifier(choice.Title, choice.FeedUrl,
                choice.FeedUrl == requested ? FeedSpecifierSource.UserEntered : FeedSpecifierSource.HtmlLink, i + 1)))
                ?? throw new FeedNotFoundException();
            requested = best.UrlString;
        }
        long? tagging = folder is not null ? await API.CreateTaggingAsync(sub.FeedID, folder.NameForDisplay, ct) : null;
        if (!string.IsNullOrEmpty(name)) await API.RenameSubscriptionAsync(sub.SubscriptionID, name, ct);
        Feed? added = null;
        account.PerformBatchUpdate(() =>
        {
            added = account.AddFeed(sub.FeedUrl, sub.Title ?? name, folder, sub.FeedID.ToString());
            if (!string.IsNullOrEmpty(name)) added.EditedName = name;
            added.ExternalID = sub.SubscriptionID.ToString();
            added.HomePageUrl = sub.SiteUrl;
            added.IconUrl = sub.JsonFeed?.Icon;
            added.FaviconUrl = sub.JsonFeed?.Favicon;
            if (folder is not null && tagging is { } id)
                (added.FolderRelationship ??= new())[folder.NameForDisplay] = id.ToString();
        });
        return added;
    }

    public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.RefreshAll, () => RefreshAllCoreAsync(account, progress, ct));

    private async Task RefreshAllCoreAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        await RemoteSyncHelpers.SendBeforeRefreshAsync(account, ct);
        await account.LogActivityAsync(ActivityKind.RefreshFeedList, () => SyncFoldersAndFeedsAsync(account, ct));
        await RefreshArticleStatusAsync(account, ct);
        var unread = await account.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Read, false);
        var starred = await account.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Starred, true);
        var newArticles = await DownloadArticlesAsync(account, unread, starred, progress, ct);
        await account.RecalculateUnreadCountsAsync();
        if (newArticles.Count > 0) account.RaiseNewArticlesDownloaded(newArticles);
        account.Settings.LastRefreshCompletedDate = DateTime.UtcNow;
    }

    public Task SendArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => SendArticleStatusReturningCountAsync(account, ct);

    private Task<int> SendArticleStatusReturningCountAsync(Account.Account account, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.SendArticleStatuses, () => RemoteSyncHelpers.FlushPendingAsync(account, async (key, flag, ids, c) =>
        {
            var longIds = ids.Select(long.Parse).ToList();
            switch ((key, flag))
            {
                case (SyncDatabase.SyncStatus.SyncKey.Read, true): await API.MarkAsReadAsync(longIds, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Read, false): await API.MarkAsUnreadAsync(longIds, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, true): await API.MarkAsStarredAsync(longIds, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, false): await API.MarkAsUnstarredAsync(longIds, c); break;
            }
        }, ct, batchSize: (_, _) => 1000));

    public Task RefreshArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => account.LogActivityAsync(ActivityKind.RefreshArticleStatuses, () => RefreshArticleStatusCoreAsync(account, ct));

    public async Task<bool> SyncArticleStatusAsync(Account.Account account, CancellationToken ct = default)
    {
        var sent = await SendArticleStatusReturningCountAsync(account, ct);
        var changed = await account.LogActivityAsync(ActivityKind.RefreshArticleStatuses, () => RefreshArticleStatusCoreAsync(account, ct));
        return sent > 0 || changed > 0;
    }

    private async Task<int> RefreshArticleStatusCoreAsync(Account.Account account, CancellationToken ct)
    {
        API.AccountSettings = account.Settings;
        Exception? error = null;
        int changed = 0;
        try
        {
            var unread = await API.RetrieveUnreadEntryIdsAsync(ct);
            var counts = await RemoteSyncHelpers.ReconcileStatusesAsync(account, unread.Items?.Select(id => id.ToString()).ToHashSet(), null);
            changed += counts.UnreadAdded + counts.UnreadRemoved;
            API.StoreConditionalGetIfNeeded(FeedbinAPICaller.ConditionalGetKeys.UnreadEntries, unread);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException
            or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"Feedbin unread status refresh failed: {ex}");
            error = ex;
        }
        try
        {
            var starred = await API.RetrieveStarredEntryIdsAsync(ct);
            var counts = await RemoteSyncHelpers.ReconcileStatusesAsync(account, null, starred.Items?.Select(id => id.ToString()).ToHashSet());
            changed += counts.StarredAdded + counts.StarredRemoved;
            API.StoreConditionalGetIfNeeded(FeedbinAPICaller.ConditionalGetKeys.StarredEntries, starred);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException
            or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceError($"Feedbin starred status refresh failed: {ex}");
            error = ex;
        }
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return changed;
    }

    private async Task SyncFoldersAndFeedsAsync(Account.Account account, CancellationToken ct)
    {
        API.AccountSettings = account.Settings;
        var currentFeeds = account.FlattenedFeeds().ToHashSet();
        var previousIDs = _feedsByServiceID.ToLookup(pair => pair.Value.Url, pair => pair.Key, StringComparer.Ordinal);
        _feedsByServiceID.Clear();
        foreach (var feed in currentFeeds)
        {
            foreach (var id in previousIDs[feed.Url]) _feedsByServiceID[id] = feed;
            if (long.TryParse(feed.FeedID, out var serviceID)) _feedsByServiceID[serviceID] = feed;
        }
        var mappedFeeds = _feedsByServiceID.Values.ToHashSet();
        var tags = await API.RetrieveTagsAsync(ct);
        var subs = await API.RetrieveSubscriptionsAsync(ct, forceRefresh: currentFeeds.Any(feed => !mappedFeeds.Contains(feed)));
        if (tags.Items?.Any(tag => account.ExistingFolderWithName(tag.Name) is null) == true)
            account.Settings.SetConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.Taggings, null);
        var taggings = await API.RetrieveTaggingsAsync(ct);
        if (subs.Items?.Any(s => string.IsNullOrWhiteSpace(s.FeedUrl)) == true)
            throw new InvalidDataException("Feedbin returned a subscription without a feed URL.");
        if (tags.Items?.Any(tag => string.IsNullOrWhiteSpace(tag.Name)) == true
            || taggings.Items?.Any(tagging => string.IsNullOrWhiteSpace(tagging.Name)) == true)
            throw new InvalidDataException("Feedbin returned a folder without a name.");
        if (tags.Items is null && subs.Items is null && taggings.Items is null) return;
        account.PerformBatchUpdate(() =>
        {
            if (tags.Items is { } changedTags)
            {
                var names = changedTags.Select(tag => tag.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var folder in account.Folders.Where(folder => folder.ExternalID is not null && !names.Contains(folder.NameForDisplay)).ToList())
                {
                    foreach (var feed in folder.Feeds)
                    {
                        feed.FolderRelationship?.Remove(folder.NameForDisplay);
                        if (!account.TopLevelFeeds.Contains(feed)) account.TopLevelFeeds.Add(feed);
                    }
                    account.Folders.Remove(folder);
                }
                foreach (var tag in changedTags) account.AddFolder(tag.Name).ExternalID = tag.TagID.ToString();
            }
            if (subs.Items is { } changedSubscriptions)
            {
                _feedsByServiceID.Clear();
                foreach (var sub in changedSubscriptions)
                {
                    var feed = account.ExistingFeedWithUrl(sub.FeedUrl) ?? account.AddFeed(sub.FeedUrl, sub.Title, feedID: sub.FeedID.ToString());
                    if (!string.IsNullOrEmpty(sub.Title)) feed.Name = sub.Title;
                    feed.ExternalID = sub.SubscriptionID.ToString();
                    feed.HomePageUrl = sub.SiteUrl;
                    feed.IconUrl = sub.JsonFeed?.Icon;
                    feed.FaviconUrl = sub.JsonFeed?.Favicon;
                    _feedsByServiceID[sub.FeedID] = feed;
                }
                var retained = _feedsByServiceID.Values.ToHashSet();
                account.TopLevelFeeds.RemoveAll(feed => !retained.Contains(feed));
                foreach (var folder in account.Folders) folder.Feeds.RemoveAll(feed => !retained.Contains(feed));
            }
            if (taggings.Items is { } changedTaggings)
            {
                var feeds = account.FlattenedFeeds().ToHashSet();
                var folderByName = account.Folders.ToDictionary(folder => folder.NameForDisplay, StringComparer.Ordinal);
                foreach (var feed in feeds) feed.FolderRelationship = new(StringComparer.Ordinal);
                foreach (var folder in account.Folders) folder.Feeds.Clear();
                foreach (var tagging in changedTaggings)
                {
                    if (!_feedsByServiceID.TryGetValue(tagging.FeedID, out var feed) || !feeds.Contains(feed)
                        || !folderByName.TryGetValue(tagging.Name, out var folder)) continue;
                    if (!folder.Feeds.Contains(feed)) folder.Feeds.Add(feed);
                    feed.FolderRelationship![tagging.Name] = tagging.TaggingID.ToString();
                }
                account.TopLevelFeeds.Clear();
                account.TopLevelFeeds.AddRange(feeds);
            }
            var inFolders = account.Folders.SelectMany(folder => folder.Feeds).ToHashSet();
            account.TopLevelFeeds.RemoveAll(inFolders.Contains);
        });
        API.StoreConditionalGetIfNeeded(FeedbinAPICaller.ConditionalGetKeys.Tags, tags);
        API.StoreConditionalGetIfNeeded(FeedbinAPICaller.ConditionalGetKeys.Subscriptions, subs);
        API.StoreConditionalGetIfNeeded(FeedbinAPICaller.ConditionalGetKeys.Taggings, taggings);
    }

    private async Task<IReadOnlyList<Article>> DownloadArticlesAsync(Account.Account account,
        IReadOnlySet<string> unread, IReadOnlySet<string> starred, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var since = account.Settings.LastArticleFetchStartTime ?? start.AddMonths(-3);
        DateTime? completedBackdate = null;
        if (account.Settings.LastArticleFetchStartTime is { } previous
            && (_lastBackdateStartTime is null || _lastBackdateStartTime.Value.AddDays(1) < previous))
        {
            completedBackdate = previous;
            since = previous.AddDays(-1);
        }
        DateTime? serverDate = null;
        var added = new List<Article>();
        int processed = 0;
        await foreach (var page in API.RetrieveEntryPagesAsync(since, ct: ct))
        {
            serverDate ??= page.ServerDate;
            added.AddRange(await SaveEntriesAsync(account, page.Entries, unread, starred));
            processed += page.Entries.Count;
            progress?.Report(new ProgressInfo(0, processed, processed));
        }
        var missing = await account.Database.FetchMissingArticleIDsAsync(start.AddMonths(-3));
        foreach (var batch in missing.Chunk(100))
            added.AddRange(await SaveEntriesAsync(account, await API.RetrieveEntriesAsync(batch.Select(long.Parse), ct), unread, starred));
        account.Settings.LastArticleFetchStartTime = serverDate ?? start;
        if (completedBackdate is { } backdate) _lastBackdateStartTime = backdate;
        return added;
    }

    private async Task<IReadOnlyList<Article>> SaveEntriesAsync(Account.Account account, IEnumerable<FeedbinEntry> entries,
        IReadOnlySet<string> unread, IReadOnlySet<string> starred)
    {
        var articles = new List<Article>();
        foreach (var e in entries.DistinctBy(e => e.ArticleID))
        {
            var feedID = _feedsByServiceID.TryGetValue(e.FeedID, out var feed) ? feed.FeedID : e.FeedID.ToString();
            var status = new ArticleStatus(e.ArticleID.ToString(), read: !unread.Contains(e.ArticleID.ToString()),
                starred: starred.Contains(e.ArticleID.ToString()), dateArrived: e.DateArrived ?? DateTime.UtcNow);
            articles.Add(new Article(
                account.AccountID, articleID: e.ArticleID.ToString(), feedID: feedID, uniqueID: e.ArticleID.ToString(),
                title: e.Title, contentHtml: e.ContentHtml, contentText: null, markdown: null,
                url: e.Url, externalURL: e.ExtractedContentUrl, summary: e.Summary, imageURL: e.ImageUrl,
                datePublished: e.DatePublished, dateModified: null,
                authors: string.IsNullOrWhiteSpace(e.Author) ? null
                    : new HashSet<Author> { Author.Create(null, e.Author, null, null, null)! },
                attachments: e.Enclosure?.ParsedAttachment is { } media
                    ? new[] { Attachment.Create(null, media.Url, media.MimeType, media.Title, media.SizeInBytes, media.DurationInSeconds)! } : null,
                status: status, thumbnailURL: e.ThumbnailUrl));
        }
        return await account.Database.UpdateArticlesAsync(articles);
    }
}
