using WinNewsWire.Account;

namespace WinNewsWire.ReaderAPI;

public sealed partial class ReaderAPIAccountDelegate
{
    public Task<Folder> CreateFolderAsync(Account.Account account, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(account.AddFolder(name));
    }

    public async Task RenameFolderAsync(Account.Account account, Folder folder, string name, CancellationToken ct = default)
    {
        var previousID = folder.ExternalID;
        if (previousID is not null) await API.RenameTagAsync(folder.NameForDisplay, name, ct);
        folder.Name = name;
        if (previousID is not null)
        {
            folder.ExternalID = "user/-/label/" + name;
            foreach (var feed in folder.Feeds)
                if (feed.FolderRelationship?.Remove(previousID, out var id) == true)
                    feed.FolderRelationship[folder.ExternalID] = id;
        }
    }

    public async Task RemoveFolderAsync(Account.Account account, Folder folder, CancellationToken ct = default)
    {
        foreach (var feed in folder.Feeds.ToList())
        {
            if (account.Folders.Count(f => f.Feeds.Contains(feed)) > 1)
                await API.ChangeSubscriptionAsync(SubscriptionID(feed), removeTag: folder.NameForDisplay, ct: ct);
            else
                await API.ChangeSubscriptionAsync(SubscriptionID(feed), unsubscribe: true, ct: ct);
            if (folder.ExternalID is { } id) feed.FolderRelationship?.Remove(id);
        }
        if (folder.ExternalID is { } externalID && API.Variant != ReaderAPIVariant.TheOldReader)
            await API.DeleteTagAsync(externalID, ct);
        account.RemoveFolder(folder);
    }

    public async Task RenameFeedAsync(Account.Account account, Feed feed, string name, CancellationToken ct = default)
    {
        await API.ChangeSubscriptionAsync(SubscriptionID(feed), title: name, ct: ct);
        feed.EditedName = string.IsNullOrEmpty(name) ? null : name;
        feed.OnDisplayNameChanged();
    }

    public async Task AddFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        if (folder is null)
        {
            if (!account.Folders.Any(f => f.Feeds.Contains(feed))) account.AddFeedToContainer(feed, null);
            return;
        }
        await API.ChangeSubscriptionAsync(SubscriptionID(feed), addTag: folder.NameForDisplay, ct: ct);
        folder.ExternalID ??= "user/-/label/" + folder.NameForDisplay;
        (feed.FolderRelationship ??= new())[folder.ExternalID] = SubscriptionID(feed);
        account.PerformBatchUpdate(() =>
        {
            account.RemoveFeedFromContainer(feed, null);
            account.AddFeedToContainer(feed, folder);
        });
    }

    public async Task RemoveFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        await API.ChangeSubscriptionAsync(SubscriptionID(feed), unsubscribe: true, ct: ct);
        account.RemoveFeed(feed);
    }

    public async Task MoveFeedAsync(Account.Account account, Feed feed, Folder? source, Folder? destination, CancellationToken ct = default)
    {
        await API.ChangeSubscriptionAsync(SubscriptionID(feed), addTag: destination?.NameForDisplay,
            removeTag: source?.NameForDisplay, ct: ct);
        if (source?.ExternalID is { } previous) feed.FolderRelationship?.Remove(previous);
        if (destination is not null)
        {
            destination.ExternalID ??= "user/-/label/" + destination.NameForDisplay;
            (feed.FolderRelationship ??= new())[destination.ExternalID] = SubscriptionID(feed);
        }
        account.MoveFeedBetweenContainers(feed, source, destination);
    }

    private static string SubscriptionID(Feed feed)
        => feed.ExternalID ?? throw new InvalidOperationException("The Reader API subscription ID is missing. Refresh the account.");
}
