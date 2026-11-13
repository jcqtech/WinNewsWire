using System.Diagnostics;
using System.Text.Json;
using WinNewsWire.Account;
using WinNewsWire.Core;
using WinNewsWire.Parsers;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.NewsBlur;

public sealed partial class NewsBlurAccountDelegate
{
    public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.RefreshAll, () => RefreshAllCoreAsync(account, progress, ct));

    private async Task RefreshAllCoreAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        await account.LogActivityAsync(ActivityKind.RefreshFeedList, async () =>
        {
            var response = await API.RetrieveFeedStructureAsync(ct);
            SyncStructure(account, response);
            return response;
        }, successMessage: response => $"{response.Feeds.Count} feeds, {response.Folders.Count} folders");
        await RemoteSyncHelpers.SendBeforeRefreshAsync(account, ct);
        await RefreshArticleStatusAsync(account, ct);
        var missing = await account.Database.FetchMissingArticleIDsAsync(DateTime.UtcNow.AddMonths(-3));
        foreach (var batch in missing.Chunk(100))
            await account.LogActivityAsync(ActivityKind.RefreshMissingArticles, async () =>
            {
                var stories = await API.RetrieveStoriesByHashesAsync(batch, ct);
                await SaveStoriesAsync(account, stories);
                return stories.Count;
            }, successMessage: count => $"{count} articles");
        foreach (var id in _initialFeeds.ToList())
        {
            var feed = account.FlattenedFeeds().FirstOrDefault(item => item.ExternalID == id);
            if (feed is not null) await DownloadFeedAsync(account, feed, ct);
            _initialFeeds.Remove(id);
        }
        await account.RecalculateUnreadCountsAsync();
        progress?.Report(new ProgressInfo(0, 1, 1));
        account.Settings.LastRefreshCompletedDate = DateTime.UtcNow;
    }

    internal void SyncStructure(Account.Account account, NewsBlurFeedsResponse response)
    {
        account.PerformBatchUpdate(() =>
        {
            var names = response.Folders.Select(folder => folder.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var folder in account.Folders.Where(folder => !names.Contains(folder.NameForDisplay)).ToList())
            {
                foreach (var feed in folder.Feeds) account.AddFeedToContainer(feed, null);
                account.RemoveFolder(folder);
            }
            foreach (var folder in response.Folders.Where(folder => folder.Name != " "))
                account.AddFolder(folder.Name);
            SyncFeeds(account, response.Feeds);
            var byID = account.FlattenedFeeds().GroupBy(feed => feed.ExternalID ?? feed.FeedID, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var feed in byID.Values) feed.FolderRelationship = new();
            account.TopLevelFeeds.Clear();
            foreach (var folder in account.Folders) folder.Feeds.Clear();
            foreach (var remote in response.Folders)
            {
                var folder = remote.Name == " " ? null : account.ExistingFolderWithName(remote.Name);
                foreach (var id in remote.FeedIDs)
                {
                    if (!byID.TryGetValue(id.ToString(), out var feed))
                    {
                        Trace.TraceWarning($"NewsBlur folder {remote.Name} references a missing feed {id}.");
                        continue;
                    }
                    account.AddFeedToContainer(feed, folder);
                    if (folder is not null) feed.FolderRelationship![folder.NameForDisplay] = folder.NameForDisplay;
                }
            }
        });
    }

    public Task RefreshArticleStatusAsync(Account.Account account, CancellationToken ct = default)
        => RefreshArticleStatusReturningCountAsync(account, ct);

    private async Task<int> RefreshArticleStatusReturningCountAsync(Account.Account account, CancellationToken ct)
    {
        IReadOnlyList<NewsBlurStoryHash>? unread = null, starred = null;
        Exception? error = null;
        try { unread = await API.RetrieveUnreadStoryHashesAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            Trace.TraceError($"NewsBlur unread status refresh failed: {ex.Message}");
            error = ex;
        }
        try { starred = await API.RetrieveStarredStoryHashesAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            Trace.TraceError($"NewsBlur starred status refresh failed: {ex.Message}");
            error = ex;
        }
        var counts = await RemoteSyncHelpers.ReconcileStatusesAsync(account, unread?.Select(item => item.Hash).ToHashSet(),
            starred?.Select(item => item.Hash).ToHashSet());
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return counts.UnreadAdded + counts.UnreadRemoved + counts.StarredAdded + counts.StarredRemoved;
    }

    private async Task DownloadFeedAsync(Account.Account account, Feed feed, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cutoff = DateTime.UtcNow.AddMonths(-3);
        for (int page = 1; ; page++)
        {
            var stories = await API.RetrieveFeedStoriesAsync(SubscriptionID(feed), page, ct);
            if (stories.Count == 0) break;
            if (stories.All(story => seen.Contains(story.StoryID))) throw new InvalidDataException("NewsBlur repeated a feed page.");
            seen.UnionWith(stories.Select(story => story.StoryID));
            var recent = stories.Where(story => story.DatePublished is null || story.DatePublished >= cutoff).ToList();
            if (recent.Count == 0) break;
            await SaveStoriesAsync(account, recent);
        }
        feed.LastCheckDate = DateTime.UtcNow;
    }

    private static async Task SaveStoriesAsync(Account.Account account, IEnumerable<NewsBlurStory> stories)
    {
        var feeds = account.FlattenedFeeds().GroupBy(feed => feed.ExternalID ?? feed.FeedID, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var articles = stories.Select(story =>
        {
            var remoteID = story.FeedID.ToString();
            var parsed = new ParsedItem(story.StoryID, story.StoryID, remoteID, story.Url, null, story.Title, null,
                story.ContentHtml, null, null, null, story.ImageUrl, null, story.DatePublished, null,
                story.AuthorName is null ? null : new HashSet<ParsedAuthor> { new(story.AuthorName, null, null, null) },
                story.Tags?.ToHashSet(), null, thumbnailUrl: story.ThumbnailUrl);
            return RemoteSyncHelpers.ArticleFromParsedItem(account, parsed, feeds.GetValueOrDefault(remoteID)?.FeedID ?? remoteID, defaultRead: true);
        }).ToList();
        var added = await account.Database.UpdateArticlesAsync(articles);
        if (added.Count > 0) account.RaiseNewArticlesDownloaded(added);
    }
}
