using WinNewsWire.Account;

namespace WinNewsWire.Feedly;

internal static class FeedlyFolderReconciliation
{
    internal static IReadOnlyList<(IReadOnlyList<FeedlyFeed> Feeds, Folder Folder)> MirrorCollectionsAsFolders(
        Account.Account account, IReadOnlyList<FeedlyCollection> collections)
    {
        var previous = account.Folders.ToList();
        var claimed = new HashSet<Folder>();
        var byID = new Dictionary<string, Folder>(StringComparer.Ordinal);
        var pairs = new List<(IReadOnlyList<FeedlyFeed>, Folder)>();
        account.PerformBatchUpdate(() =>
        {
            foreach (var collection in collections)
            {
                var folder = previous.FirstOrDefault(f => f.ExternalID == collection.Id && !claimed.Contains(f));
                if (folder is null) continue;
                claimed.Add(folder);
                byID[collection.Id] = folder;
                folder.Name = new FeedlyCollectionParser(collection).FolderName;
            }
            foreach (var collection in collections)
            {
                if (!byID.TryGetValue(collection.Id, out var folder))
                {
                    var name = new FeedlyCollectionParser(collection).FolderName;
                    var candidate = name;
                    int suffix = 2;
                    while (true)
                    {
                        folder = account.AddFolder(candidate);
                        if (!claimed.Contains(folder)) break;
                        candidate = $"{name} ({suffix++})";
                    }
                    claimed.Add(folder);
                    folder.ExternalID = collection.Id;
                }
                pairs.Add((collection.Feeds, folder));
            }
            foreach (var folder in previous.Except(claimed)) account.RemoveFolder(folder);
        });
        return pairs;
    }

    internal static async Task SyncFeedsForCollectionFoldersAsync(Account.Account account,
        IEnumerable<(IReadOnlyList<FeedlyFeed> Feeds, Folder Folder)> collections, IEnumerable<Feed>? previousFeeds = null)
    {
        var pairs = collections.ToList();
        var knownFeeds = account.FlattenedFeeds().Concat(previousFeeds ?? []).Distinct().ToList();
        var byUrl = knownFeeds.GroupBy(feed => feed.Url, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var repairs = pairs.SelectMany(pair => pair.Feeds).DistinctBy(item => item.Id)
            .Select(item => (Feed: byUrl.GetValueOrDefault(FeedlyResourceIds.UrlFromFeedId(item.Id)), ID: item.Id))
            .Where(pair => pair.Feed is not null && pair.Feed.FeedID != pair.ID)
            .Select(pair => (Feed: pair.Feed!, pair.ID)).ToList();
        await account.RepairFeedIDsAsync(repairs);
        foreach (var (feed, id) in repairs) byUrl[feed.Url] = account.ExistingFeedWithUrl(feed.Url) ?? feed.WithFeedID(id);
        var feedsBefore = pairs.SelectMany(pair => pair.Folder.Feeds).ToHashSet();
        var feedsAfter = new HashSet<Feed>();
        account.PerformBatchUpdate(() =>
        {
            foreach (var (items, folder) in pairs)
            {
                var desired = new List<Feed>();
                foreach (var item in items)
                {
                    var parser = new FeedlyFeedParser(item);
                    if (!byUrl.TryGetValue(parser.Url, out var feed))
                        byUrl[parser.Url] = feed = account.CreateFeedModel(parser.Url, parser.Title, parser.FeedID);
                    if (!string.IsNullOrEmpty(parser.Title)) feed.Name = parser.Title;
                    feed.ExternalID = item.Id;
                    if (parser.HomePageUrl is not null) feed.HomePageUrl = parser.HomePageUrl;
                    if (!desired.Contains(feed)) desired.Add(feed);
                    feedsAfter.Add(feed);
                }
                folder.Feeds.Clear();
                folder.Feeds.AddRange(desired);
            }
            account.TopLevelFeeds.RemoveAll(feed => feedsBefore.Contains(feed) && !feedsAfter.Contains(feed));
        });
    }
}
