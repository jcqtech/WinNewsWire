using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;

namespace WinNewsWire.Feedbin;

/// <summary>Port of <c>FeedbinAccountDelegate</c> (core sync loop).</summary>
public sealed class FeedbinAccountDelegate : IAccountDelegate
{
    public AccountType Type => AccountType.Feedbin;
    public bool SupportsRemoteSync => true;
    public FeedbinAPICaller API { get; } = new();

    public FeedbinAccountDelegate(Credentials? credentials = null)
    {
        API.Credentials = credentials;
    }

    public Task<bool> ValidateCredentialsAsync(CancellationToken ct = default)
        => API.ValidateCredentialsAsync(ct);

    public async Task<Feed?> CreateFeedAsync(Account.Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
    {
        var sub = await API.CreateSubscriptionAsync(urlOrSite, ct);
        if (sub is null) return null;
        var feed = account.AddFeed(sub.FeedUrl, sub.Title ?? name, folder);
        feed.ExternalID = sub.SubscriptionID.ToString();
        feed.HomePageUrl = sub.SiteUrl;
        if (folder is not null) await API.CreateTaggingAsync(sub.FeedID, folder.Name ?? "", ct);
        return feed;
    }

    public async Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        await SendArticleStatusAsync(account, ct);
        await SyncFoldersAndFeedsAsync(account, ct);
        await RefreshArticleStatusAsync(account, ct);
        await DownloadArticlesAsync(account, progress, ct);
        await account.RecalculateUnreadCountsAsync();
    }

    public Task SendArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => RemoteSyncHelpers.FlushPendingAsync(account, async (key, flag, ids, c) =>
        {
            var longIds = ids.Where(id => long.TryParse(id, out _)).Select(long.Parse).ToList();
            if (longIds.Count == 0) return;
            switch ((key, flag))
            {
                case (SyncDatabase.SyncStatus.SyncKey.Read, true): await API.MarkAsReadAsync(longIds, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Read, false): await API.MarkAsUnreadAsync(longIds, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, true): await API.MarkAsStarredAsync(longIds, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, false): await API.MarkAsUnstarredAsync(longIds, c); break;
            }
        }, ct);

    public async Task RefreshArticleStatusAsync(Account.Account account, CancellationToken ct = default)
    {
        var unread = await API.RetrieveUnreadEntryIdsAsync(ct);
        var starred = await API.RetrieveStarredEntryIdsAsync(ct);
        if (unread.Count > 0)
            await account.Database.MarkAsync(unread.Select(id => id.ToString()), ArticleStatus.Key.Read, false);
        if (starred.Count > 0)
            await account.Database.MarkAsync(starred.Select(id => id.ToString()), ArticleStatus.Key.Starred, true);
    }

    private async Task SyncFoldersAndFeedsAsync(Account.Account account, CancellationToken ct)
    {
        var subs = await API.RetrieveSubscriptionsAsync(ct);
        var taggings = await API.RetrieveTaggingsAsync(ct);
        var feedToFolder = taggings.ToLookup(t => t.FeedID);

        var folderByName = account.Folders.ToDictionary(f => f.Name ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var name in taggings.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!folderByName.ContainsKey(name))
                folderByName[name] = account.AddFolder(name);
        }

        foreach (var sub in subs)
        {
            Folder? folder = null;
            var tag = feedToFolder[sub.FeedID].FirstOrDefault();
            if (tag is not null && folderByName.TryGetValue(tag.Name, out var f)) folder = f;
            if (account.ExistingFeedWithUrl(sub.FeedUrl) is null)
            {
                var feed = account.AddFeed(sub.FeedUrl, sub.Title, folder);
                feed.ExternalID = sub.SubscriptionID.ToString();
                feed.HomePageUrl = sub.SiteUrl;
            }
        }
    }

    private async Task DownloadArticlesAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var entries = await API.RetrieveRecentEntriesAsync(100, ct);
        if (entries.Count == 0) return;
        var articles = new List<Article>();
        foreach (var e in entries)
        {
            var feedID = e.FeedID.ToString();
            if (account.FlattenedFeeds().FirstOrDefault(f => f.ExternalID == e.FeedID.ToString()) is { } feed)
                feedID = feed.FeedID;
            var status = new ArticleStatus(e.ArticleID.ToString(), read: false, dateArrived: e.DateArrived ?? DateTime.UtcNow);
            articles.Add(new Article(
                account.AccountID, articleID: e.ArticleID.ToString(), feedID: feedID, uniqueID: e.ArticleID.ToString(),
                title: e.Title, contentHtml: e.ContentHtml, contentText: null, markdown: null,
                url: e.Url, externalURL: e.ExtractedContentUrl, summary: e.Summary, imageURL: null,
                datePublished: e.DatePublished, dateModified: null,
                authors: string.IsNullOrWhiteSpace(e.Author) ? null
                    : new HashSet<Author> { Author.Create(null, e.Author, null, null, null)! },
                attachments: null, status: status));
        }
        var newArticles = await account.Database.UpdateArticlesAsync(articles);
        if (newArticles.Count > 0) account.RaiseNewArticlesDownloaded(newArticles);
        progress?.Report(new ProgressInfo(0, articles.Count, articles.Count));
    }
}
