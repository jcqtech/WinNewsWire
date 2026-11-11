using WinNewsWire.Account;

namespace WinNewsWire.Feedbin;

public sealed partial class FeedbinAccountDelegate
{
    public Task<Folder> CreateFolderAsync(Account.Account account, string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(account.AddFolder(name));
    }

    public async Task RenameFolderAsync(Account.Account account, Folder folder, string name, CancellationToken ct = default)
    {
        var previous = folder.NameForDisplay;
        if (folder.Feeds.Count > 0) await API.RenameTagAsync(previous, name, ct);
        foreach (var feed in folder.Feeds)
            if (feed.FolderRelationship?.Remove(previous, out var id) == true)
                feed.FolderRelationship[name] = id;
        folder.Name = name;
    }

    public async Task RemoveFolderAsync(Account.Account account, Folder folder, CancellationToken ct = default)
    {
        var feeds = folder.Feeds.ToList();
        foreach (var feed in feeds) await RemoveFeedAsync(account, feed, folder, ct);
        account.RemoveFolder(folder);
        folder.Feeds.AddRange(feeds.Where(feed => !folder.Feeds.Contains(feed)));
    }

    public async Task RenameFeedAsync(Account.Account account, Feed feed, string name, CancellationToken ct = default)
    {
        await API.RenameSubscriptionAsync(SubscriptionID(feed), name, ct);
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
        var id = await API.CreateTaggingAsync(await ServiceFeedIDAsync(feed, ct), folder.NameForDisplay, ct);
        (feed.FolderRelationship ??= new())[folder.NameForDisplay] = id.ToString();
        account.PerformBatchUpdate(() =>
        {
            account.RemoveFeedFromContainer(feed, null);
            account.AddFeedToContainer(feed, folder);
        });
    }

    public async Task RemoveFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
    {
        if (folder is not null && account.Folders.Count(f => f.Feeds.Contains(feed)) > 1)
        {
            if (await TaggingIDAsync(feed, folder, ct) is { } id) await API.DeleteTaggingAsync(id, ct);
            feed.FolderRelationship?.Remove(folder.NameForDisplay);
            account.RemoveFeedFromContainer(feed, folder);
        }
        else
        {
            await API.DeleteSubscriptionAsync(SubscriptionID(feed), ct);
            account.RemoveFeed(feed);
        }
    }

    public async Task MoveFeedAsync(Account.Account account, Feed feed, Folder? source, Folder? destination, CancellationToken ct = default)
    {
        long? oldTag = source is null ? null : await TaggingIDAsync(feed, source, ct);
        bool alreadyInDestination = destination?.Feeds.Contains(feed) == true;
        long? newTag = destination is null || alreadyInDestination ? null
            : await API.CreateTaggingAsync(await ServiceFeedIDAsync(feed, ct), destination.NameForDisplay, ct);
        try
        {
            if (oldTag is { } id) await API.DeleteTaggingAsync(id, ct);
        }
        catch (Exception original)
        {
            if (newTag is { } id)
            {
                try { await API.DeleteTaggingAsync(id, CancellationToken.None); }
                catch (Exception rollback) { throw new AggregateException("The move and its rollback both failed.", original, rollback); }
            }
            throw;
        }
        if (source is not null) feed.FolderRelationship?.Remove(source.NameForDisplay);
        if (destination is not null && newTag is { } tagging)
            (feed.FolderRelationship ??= new())[destination.NameForDisplay] = tagging.ToString();
        account.PerformBatchUpdate(() =>
        {
            account.RemoveFeedFromContainer(feed, source);
            if (destination is not null || !account.Folders.Any(f => f.Feeds.Contains(feed)))
                account.AddFeedToContainer(feed, destination);
        });
    }

    private static long SubscriptionID(Feed feed)
        => long.TryParse(feed.ExternalID, out var id) ? id
            : throw new InvalidOperationException("The Feedbin subscription ID is missing. Refresh the account.");

    private async Task<long> ServiceFeedIDAsync(Feed feed, CancellationToken ct)
    {
        if (long.TryParse(feed.FeedID, out var id)) return id;
        var known = _feedsByServiceID.FirstOrDefault(pair => pair.Value.Equals(feed));
        if (known.Value is not null) return known.Key;
        var subscriptions = (await API.RetrieveSubscriptionsAsync(ct, forceRefresh: true)).Items
            ?? throw new InvalidDataException("Feedbin returned no subscription snapshot.");
        var subscription = subscriptions.FirstOrDefault(s => s.FeedUrl == feed.Url);
        return subscription?.FeedID ?? throw new InvalidOperationException("The Feedbin feed could not be found.");
    }

    private async Task<long?> TaggingIDAsync(Feed feed, Folder folder, CancellationToken ct)
    {
        if (feed.FolderRelationship?.TryGetValue(folder.NameForDisplay, out var value) == true && long.TryParse(value, out var id))
            return id;
        var feedID = await ServiceFeedIDAsync(feed, ct);
        var taggings = (await API.RetrieveTaggingsAsync(ct, forceRefresh: true)).Items
            ?? throw new InvalidDataException("Feedbin returned no tagging snapshot.");
        return taggings.FirstOrDefault(t => t.FeedID == feedID && t.Name == folder.NameForDisplay)?.TaggingID;
    }
}
