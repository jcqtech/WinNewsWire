using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;

namespace WinNewsWire.Feedly;

/// <summary>
/// Port of <c>FeedlyAccountDelegate</c>.
///
/// The Swift original uses a custom main-thread <c>OperationQueue</c> with ~25 discrete operations
/// (see <c>FeedlySyncAllOperation</c>). On Windows, <see cref="System.Threading.Tasks.Task"/>
/// and <c>async/await</c> already express the same ordering/dependency semantics declaratively,
/// so we fold the whole pipeline into <see cref="RefreshAllAsync"/>. Every stage is a separate
/// <c>private async Task</c> method named after the matching Swift operation file for easy
/// cross-referencing.
///
/// OAuth: on 401, <see cref="FeedlyAPICaller"/> calls <see cref="ReauthorizeAsync"/> which reads
/// the stored refresh token, posts to <c>/v3/auth/token</c>, and updates both
/// <see cref="CredentialsManager"/> and the in-memory access token before retrying.
/// </summary>
public sealed class FeedlyAccountDelegate : IAccountDelegate
{
    public AccountType Type => AccountType.Feedly;
    public bool SupportsRemoteSync => true;

    /// <summary>Access token credentials. <c>Username</c> is the Feedly user ID.</summary>
    public Credentials? AccessToken { get => _api.AccessToken; set => _api.AccessToken = value; }

    /// <summary>Refresh-token credentials. <c>Username</c> is the Feedly user ID.</summary>
    public Credentials? RefreshToken { get; set; }

    public FeedlyAPICaller API => _api;

    private readonly FeedlyAPICaller _api;
    private readonly FeedlyClientConfig? _config;

    public FeedlyAccountDelegate(Credentials? accessToken = null, Credentials? refreshToken = null, FeedlyClientConfig? config = null)
    {
        _config = config;
        _api = new FeedlyAPICaller(config?.Host ?? "cloud.feedly.com")
        {
            AccessToken = accessToken,
            ReauthorizeAsync = ReauthorizeAsync,
        };
        RefreshToken = refreshToken;
    }

    // --------------------- Authentication ---------------------

    /// <summary>Port of <c>FeedlyAccountDelegate.refreshAccessToken(_:_:)</c> +
    /// <c>FeedlyRefreshAccessTokenOperation</c>. Invoked by the API caller on any 401.</summary>
    private async Task<bool> ReauthorizeAsync(CancellationToken ct)
    {
        if (_config is null || RefreshToken is not { Secret: { Length: > 0 } rt }) return false;
        FeedlyOAuthAccessTokenResponse resp;
        try { resp = await FeedlyBrowserAuth.RefreshTokenAsync(_config, rt, http: null, ct); }
        catch { return false; }

        ApplyTokenResponse(resp);
        return true;
    }

    /// <summary>Stores the new access+refresh tokens in memory and in DPAPI. Mirrors
    /// <c>FeedlyAccountDelegate+OAuth.swift</c>'s handling of
    /// <c>FeedlyOAuthAccessTokenResponse</c>.</summary>
    public void ApplyTokenResponse(FeedlyOAuthAccessTokenResponse resp)
    {
        var access = new Credentials(CredentialsType.OAuthAccessToken, resp.Id, resp.AccessToken);
        _api.AccessToken = access;
        try { CredentialsManager.Store(access, "Feedly"); } catch { }

        if (!string.IsNullOrEmpty(resp.RefreshToken))
        {
            var refresh = new Credentials(CredentialsType.OAuthRefreshToken, resp.Id, resp.RefreshToken!);
            RefreshToken = refresh;
            try { CredentialsManager.Store(refresh, "Feedly"); } catch { }
        }
    }

    // --------------------- IAccountDelegate ---------------------

