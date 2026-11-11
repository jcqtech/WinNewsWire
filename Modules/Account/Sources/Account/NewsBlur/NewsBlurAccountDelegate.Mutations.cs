using WinNewsWire.Account;

namespace WinNewsWire.NewsBlur;

public sealed partial class NewsBlurAccountDelegate
{
    public async Task<Folder> CreateFolderAsync(Account.Account account, string name, CancellationToken ct = default)
    {
        await API.AddFolderAsync(name, ct);
        return account.AddFolder(name);
    }

    public async Task RenameFolderAsync(Account.Account account, Folder folder, string name, CancellationToken ct = default)
    {
        var previous = folder.NameForDisplay;
        await API.RenameFolderAsync(previous, name, ct);
        folder.Name = name;
        foreach (var feed in folder.Feeds)
        {
            feed.FolderRelationship?.Remove(previous);
            (feed.FolderRelationship ??= new())[name] = name;
        }
    }

    public async Task RemoveFolderAsync(Account.Account account, Folder folder, CancellationToken ct = default)
    {
        var uniqueFeeds = folder.Feeds.Where(feed => !account.TopLevelFeeds.Contains(feed)
            && !account.Folders.Any(other => !ReferenceEquals(other, folder) && other.Feeds.Contains(feed)));
        await API.RemoveFolderAsync(folder.NameForDisplay, uniqueFeeds.Select(SubscriptionID), ct);
        foreach (var feed in folder.Feeds) feed.FolderRelationship?.Remove(folder.NameForDisplay);
        account.RemoveFolder(folder);
    }

    public async Task RenameFeedAsync(Account.Account account, Feed feed, string name, CancellationToken ct = default)
    {
        await API.RenameFeedAsync(SubscriptionID(feed), name, ct);
        feed.EditedName = string.IsNullOrEmpty(name) ? null : name;
        feed.OnDisplayNameChanged();
    }

    public async Task AddFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        var remote = await API.AddUrlAsync(feed.Url, folder?.NameForDisplay, ct);
        feed.ExternalID = remote.FeedID.ToString();
        if (folder is not null)
            (feed.FolderRelationship ??= new())[folder.NameForDisplay] = folder.NameForDisplay;
        account.AddFeedToContainer(feed, folder);
    }

    public async Task RemoveFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        await API.DeleteFeedAsync(SubscriptionID(feed), folder?.NameForDisplay, ct);
        account.RemoveFeed(feed);
    }

    public async Task MoveFeedAsync(Account.Account account, Feed feed, Folder? source, Folder? destination, CancellationToken ct = default)
    {
        await API.MoveFeedAsync(SubscriptionID(feed), source?.NameForDisplay, destination?.NameForDisplay, ct);
        if (source is not null) feed.FolderRelationship?.Remove(source.NameForDisplay);
        if (destination is not null)
            (feed.FolderRelationship ??= new())[destination.NameForDisplay] = destination.NameForDisplay;
        account.MoveFeedBetweenContainers(feed, source, destination);
    }

    private static string SubscriptionID(Feed feed)
        => feed.ExternalID ?? throw new InvalidOperationException("The NewsBlur feed ID is missing. Refresh the account.");
}
