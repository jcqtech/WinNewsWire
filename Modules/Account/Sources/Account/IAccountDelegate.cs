using WinNewsWire.Core;

namespace WinNewsWire.Account;

/// <summary>Port of <c>AccountDelegate</c>.</summary>
public interface IAccountDelegate : IDisposable, IAsyncDisposable
{
    AccountType Type { get; }
    string? CredentialUsername => null;
    string? Server => null;
    void CredentialsChanged(Secrets.Credentials credentials) { }
    void ArticleStatusesQueued() { }
    void PrepareToSaveStructure(Account account) { }
    void StructureSaved(Account account) { }
    bool CanDisposeSynchronously => true;
    Task WaitForPendingWorkAsync(CancellationToken ct = default) => Task.CompletedTask;
    void IDisposable.Dispose() { }
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        ((IDisposable)this).Dispose();
        return ValueTask.CompletedTask;
    }
    /// <summary>When true, <see cref="Account.MarkAsync"/> also queues changes in the account's
    /// SyncDatabase so <see cref="SendArticleStatusAsync"/> can flush them to the remote service.</summary>
    bool SupportsRemoteSync => false;
    bool RequiresContinuousStatusPolling => false;
    bool UsesFeedBasedRetention => false;
    Task PublisherArticlesDeletedAsync(Account account, IReadOnlyCollection<string> ids, CancellationToken ct)
        => Task.CompletedTask;
    bool SupportsOpmlImport => false;
    Task ImportOpmlAsync(Account account, Parsers.ParserData data, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException("This account does not support OPML imports."));
    Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct);
    Task<Folder> CreateFolderAsync(Account account, string name, CancellationToken ct = default)
        => Task.FromException<Folder>(new NotSupportedException("Folder creation is not supported by this account."));
    Task RenameFolderAsync(Account account, Folder folder, string name, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException("Folder renaming is not supported by this account."));
    Task RemoveFolderAsync(Account account, Folder folder, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException("Folder deletion is not supported by this account."));
    Task RenameFeedAsync(Account account, Feed feed, string name, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException("Feed renaming is not supported by this account."));
    Task AddFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException("Adding an existing feed is not supported by this account."));
    Task RemoveFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException("Feed deletion is not supported by this account."));
    Task MoveFeedAsync(Account account, Feed feed, Folder? source, Folder? destination, CancellationToken ct = default)
        => Task.FromException(new NotSupportedException("Moving a feed is not supported by this account."));

    async Task RestoreFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        if (account.ExistingFeedWithUrl(feed.Url) is { } existing)
        {
            if (!(folder?.Feeds ?? account.TopLevelFeeds).Contains(existing))
                await AddFeedAsync(account, existing, folder, ct);
        }
        else
        {
            var created = await CreateFeedAsync(account, feed.Url, feed.EditedName, folder, ct)
                ?? throw new FeedFinder.FeedNotFoundException();
            if (!ReferenceEquals(created, feed)) account.PreserveRestoredFeed(feed, created);
        }
    }

    async Task RestoreFolderAsync(Account account, Folder folder, CancellationToken ct = default)
    {
        var feeds = folder.Feeds.ToList();
        var target = account.Folders.Contains(folder) ? folder : account.ExistingFolderWithName(folder.NameForDisplay);
        target ??= await CreateFolderAsync(account, folder.NameForDisplay, ct);
        foreach (var feed in feeds) await RestoreFeedAsync(account, feed, target, ct);
        if (!ReferenceEquals(target, folder))
        {
            account.PerformBatchUpdate(() =>
            {
                folder.ExternalID = target.ExternalID;
                folder.Feeds.Clear();
                folder.Feeds.AddRange(target.Feeds);
                account.RemoveFolder(target);
                account.RestoreFolder(folder);
            });
        }
    }
    Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct);
    Task VacuumDatabasesAsync(Account account, CancellationToken ct = default)
        => SupportsRemoteSync ? account.VacuumSyncDatabaseAsync(ct) : Task.CompletedTask;
    Task SendArticleStatusAsync(Account account, CancellationToken ct = default) => Task.CompletedTask;
    Task RefreshArticleStatusAsync(Account account, CancellationToken ct = default) => Task.CompletedTask;
    async Task<bool> SyncArticleStatusAsync(Account account, CancellationToken ct = default)
    {
        if (!SupportsRemoteSync) return false;
        await SendArticleStatusAsync(account, ct);
        await RefreshArticleStatusAsync(account, ct);
        return true;
    }
}
