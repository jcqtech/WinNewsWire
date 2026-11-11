using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;
using WinNewsWire.ActivityLog;

namespace WinNewsWire.Feedly;

/// <summary>
/// Port of <c>FeedlyAccountDelegate</c>.
///
/// The refresh pipeline mirrors the current Swift delegate, with asynchronous SQLite
/// and HTTP adapters and Windows credential protection.
///
/// OAuth: on 401, <see cref="FeedlyAPICaller"/> calls <see cref="ReauthorizeAsync"/> which reads
/// the stored refresh token, posts to <c>/v3/auth/token</c>, and updates both
/// <see cref="CredentialsManager"/> and the in-memory access token before retrying.
/// </summary>
public sealed partial class FeedlyAccountDelegate : IAccountDelegate
{
    public AccountType Type => AccountType.Feedly;
    public bool SupportsRemoteSync => true;
    public bool SupportsOpmlImport => false;
    public string? CredentialUsername => AccessToken?.Username ?? RefreshToken?.Username;

    public Task ImportOpmlAsync(Account.Account account, Parsers.ParserData data, CancellationToken ct = default)
        => _api.ImportOpmlAsync(data.Data, ct);

    /// <summary>Access token credentials. <c>Username</c> is the Feedly user ID.</summary>
    public Credentials? AccessToken
    {
        get => _api.AccessToken;
        set
        {
            lock (_reauthorizationLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _api.AccessToken = value;
                _reauthorizationFailedDefinitively = false;
                _credentialsVersion++;
            }
        }
    }

    /// <summary>Refresh-token credentials. <c>Username</c> is the Feedly user ID.</summary>
    public Credentials? RefreshToken
    {
        get => _refreshToken;
        set
        {
            lock (_reauthorizationLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _refreshToken = value;
                _reauthorizationFailedDefinitively = false;
                _credentialsVersion++;
            }
        }
    }

    public FeedlyAPICaller API => _api;

    private readonly FeedlyAPICaller _api;
    private readonly FeedlyClientConfig? _config;
    private readonly HttpClient? _authenticationHttp;
    private readonly TimeProvider _clock;

    public FeedlyAccountDelegate(Credentials? accessToken = null, Credentials? refreshToken = null, FeedlyClientConfig? config = null,
        HttpClient? http = null, TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _config = config;
        _authenticationHttp = http;
        _api = new FeedlyAPICaller(config?.Host ?? "cloud.feedly.com", http)
        {
            AccessToken = accessToken,
            ReauthorizeAsync = ReauthorizeAsync,
        };
        RefreshToken = refreshToken;
    }

    // --------------------- Authentication ---------------------

    /// <summary>Stores the new access+refresh tokens in memory and in DPAPI. Mirrors
    /// <c>FeedlyAccountDelegate+OAuth.swift</c>'s handling of
    /// <c>FeedlyOAuthAccessTokenResponse</c>.</summary>
    public void ApplyTokenResponse(FeedlyOAuthAccessTokenResponse resp)
    {
        lock (_reauthorizationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Feedly token persistence requires Windows credential protection.");
            var access = new Credentials(CredentialsType.OAuthAccessToken, resp.Id, resp.AccessToken);
            var refresh = string.IsNullOrEmpty(resp.RefreshToken) ? null : new Credentials(CredentialsType.OAuthRefreshToken, resp.Id, resp.RefreshToken);
            CredentialsManager.StoreMany(refresh is null ? [access] : [refresh, access], "Feedly");
            if (refresh is not null) RefreshToken = refresh;
            AccessToken = access;
        }
    }

    // --------------------- IAccountDelegate ---------------------

