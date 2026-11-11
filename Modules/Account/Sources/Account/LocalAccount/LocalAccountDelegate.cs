using WinNewsWire.FeedFinder;
using WinNewsWire.Core;
using WinNewsWire.ActivityLog;
using WinNewsWire.Web;

namespace WinNewsWire.Account;

/// <summary>
/// Port of <c>LocalAccountDelegate</c> + <c>InitialFeedDownloader</c>.
/// Single-feed discovery uses <see cref="Downloader"/>; batch refresh delegates
/// to <see cref="LocalAccountRefresher"/> which drives a <see cref="DownloadSession"/>.
/// </summary>
public sealed class LocalAccountDelegate : IAccountDelegate
{
    public AccountType Type => AccountType.OnMyMac;
    public bool SupportsOpmlImport => true;

    public Task ImportOpmlAsync(Account account, Parsers.ParserData data, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        account.LoadOpmlItems(Parsers.OpmlParser.Parse(data).Children, isManualImport: true);
        return Task.CompletedTask;
    }

    public async Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
    {
        var specs = await FeedFinder.FeedFinder.FindAsync(urlOrSite, ct);
        var best = FeedSpecifier.BestFeed(specs) ?? throw new FeedNotFoundException();
        if (account.ExistingFeedWithUrl(best.UrlString) is not null)
            throw new InvalidOperationException("This feed is already subscribed.");
        var response = await FeedFinder.FeedFinder.DownloadAndLogAsync(best.UrlString, ct);
        if (!response.Success || response.Data is not { Length: > 0 }) throw new FeedNotFoundException();
        var parsed = await Parsers.FeedParser.ParseAsync(new(best.UrlString, response.Data), ct)
            ?? throw new FeedNotFoundException();
        var changes = await account.Database.UpdateAsync(parsed.Items, best.UrlString);
        var feed = account.AddFeed(best.UrlString, parsed.Title, folder);
        feed.EditedName = name;
        Account.UpdateFeedMetadata(feed, parsed);
        feed.ETag = response.ConditionalGet?.ETag;
        feed.LastModified = response.ConditionalGet?.LastModified;
        if (response.ConditionalGet is not null) feed.ConditionalGetInfoDate = DateTime.UtcNow;
        feed.LastCheckDate = DateTime.UtcNow;
        feed.ContentHash = LocalAccountRefresher.ContentHashFor(response.Data);
        if (changes.New is { Count: > 0 } articles) account.RaiseNewArticlesDownloaded(articles);
        return feed;
    }

    public Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.RefreshAll, () => account.LocalRefresher.RefreshAllAsync(progress, ct));

    public Task<Folder> CreateFolderAsync(Account account, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(account.AddFolder(name));
    }

    public Task RenameFolderAsync(Account account, Folder folder, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        folder.Name = name;
        return Task.CompletedTask;
    }

    public Task RemoveFolderAsync(Account account, Folder folder, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        account.RemoveFolder(folder);
        return Task.CompletedTask;
    }

    public Task RenameFeedAsync(Account account, Feed feed, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        feed.EditedName = string.IsNullOrWhiteSpace(name) ? null : name;
        feed.OnDisplayNameChanged();
        return Task.CompletedTask;
    }

    public Task AddFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        account.AddFeedToContainer(feed, folder);
        return Task.CompletedTask;
    }

    public Task RemoveFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        account.RemoveFeedFromContainer(feed, folder);
        return Task.CompletedTask;
    }

    public Task MoveFeedAsync(Account account, Feed feed, Folder? source, Folder? destination, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        account.MoveFeedBetweenContainers(feed, source, destination);
        return Task.CompletedTask;
    }

    public Task RestoreFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        => AddFeedAsync(account, feed, folder, ct);

    public Task RestoreFolderAsync(Account account, Folder folder, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        account.RestoreFolder(folder);
        return Task.CompletedTask;
    }
}
