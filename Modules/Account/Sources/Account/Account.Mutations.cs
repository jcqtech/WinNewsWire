using WinNewsWire.ActivityLog;

namespace WinNewsWire.Account;

public sealed partial class Account
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public Task<Folder> CreateFolderAsync(string name, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.CreateFolder, token =>
    {
        ValidateFolderManagement();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (ExistingFolderWithName(name.Trim()) is { } existing) return Task.FromResult(existing);
        return Delegate.CreateFolderAsync(this, name.Trim(), token);
    }, ct);

    public Task RenameFolderAsync(Folder folder, string name, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.RenameFolder, token =>
    {
        ValidateFolderManagement();
        ValidateExistingFolder(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (ExistingFolderWithName(name.Trim()) is { } existing && !ReferenceEquals(existing, folder))
            throw new InvalidOperationException("A folder with that name already exists.");
        return Delegate.RenameFolderAsync(this, folder, name.Trim(), token);
    }, ct);

    public Task RemoveFolderAsync(Folder folder, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.RemoveFolder, token =>
    {
        ValidateFolderManagement();
        ValidateExistingFolder(folder);
        return Delegate.RemoveFolderAsync(this, folder, token);
    }, ct);

    public Task RenameFeedAsync(Feed feed, string name, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.RenameFeed, token =>
    {
        feed = ValidateExistingFeed(feed);
        ArgumentNullException.ThrowIfNull(name);
        if (Type.Has(AccountBehavior.DisallowFeedRename))
            throw new NotSupportedException("This account does not allow feed renaming.");
        return Delegate.RenameFeedAsync(this, feed, name.Trim(), token);
    }, ct);

    public Task AddFeedAsync(Feed feed, Folder? destination, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.AddFeed, token =>
    {
        feed = ValidateExistingFeed(feed);
        ValidateDestination(destination);
        if ((destination?.Feeds ?? TopLevelFeeds).Contains(feed)) return Task.CompletedTask;
        ValidateFeedAddition(feed, destination);
        return Delegate.AddFeedAsync(this, feed, destination, token);
    }, ct);

    public Task<Feed> CopyFeedAsync(Feed source, Folder? destination, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.AddFeed, token => CopyFeedCoreAsync(source, destination, token), ct);

    public Task<Folder> CopyFolderAsync(Folder source, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.CreateFolder, async token =>
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateFolderManagement();
        if (source.AccountID == AccountID) throw new InvalidOperationException("Folders can be copied only to a different account.");
        ArgumentException.ThrowIfNullOrWhiteSpace(source.NameForDisplay);
        if (ExistingFolderWithName(source.NameForDisplay) is not null)
            throw new InvalidOperationException("A folder with that name already exists in the destination account.");
        var feeds = source.Feeds.ToArray();
        var destination = await Delegate.CreateFolderAsync(this, source.NameForDisplay, token);
        SaveAndNotifyStructure();
        foreach (var feed in feeds)
        {
            token.ThrowIfCancellationRequested();
            await CopyFeedCoreAsync(feed, destination, token);
            SaveAndNotifyStructure();
        }
        return destination;
    }, ct);

    private async Task<Feed> CopyFeedCoreAsync(Feed source, Folder? destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateDestination(destination);
        if (ExistingFeedWithUrl(source.Url) is { } existing)
        {
            if (!(destination?.Feeds ?? TopLevelFeeds).Contains(existing))
            {
                ValidateFeedAddition(existing, destination);
                await Delegate.AddFeedAsync(this, existing, destination, ct);
            }
            return existing;
        }
        return await Delegate.CreateFeedAsync(this, source.Url, source.NameForDisplay, destination, ct)
            ?? throw new FeedFinder.FeedNotFoundException();
    }

    public Task RemoveFeedAsync(Feed feed, Folder? container, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.RemoveFeed, token =>
    {
        feed = ValidateSource(feed, container);
        return Delegate.RemoveFeedAsync(this, feed, container, token);
    }, ct);

    public Task MoveFeedAsync(Feed feed, Folder? source, Folder? destination, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.MoveFeed, token =>
    {
        feed = ValidateSource(feed, source);
        ValidateDestination(destination);
        if (ReferenceEquals(source, destination)) return Task.CompletedTask;
        return Delegate.MoveFeedAsync(this, feed, source, destination, token);
    }, ct);

    public Task RestoreFeedAsync(Feed feed, Folder? container, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.RestoreFeed, token =>
    {
        ValidateFeed(feed);
        ValidateDestination(container);
        return Delegate.RestoreFeedAsync(this, feed, container, token);
    }, ct);

    public Task RestoreFolderAsync(Folder folder, CancellationToken ct = default)
        => PerformMutationAsync(ActivityKind.RestoreFolder, token =>
    {
        ValidateFolderManagement();
        ValidateFolder(folder);
        return Delegate.RestoreFolderAsync(this, folder, token);
    }, ct);

    internal void PreserveRestoredFeed(Feed original, Feed created)
    {
        if (_feedIDRedirects.TryGetValue(original.FeedID, out var canonicalID)) original = original.WithFeedID(canonicalID);
        original.ExternalID = created.ExternalID;
        original.FolderRelationship = created.FolderRelationship;
        original.Name = created.Name ?? original.Name;
        original.HomePageUrl = created.HomePageUrl ?? original.HomePageUrl;
        PerformBatchUpdate(() =>
        {
            foreach (var container in Folders.Select(f => f.Feeds).Prepend(TopLevelFeeds))
            {
                var index = container.IndexOf(created);
                if (index >= 0) container[index] = original;
            }
        });
    }

    internal void AddFeedToContainer(Feed feed, Folder? folder)
    {
        var feeds = folder?.Feeds ?? TopLevelFeeds;
        if (feeds.Contains(feed)) return;
        feeds.Add(feed);
        SaveAndNotifyStructure();
    }

    internal void RemoveFeedFromContainer(Feed feed, Folder? folder)
    {
        if ((folder?.Feeds ?? TopLevelFeeds).Remove(feed)) SaveAndNotifyStructure();
    }

    internal void MoveFeedBetweenContainers(Feed feed, Folder? source, Folder? destination)
        => PerformBatchUpdate(() =>
        {
            RemoveFeedFromContainer(feed, source);
            AddFeedToContainer(feed, destination);
        });

    private Task<T> PerformMutationAsync<T>(ActivityKind kind, Func<CancellationToken, Task<T>> work, CancellationToken ct)
        => RunOperationAsync(token =>
        {
            if (!IsActive) throw new InvalidOperationException("Activate this account before changing subscriptions.");
            return LogActivityAsync(kind, async () =>
            {
                var result = await work(token);
                SaveAndNotifyStructure();
                return result;
            });
        }, ct);

    private Task PerformMutationAsync(ActivityKind kind, Func<CancellationToken, Task> work, CancellationToken ct)
        => PerformMutationAsync(kind, async token => { await work(token); return true; }, ct);

    private void ValidateFeed(Feed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (feed.AccountID != AccountID) throw new ArgumentException("The feed belongs to a different account.", nameof(feed));
    }

    private Feed ValidateExistingFeed(Feed feed)
    {
        ValidateFeed(feed);
        return FlattenedFeeds().FirstOrDefault(existing => existing.FeedID == feed.FeedID)
            ?? ExistingFeedWithUrl(feed.Url) ?? throw new InvalidOperationException("This feed is no longer subscribed.");
    }

    private Feed ValidateSource(Feed feed, Folder? folder)
    {
        feed = ValidateExistingFeed(feed);
        if (folder is not null) ValidateExistingFolder(folder);
        if (!(folder?.Feeds ?? TopLevelFeeds).Contains(feed))
            throw new InvalidOperationException("The feed is no longer in this folder.");
        return feed;
    }

    private void ValidateFolder(Folder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.AccountID != AccountID) throw new ArgumentException("The folder belongs to a different account.", nameof(folder));
    }

    private void ValidateExistingFolder(Folder folder)
    {
        ValidateFolder(folder);
        if (!Folders.Contains(folder)) throw new InvalidOperationException("This folder no longer exists.");
    }

    private void ValidateDestination(Folder? folder)
    {
        if (folder is not null) ValidateExistingFolder(folder);
        else if (Type.Has(AccountBehavior.DisallowFeedInRootFolder))
            throw new InvalidOperationException("Choose a folder for this account.");
    }

    private void ValidateFeedAddition(Feed feed, Folder? destination)
    {
        if (Type.Has(AccountBehavior.DisallowFeedInMultipleFolders)
            && Folders.Any(folder => !ReferenceEquals(folder, destination) && folder.Feeds.Contains(feed)))
            throw new InvalidOperationException("This account only allows a feed in one folder. Move it instead.");
        if (destination is null && Type.Has(AccountBehavior.DisallowFeedCopyInRootFolder)
            && Folders.Any(folder => folder.Feeds.Contains(feed)))
            throw new InvalidOperationException("This account cannot copy a tagged feed to the top level.");
    }

    private void ValidateFolderManagement()
    {
        if (Type.Has(AccountBehavior.DisallowFolderManagement))
            throw new NotSupportedException("This account does not support folder management.");
    }
}