    public async Task<Feed?> CreateFeedAsync(Account.Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
    {
        var user = AccessToken?.Username ?? throw new CredentialsException("Sign in to Feedly before adding a subscription.");
        var collectionID = CollectionID(folder);
        var result = await SearchForFeedAsync(urlOrSite, ct);
        var feeds = await _api.AddFeedToCollectionAsync(result.FeedId, collectionID, name, ct);
        if (!feeds.Any(feed => feed.Id == result.FeedId)) throw new FeedFinder.FeedNotFoundException();
        await FeedlyFolderReconciliation.SyncFeedsForCollectionFoldersAsync(account, [(feeds, folder!)]);
        var added = folder!.Feeds.First(feed => feed.ExternalID == result.FeedId);
        if (!string.IsNullOrEmpty(name)) added.EditedName = name;
        var unread = await CollectStreamIDsAsync(FeedlyResourceIds.GlobalAllFor(user), true, DateTime.UtcNow.AddDays(-StreamFetchDaysLimit), ct);
        await RemoteSyncHelpers.ReconcileStatusesAsync(account, unread.IDs, null, unreadIsComplete: !unread.Truncated);
        await account.LogActivityAsync(ActivityKind.RefreshArticles, async () =>
        {
            var page = await _api.GetStreamContentsAsync(result.FeedId, ct: ct);
            if (page?.Items is null) throw new InvalidDataException("Feedly returned no stream contents.");
            return await SaveEntriesAsync(account, page.Items, restoreNewUnread: false);
        }, detail: added.NameForDisplay, successMessage: count => $"{count} new articles");
        await account.RecalculateUnreadCountsAsync();
        return added;
    }

    private async Task<FeedlyFeedsSearchResult> SearchForFeedAsync(string url, CancellationToken ct)
    {
        var locale = System.Globalization.CultureInfo.CurrentCulture.Name.Replace('-', '_');
        var result = (await _api.SearchFeedsAsync(url, count: 1, locale: locale, ct: ct)).Results.FirstOrDefault();
        if (result is not null) return result;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FeedFinder.FeedNotFoundException();
        var specifiers = await FeedFinder.FeedFinder.FindAsync(url, ct);
        var best = FeedFinder.FeedSpecifier.BestFeed(specifiers.Where(specifier => !specifier.UrlString.Contains("json", StringComparison.Ordinal)));
        if (best is null || best.UrlString == url) throw new FeedFinder.FeedNotFoundException();
        return (await _api.SearchFeedsAsync(best.UrlString, count: 1, locale: locale, ct: ct)).Results.FirstOrDefault()
            ?? throw new FeedFinder.FeedNotFoundException();
    }

    public Task SendArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => SendArticleStatusReturningCountAsync(account, ct);

    private Task<int> SendArticleStatusReturningCountAsync(Account.Account account, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.SendArticleStatuses, async () =>
    {
        int sent = 0;
        await RemoteSyncHelpers.FlushPendingAsync(account, async (key, flag, ids, c) =>
        {
            if (key == SyncDatabase.SyncStatus.SyncKey.Read && flag)
            {
                var articles = await account.Database.FetchArticlesByIDsAsync(ids);
                var dates = articles.ToDictionary(a => a.ArticleID, a => a.DatePublished is { } published && published > a.Status.DateArrived
                    ? published : a.Status.DateArrived);
                var unmarkable = UnmarkableAsReadArticleIDs(ids, dates, DateTime.UtcNow.AddDays(-MarkAsReadDaysLimit));
                if (unmarkable.Count > 0)
                {
                    System.Diagnostics.Trace.TraceInformation($"Feedly: dropping {unmarkable.Count} mark-as-read changes beyond its marker limit.");
                    await account.SyncDatabase.DeleteSelectedAsync(unmarkable, key);
                    ids = ids.Except(unmarkable).ToList();
                }
                if (ids.Count == 0) return;
            }
            var action = (key, flag) switch
            {
                (SyncDatabase.SyncStatus.SyncKey.Read, true) => FeedlyMarkAction.Read,
                (SyncDatabase.SyncStatus.SyncKey.Read, false) => FeedlyMarkAction.Unread,
                (SyncDatabase.SyncStatus.SyncKey.Starred, true) => FeedlyMarkAction.Saved,
                (SyncDatabase.SyncStatus.SyncKey.Starred, false) => FeedlyMarkAction.Unsaved,
                _ => (FeedlyMarkAction?)null,
            };
            if (action is FeedlyMarkAction act)
            {
                await _api.MarkAsync(ids, act, c);
                sent += ids.Count;
            }
        }, ct, batchSize: (_, _) => 300);
        return sent;
    }, successMessage: SendStatusMessage, durationIsSignificant: count => count > 0);

    public const int MarkAsReadDaysLimit = 31;

    internal static HashSet<string> UnmarkableAsReadArticleIDs(IEnumerable<string> articleIDs,
        IReadOnlyDictionary<string, DateTime> datesByArticleID, DateTime cutoffDate)
        => articleIDs.Where(id => !datesByArticleID.TryGetValue(id, out var date) || date < cutoffDate).ToHashSet(StringComparer.Ordinal);

}