    public async Task<Feed?> CreateFeedAsync(Account.Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
    {
        // Port of FeedlyAddNewFeedOperation: ensure the feed exists in Feedly, then reflect it locally.
        var feedId = FeedlyResourceIds.FeedIdForUrl(urlOrSite);
        if (folder is not null && TryFindCollectionId(folder) is { Length: > 0 } collectionId)
        {
            try { await _api.AddFeedToCollectionAsync(feedId, collectionId, name, ct); }
            catch { /* best-effort — local feed is still created so the user can retry */ }
        }
        var feed = account.AddFeed(FeedlyResourceIds.UrlFromFeedId(feedId), name, folder);
        feed.ExternalID = feedId;
        return feed;
    }

    /// <summary>Returns the Feedly collection id stamped on this folder by
    /// <see cref="MirrorCollectionsAsFolders"/>. Mirrors Swift's
    /// <c>folder.externalID = parser.externalID</c> in <c>FeedlyMirrorCollectionsAsFoldersOperation</c>.</summary>
    private static string? TryFindCollectionId(Folder folder)
        => string.IsNullOrEmpty(folder.ExternalID) ? null : folder.ExternalID;

    public Task SendArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => RemoteSyncHelpers.FlushPendingAsync(account, async (key, flag, ids, c) =>
        {
            var action = (key, flag) switch
            {
                (SyncDatabase.SyncStatus.SyncKey.Read, true) => FeedlyMarkAction.Read,
                (SyncDatabase.SyncStatus.SyncKey.Read, false) => FeedlyMarkAction.Unread,
                (SyncDatabase.SyncStatus.SyncKey.Starred, true) => FeedlyMarkAction.Saved,
                (SyncDatabase.SyncStatus.SyncKey.Starred, false) => FeedlyMarkAction.Unsaved,
                _ => (FeedlyMarkAction?)null,
            };
            if (action is FeedlyMarkAction act) await _api.MarkAsync(ids, act, c);
        }, ct);

    /// <summary>Main sync pipeline. Port of <c>FeedlySyncAllOperation</c>.
    /// All stages share the same <see cref="FeedlyAPICaller"/> which will transparently refresh
    /// the access token on 401.</summary>
    public async Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        if (AccessToken is null) return;

        // 1. Flush any pending local read/starred changes first (Swift: FeedlySendArticleStatusesOperation).
        await SendArticleStatusAsync(account, ct);

        // 2. Pull collections (Swift: FeedlyGetCollectionsOperation + MirrorCollectionsAsFolders + CreateFeedsForCollectionFolders).
        var collections = await _api.GetCollectionsAsync(ct);
        MirrorCollectionsAsFolders(account, collections);

        // 3. Pull stream IDs for global.all / starred / updated in parallel (Swift:
        //    FeedlyIngestStreamArticleIdsOperation + IngestUnreadArticleIdsOperation +
        //    IngestStarredArticleIdsOperation + GetUpdatedArticleIdsOperation).
        var userId = AccessToken.Username;
        var allStream = FeedlyResourceIds.GlobalAllFor(userId);
        var savedStream = FeedlyResourceIds.GlobalSavedFor(userId);

        var unreadTask = FetchAllStreamIdsAsync(allStream, unreadOnly: true, newerThan: null, ct);
        var starredTask = FetchAllStreamIdsAsync(savedStream, unreadOnly: null, newerThan: null, ct);

        var unreadIds = await unreadTask;
        var starredIds = await starredTask;

        // 4. Reconcile local read/starred state with the server-truth sets (Swift: the ingest ops do this in-place).
        await ReconcileStatusesAsync(account, unreadIds, starredIds);

        // 5. Download recent stream contents (unread-only pulls the user's reading list body).
        //    Swift equivalent: FeedlyDownloadArticlesOperation calls GetEntriesService.getEntries for
        //    the union of missing + updated IDs. We use /v3/streams/contents which is simpler and
        //    returns the same payload as getEntries for the union of recent/unread items.
        await DownloadRecentArticlesAsync(account, allStream, ct);

        await account.RecalculateUnreadCountsAsync();
        progress?.Report(new ProgressInfo(0, 1, 1));
    }

    // --------------------- Pipeline stages ---------------------

    /// <summary>Port of <c>FeedlyMirrorCollectionsAsFoldersOperation</c> +
    /// <c>FeedlyCreateFeedsForCollectionFoldersOperation</c>.</summary>
    private static void MirrorCollectionsAsFolders(Account.Account account, List<FeedlyCollection> collections)
    {
        var existingFoldersByName = account.Folders.ToDictionary(f => f.Name ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var c in collections)
        {
            if (!existingFoldersByName.TryGetValue(c.Label, out var folder))
            {
                folder = account.AddFolder(c.Label);
                existingFoldersByName[c.Label] = folder;
            }
            // Stamp the Feedly collection id onto the folder so CreateFeedAsync can later tag
            // new feeds with their server collection. Mirrors Swift's `folder.externalID = parser.externalID`.
            folder.ExternalID = c.Id;
            foreach (var ff in c.Feeds)
            {
                var url = FeedlyResourceIds.UrlFromFeedId(ff.Id);
                if (account.ExistingFeedWithUrl(url) is null)
                {
                    var feed = account.AddFeed(url, ff.Title, folder);
                    feed.ExternalID = ff.Id;
                    feed.HomePageUrl = ff.Website;
                }
            }
        }
    }

