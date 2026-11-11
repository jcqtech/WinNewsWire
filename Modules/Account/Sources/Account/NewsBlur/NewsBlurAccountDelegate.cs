using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;
using WinNewsWire.ActivityLog;

namespace WinNewsWire.NewsBlur;

/// <summary>Port of <c>NewsBlurAccountDelegate</c> (core sync loop).</summary>
public sealed partial class NewsBlurAccountDelegate : IAccountDelegate
{
    public AccountType Type => AccountType.NewsBlur;
    public bool SupportsRemoteSync => true;
    public string? CredentialUsername => API.Credentials?.Username;
    public NewsBlurAPICaller API { get; }
    private readonly HashSet<string> _initialFeeds = new(StringComparer.Ordinal);

    public NewsBlurAccountDelegate(Credentials? credentials = null, HttpClient? http = null)
        => API = new NewsBlurAPICaller(http) { Credentials = credentials };

    public void CredentialsChanged(Credentials credentials) => API.Credentials = credentials;
    public void Dispose() => API.Dispose();

    public Task SendArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => SendArticleStatusReturningCountAsync(account, ct);

    private Task<int> SendArticleStatusReturningCountAsync(Account.Account account, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.SendArticleStatuses, () => RemoteSyncHelpers.FlushPendingAsync(account, async (key, flag, ids, c) =>
        {
            switch ((key, flag))
            {
                case (SyncDatabase.SyncStatus.SyncKey.Read, true): await API.MarkAsReadAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Read, false): await API.MarkAsUnreadAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, true): await API.MarkAsStarredAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, false): await API.MarkAsUnstarredAsync(ids, c); break;
            }
        }, ct, batchSize: (key, flag) => key == SyncDatabase.SyncStatus.SyncKey.Read && flag ? 100 : 1));

    public async Task<bool> SyncArticleStatusAsync(Account.Account account, CancellationToken ct = default)
    {
        var sent = await SendArticleStatusReturningCountAsync(account, ct);
        var changed = await RefreshArticleStatusReturningCountAsync(account, ct);
        return sent > 0 || changed > 0;
    }

    public async Task<Feed?> CreateFeedAsync(Account.Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
    {
        var remote = await API.AddUrlAsync(urlOrSite, folder?.NameForDisplay, ct);
        if (string.IsNullOrEmpty(remote.FeedUrl)) throw new InvalidDataException("NewsBlur did not return the feed URL.");
        if (!string.IsNullOrEmpty(name)) await API.RenameFeedAsync(remote.FeedID.ToString(), name, ct);
        Feed? added = null;
        account.PerformBatchUpdate(() =>
        {
            added = account.AddFeed(remote.FeedUrl, remote.Name, folder, remote.FeedID.ToString());
            added.ExternalID = remote.FeedID.ToString();
            added.HomePageUrl = remote.HomePageUrl;
            added.FaviconUrl = remote.FaviconUrl;
            if (!string.IsNullOrEmpty(name)) added.EditedName = name;
            if (folder is not null)
                (added.FolderRelationship ??= new())[folder.NameForDisplay] = folder.NameForDisplay;
        });
        _initialFeeds.Add(remote.FeedID.ToString());
        return added;
    }

    internal void SyncFeeds(Account.Account account, IEnumerable<NewsBlurFeed> feeds)
    {
        var incoming = feeds.ToList();
        var ids = incoming.Select(feed => feed.FeedID.ToString()).ToHashSet(StringComparer.Ordinal);
        account.PerformBatchUpdate(() =>
        {
            foreach (var obsolete in account.FlattenedFeeds().Where(feed => !ids.Contains(feed.ExternalID ?? feed.FeedID)).ToList())
                account.RemoveFeed(obsolete);
            foreach (var remote in incoming)
            {
                if (string.IsNullOrEmpty(remote.FeedUrl)) throw new InvalidDataException("NewsBlur returned a feed without its address.");
                var feed = account.FlattenedFeeds().FirstOrDefault(f => f.ExternalID == remote.FeedID.ToString())
                    ?? account.ExistingFeedWithUrl(remote.FeedUrl)
                    ?? account.AddFeed(remote.FeedUrl, remote.Name, feedID: remote.FeedID.ToString());
                if (!string.IsNullOrEmpty(remote.Name)) feed.Name = remote.Name;
                feed.ExternalID = remote.FeedID.ToString();
                feed.HomePageUrl = remote.HomePageUrl;
                feed.FaviconUrl = remote.FaviconUrl;
            }
        });
    }
}
