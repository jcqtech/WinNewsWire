using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;

namespace WinNewsWire.NewsBlur;

/// <summary>Port of <c>NewsBlurAccountDelegate</c> (core sync loop).</summary>
public sealed class NewsBlurAccountDelegate : IAccountDelegate
{
    public AccountType Type => AccountType.NewsBlur;
    public bool SupportsRemoteSync => true;
    public NewsBlurAPICaller API { get; } = new();

    public NewsBlurAccountDelegate(Credentials? credentials = null) { API.Credentials = credentials; }

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
        var feeds = await API.RetrieveFeedsAsync(ct);
        foreach (var nb in feeds)
        {
            if (nb.FeedUrl is null) continue;
            if (account.ExistingFeedWithUrl(nb.FeedUrl) is null)
            {
                var f = account.AddFeed(nb.FeedUrl, nb.Name, folder: null);
                f.ExternalID = nb.FeedID.ToString();
                f.HomePageUrl = nb.HomePageUrl;
            }
        }

        var stories = await API.RetrieveStoriesAsync(1, ct);
        if (stories.Count == 0) return;
        var articles = new List<Article>();
        foreach (var s in stories)
        {
            var feedID = account.FlattenedFeeds()
                .FirstOrDefault(f => f.ExternalID == s.FeedID.ToString())?.FeedID ?? s.FeedID.ToString();
            var status = new ArticleStatus(s.StoryID, read: false, dateArrived: s.DatePublished ?? DateTime.UtcNow);
            articles.Add(new Article(
                account.AccountID, articleID: s.StoryID, feedID: feedID, uniqueID: s.StoryID,
                title: s.Title, contentHtml: s.ContentHtml, contentText: null, markdown: null,
                url: s.Url, externalURL: null, summary: null, imageURL: null,
                datePublished: s.DatePublished, dateModified: null,
                authors: string.IsNullOrWhiteSpace(s.AuthorName) ? null
                    : new HashSet<Author> { Author.Create(null, s.AuthorName, null, null, null)! },
                attachments: null, status: status));
        }
        var newArticles = await account.Database.UpdateArticlesAsync(articles);
        if (newArticles.Count > 0) account.RaiseNewArticlesDownloaded(newArticles);
        await account.RecalculateUnreadCountsAsync();
    }
}
