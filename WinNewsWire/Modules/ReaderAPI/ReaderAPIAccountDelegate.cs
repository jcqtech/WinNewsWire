using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;

namespace WinNewsWire.ReaderAPI;

/// <summary>Port of <c>ReaderAPIAccountDelegate</c> (core sync loop). Handles
/// FreshRSS / Inoreader / BazQux / The Old Reader and any other Google-Reader-compatible host.</summary>
public sealed class ReaderAPIAccountDelegate : IAccountDelegate
{
    public AccountType Type { get; }
    public bool SupportsRemoteSync => true;
    public ReaderAPICaller API { get; }

    public ReaderAPIAccountDelegate(ReaderAPIVariant variant, string? host = null, Credentials? credentials = null, string? authToken = null)
    {
        Type = variant.ToAccountType();
        API = new ReaderAPICaller(variant, host) { Credentials = credentials, AuthToken = authToken };
    }

    public Task SendArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => RemoteSyncHelpers.FlushPendingAsync(account, async (key, flag, ids, c) =>
        {
            switch ((key, flag))
            {
                case (SyncDatabase.SyncStatus.SyncKey.Read, true): await API.MarkAsReadAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Read, false): await API.MarkAsUnreadAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, true): await API.MarkAsStarredAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, false): await API.MarkAsUnstarredAsync(ids, c); break;
            }
        }, ct);

    public async Task<Feed?> CreateFeedAsync(Account.Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
        => await Task.FromResult(account.AddFeed(urlOrSite, name, folder));

    public async Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        await SendArticleStatusAsync(account, ct);
        var subs = await API.RetrieveSubscriptionsAsync(ct);
        if (subs is not null)
        {
            var folderByLabel = account.Folders.ToDictionary(f => f.Name ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var s in subs.Subscriptions)
            {
                if (s.FeedUrl is null) continue;
                Folder? folder = null;
                var label = s.Categories?.FirstOrDefault()?.Label;
                if (!string.IsNullOrEmpty(label))
                {
                    if (!folderByLabel.TryGetValue(label, out folder))
                    { folder = account.AddFolder(label); folderByLabel[label] = folder; }
                }
                if (account.ExistingFeedWithUrl(s.FeedUrl) is null)
                {
                    var feed = account.AddFeed(s.FeedUrl, s.Title, folder);
                    feed.ExternalID = s.Id;
                    feed.HomePageUrl = s.HomePageUrl;
                }
            }
        }

        // Pull the user's main reading list
        var stream = await API.RetrieveStreamContentsAsync("user/-/state/com.google/reading-list", 100, null, ct);
        if (stream is null) return;
        var articles = new List<Article>();
        foreach (var e in stream.Items)
        {
            var streamId = e.Origin?.StreamId;
            var feed = streamId is null ? null
                : account.FlattenedFeeds().FirstOrDefault(f => f.ExternalID == streamId);
            var feedID = feed?.FeedID ?? streamId ?? "unknown";
            var status = new ArticleStatus(e.Id,
                read: e.Categories?.Any(c => c.EndsWith("/state/com.google/read")) == true,
                starred: e.Categories?.Any(c => c.EndsWith("/state/com.google/starred")) == true,
                dateArrived: DateTimeOffset.FromUnixTimeSeconds(e.Published).UtcDateTime);
            articles.Add(new Article(
                account.AccountID, articleID: e.Id, feedID: feedID, uniqueID: e.Id,
                title: e.Title, contentHtml: e.Content?.Content ?? e.Summary?.Content,
                contentText: null, markdown: null,
                url: e.Alternate?.FirstOrDefault()?.Href, externalURL: null, summary: null, imageURL: null,
                datePublished: DateTimeOffset.FromUnixTimeSeconds(e.Published).UtcDateTime,
                dateModified: e.Updated > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.Updated).UtcDateTime : null,
                authors: string.IsNullOrWhiteSpace(e.Author) ? null
                    : new HashSet<Author> { Author.Create(null, e.Author, null, null, null)! },
                attachments: null, status: status));
        }
        var newArticles = await account.Database.UpdateArticlesAsync(articles);
        if (newArticles.Count > 0) account.RaiseNewArticlesDownloaded(newArticles);
        await account.RecalculateUnreadCountsAsync();
    }
}