    /// <summary>Port of <c>FeedlyGetStreamIdsOperation</c> paginating through <c>continuation</c>.</summary>
    private async Task<HashSet<string>> FetchAllStreamIdsAsync(string streamId, bool? unreadOnly, DateTime? newerThan, CancellationToken ct)
    {
        var all = new HashSet<string>();
        string? cont = null;
        do
        {
            var page = await _api.GetStreamIdsAsync(streamId, cont, newerThan, unreadOnly, ct: ct);
            if (page is null) break;
            foreach (var id in page.Ids) all.Add(id);
            cont = page.Continuation;
        } while (!string.IsNullOrEmpty(cont));
        return all;
    }

    /// <summary>Port of the in-place reconciliation done by
    /// <c>FeedlyIngestUnreadArticleIdsOperation</c> and <c>FeedlyIngestStarredArticleIdsOperation</c>:
    /// the set of unread IDs from the server becomes the local unread set — articles the server
    /// considers unread are marked unread, and articles we locally hold as unread that the server
    /// no longer reports become read. Same pattern for starred.</summary>
    private static async Task ReconcileStatusesAsync(Account.Account account, HashSet<string> serverUnread, HashSet<string> serverStarred)
    {
        var feedIDs = account.FlattenedFeeds().Select(f => f.FeedID).ToList();

        // Unread: apply the positive set, then flip the complement back to read.
        if (serverUnread.Count > 0)
            await account.Database.MarkAsync(serverUnread, ArticleStatus.Key.Read, false);
        var localUnread = await account.Database.FetchUnreadArticleIDsAsync(feedIDs);
        localUnread.ExceptWith(serverUnread);
        if (localUnread.Count > 0)
            await account.Database.MarkAsync(localUnread, ArticleStatus.Key.Read, true);

        // Starred: apply the positive set, then unstar anything the server no longer has saved.
        if (serverStarred.Count > 0)
            await account.Database.MarkAsync(serverStarred, ArticleStatus.Key.Starred, true);
        var localStarred = await account.Database.FetchStarredArticleIDsAsync(feedIDs);
        localStarred.ExceptWith(serverStarred);
        if (localStarred.Count > 0)
            await account.Database.MarkAsync(localStarred, ArticleStatus.Key.Starred, false);
    }

    /// <summary>Port of <c>FeedlyDownloadArticlesOperation</c>. Pulls the reading list stream
    /// and upserts articles into the local DB.</summary>
    private async Task DownloadRecentArticlesAsync(Account.Account account, string streamId, CancellationToken ct)
    {
        var stream = await _api.GetStreamContentsAsync(streamId, count: 250, ct: ct);
        if (stream is null || stream.Items.Count == 0) return;

        var articles = new List<Article>(stream.Items.Count);
        foreach (var e in stream.Items)
        {
            var streamFeedId = e.Origin?.StreamId;
            var feed = streamFeedId is null ? null
                : account.FlattenedFeeds().FirstOrDefault(f => f.ExternalID == streamFeedId);
            var localFeedID = feed?.FeedID ?? FeedlyResourceIds.UrlFromFeedId(streamFeedId ?? e.Id);

            var status = new ArticleStatus(e.Id,
                read: !e.Unread,
                starred: e.Tags?.Any(t => t.Id.EndsWith("/tag/global.saved", StringComparison.Ordinal)) == true,
                dateArrived: e.DatePublished);
            articles.Add(new Article(
                account.AccountID,
                articleID: e.Id,
                feedID: localFeedID,
                uniqueID: e.Id,
                title: e.Title,
                contentHtml: e.ContentHtml,
                contentText: null,
                markdown: null,
                url: e.ExternalUrl,
                externalURL: null,
                summary: null,
                imageURL: null,
                datePublished: e.DatePublished,
                dateModified: e.DateModified,
                authors: string.IsNullOrWhiteSpace(e.Author) ? null
                    : new HashSet<Author> { Author.Create(null, e.Author, null, null, null)! },
                attachments: null, status: status));
        }
        var newArticles = await account.Database.UpdateArticlesAsync(articles);
        if (newArticles.Count > 0) account.RaiseNewArticlesDownloaded(newArticles);
    }
}
