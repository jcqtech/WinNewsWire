using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Secrets;
using WinNewsWire.ActivityLog;

namespace WinNewsWire.ReaderAPI;

/// <summary>Port of <c>ReaderAPIAccountDelegate</c> (core sync loop). Handles
/// FreshRSS / Inoreader / BazQux / The Old Reader and any other Google-Reader-compatible host.</summary>
public sealed partial class ReaderAPIAccountDelegate : IAccountDelegate
{
    public AccountType Type { get; }
    public bool SupportsRemoteSync => true;
    public bool SupportsOpmlImport => true;
    public string? CredentialUsername => API.Credentials?.Username;
    public string? Server => API.Host;
    public ReaderAPICaller API { get; }
    private readonly HashSet<string> _initialFeeds = new(StringComparer.Ordinal);

    public Task ImportOpmlAsync(Account.Account account, Parsers.ParserData data, CancellationToken ct = default)
        => API.ImportOpmlAsync(data.Data, ct);

    public ReaderAPIAccountDelegate(ReaderAPIVariant variant, string? host = null, Credentials? credentials = null, string? authToken = null,
        HttpClient? http = null)
    {
        Type = variant.ToAccountType();
        API = new ReaderAPICaller(variant, host, http)
        {
            Credentials = credentials, AuthToken = authToken,
            ApplicationID = Environment.GetEnvironmentVariable("INOREADER_APP_ID"),
            ApplicationKey = Environment.GetEnvironmentVariable("INOREADER_APP_KEY"),
        };
    }

    public void CredentialsChanged(Credentials credentials)
    {
        if (credentials.Type == CredentialsType.ReaderApiKey) API.AuthToken = credentials.Secret;
        else { API.Credentials = credentials; API.AuthToken = null; }
    }

    public void Dispose() => API.Dispose();

    public Task SendArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => SendArticleStatusReturningCountAsync(account, ct);

    private Task<int> SendArticleStatusReturningCountAsync(Account.Account account, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.SendArticleStatuses, async () =>
    {
        int sent = 0;
        await RemoteSyncHelpers.FlushPendingAsync(account, async (key, flag, ids, c) =>
        {
            var unsendable = ids.Where(id => !ReaderAPICaller.CanEncodeItemID(id, API.Variant)).ToList();
            if (unsendable.Count > 0)
            {
                System.Diagnostics.Trace.TraceError($"Reader API dropped {unsendable.Count} status changes with unencodable article IDs.");
                await account.SyncDatabase.DeleteSelectedAsync(unsendable, key);
                ids = ids.Except(unsendable).ToList();
            }
            if (ids.Count == 0) return;
            switch ((key, flag))
            {
                case (SyncDatabase.SyncStatus.SyncKey.Read, true): await API.MarkAsReadAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Read, false): await API.MarkAsUnreadAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, true): await API.MarkAsStarredAsync(ids, c); break;
                case (SyncDatabase.SyncStatus.SyncKey.Starred, false): await API.MarkAsUnstarredAsync(ids, c); break;
            }
            sent += ids.Count;
        }, ct, batchSize: (_, _) => 1000);
        return sent;
    });

    public async Task<bool> SyncArticleStatusAsync(Account.Account account, CancellationToken ct = default)
    {
        if (API.RateLimiter.ShouldSkip()) return false;
        try
        {
            var sent = await SendArticleStatusReturningCountAsync(account, ct);
            if (API.Variant == ReaderAPIVariant.Inoreader) return sent > 0;
            var changed = await RefreshArticleStatusReturningCountAsync(account, ct, respectQuota: true);
            return sent > 0 || changed > 0;
        }
        catch (Exception error) when (API.RateLimiter.IsRateLimitError(error))
        {
            if (!API.RateLimiter.ShouldSkip()) API.RateLimiter.NoteRateLimited(error);
            return false;
        }
    }

    public async Task<Feed?> CreateFeedAsync(Account.Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
    {
        var subscription = await API.CreateSubscriptionAsync(urlOrSite, ct);
        if (folder is not null) await API.ChangeSubscriptionAsync(subscription.Id, addTag: folder.NameForDisplay, ct: ct);
        if (!string.IsNullOrEmpty(name)) await API.ChangeSubscriptionAsync(subscription.Id, title: name, ct: ct);
        Feed? created = null;
        account.PerformBatchUpdate(() =>
        {
            created = account.AddFeed(subscription.Url, DecodeFullwidth(subscription.Title), folder, subscription.Id);
            created.ExternalID = subscription.Id;
            created.HomePageUrl = subscription.HomePageUrl;
            created.IconUrl = subscription.IconUrl;
            if (!string.IsNullOrEmpty(name)) created.EditedName = name;
            if (folder is not null)
            {
                folder.ExternalID ??= "user/-/label/" + folder.NameForDisplay;
                (created.FolderRelationship ??= new())[folder.ExternalID] = subscription.Id;
            }
        });
        _initialFeeds.Add(subscription.Id);
        return created;
    }
}
