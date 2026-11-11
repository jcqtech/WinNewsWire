using System.Net;
using WinNewsWire.Account;

namespace WinNewsWire.Feedly;

public sealed partial class FeedlyAccountDelegate
{
    public async Task<Folder> CreateFolderAsync(Account.Account account, string name, CancellationToken ct = default)
    {
        var collection = await _api.CreateCollectionAsync(name, ct);
        var folder = account.AddFolder(new FeedlyCollectionParser(collection).FolderName);
        folder.ExternalID = collection.Id;
        return folder;
    }

    public async Task RenameFolderAsync(Account.Account account, Folder folder, string name, CancellationToken ct = default)
    {
        var collection = await _api.RenameCollectionAsync(CollectionID(folder), name, ct);
        folder.Name = new FeedlyCollectionParser(collection).FolderName;
    }

    public async Task RemoveFolderAsync(Account.Account account, Folder folder, CancellationToken ct = default)
    {
        await _api.DeleteCollectionAsync(CollectionID(folder), ct);
        account.RemoveFolder(folder);
    }

    public async Task RenameFeedAsync(Account.Account account, Feed feed, string name, CancellationToken ct = default)
    {
        var folder = account.Folders.FirstOrDefault(f => f.Feeds.Contains(feed));
        await _api.AddFeedToCollectionAsync(ResourceID(feed), CollectionID(folder), name, ct);
        feed.EditedName = string.IsNullOrEmpty(name) ? null : name;
        feed.OnDisplayNameChanged();
    }

    public async Task AddFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        var feeds = await _api.AddFeedToCollectionAsync(ResourceID(feed), CollectionID(folder), feed.EditedName, ct);
        if (!feeds.Any(item => item.Id == ResourceID(feed))) throw new FeedFinder.FeedNotFoundException();
        await FeedlyFolderReconciliation.SyncFeedsForCollectionFoldersAsync(account, [(feeds, folder!)]);
    }

    public async Task RemoveFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        var collectionID = CollectionID(folder);
        try { await _api.RemoveFeedFromCollectionAsync(ResourceID(feed), collectionID, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            System.Diagnostics.Trace.TraceInformation($"Feedly no longer recognizes the subscription: {feed.Url}");
        }
        account.RemoveFeedFromContainer(feed, folder);
    }

    public async Task MoveFeedAsync(Account.Account account, Feed feed, Folder? source, Folder? destination, CancellationToken ct = default)
    {
        var sourceID = CollectionID(source);
        var destinationID = CollectionID(destination);
        var id = ResourceID(feed);
        bool alreadyInDestination = destination!.Feeds.Contains(feed);
        if (!alreadyInDestination)
        {
            var feeds = await _api.AddFeedToCollectionAsync(id, destinationID, feed.EditedName, ct);
            if (!feeds.Any(item => item.Id == id)) throw new FeedFinder.FeedNotFoundException();
        }
        try { await _api.RemoveFeedFromCollectionAsync(id, sourceID, ct); }
        catch (Exception original)
        {
            if (!alreadyInDestination)
            {
                try { await _api.RemoveFeedFromCollectionAsync(id, destinationID, CancellationToken.None); }
                catch (Exception rollback) { throw new AggregateException("The move and its rollback both failed.", original, rollback); }
            }
            throw;
        }
        account.MoveFeedBetweenContainers(feed, source, destination);
    }

    private static string CollectionID(Folder? folder)
        => folder?.ExternalID is { Length: > 0 } id ? id
            : throw new InvalidOperationException("Choose a Feedly folder with a server collection ID.");

    private static string ResourceID(Feed feed) => feed.ExternalID ?? FeedlyResourceIds.FeedIdForUrl(feed.Url);
}
