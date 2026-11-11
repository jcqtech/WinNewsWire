using WinNewsWire.Articles;
using WinNewsWire.Feedly;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

public sealed class FeedlyFolderReconciliationTests : IDisposable
{
    private readonly TestAccountManager _manager = new();
    private readonly Account _account;

    public FeedlyFolderReconciliationTests()
        => _account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate());

    [Fact]
    public void MirrorCollectionsAsFoldersAddsFolders()
    {
        Mirror([Collection("One", "collections/1"), Collection("Two", "collections/2")]);
        Assert.Equal(["One", "Two"], _account.Folders.Select(f => f.Name).Order());
        Assert.Equal(["collections/1", "collections/2"], _account.Folders.Select(f => f.ExternalID).Order());
    }

    [Fact]
    public void MirrorCollectionsAsFoldersRemovesFoldersWithoutCollections()
    {
        Mirror([Collection("One", "collections/1"), Collection("Two", "collections/2")]);
        Assert.Empty(Mirror([]));
        Assert.Empty(_account.Folders);
    }

    [Fact]
    public void MirrorCollectionsAsFoldersMatchesByExternalIDAcrossRename()
    {
        var folder = Assert.Single(Mirror([Collection("News", "collections/1")])).Folder;
        folder.Name = "Nachrichten";
        var after = Assert.Single(Mirror([Collection("News", "collections/1")])).Folder;
        Assert.Single(_account.Folders);
        Assert.Same(folder, after);
        Assert.Equal("News", folder.Name);
    }

    [Fact]
    public void MirrorCollectionsAsFoldersKeepsSameLabelCollectionsDistinct()
    {
        var collections = new[] { Collection("News", "collections/1"), Collection("News", "collections/2") };
        var before = Mirror(collections);
        Assert.Equal(2, before.Count);
        Assert.Equal(2, _account.Folders.Count);
        Assert.Equal(["collections/1", "collections/2"], _account.Folders.Select(f => f.ExternalID).Order());
        var after = Mirror(collections);
        Assert.Equal(2, _account.Folders.Count);
        Assert.True(before.Select(pair => pair.Folder).ToHashSet().SetEquals(after.Select(pair => pair.Folder)));
    }

    [Fact]
    public void MirrorCollectionsAsFoldersReturnsCollectionFeedsPairedWithFolders()
    {
        var collections = new[] { Collection("One", "collections/1", 1, 2), Collection("Two", "collections/2", 1, 3) };
        var pairs = Mirror(collections);
        Assert.Equal(2, pairs.Count);
        foreach (var pair in pairs)
            Assert.Equal(collections.Single(c => c.Id == pair.Folder.ExternalID).Feeds.Select(f => f.Id).Order(),
                pair.Feeds.Select(f => f.Id).Order());
    }

    [Fact]
    public async Task SyncFeedsForCollectionFoldersAddsFeeds()
    {
        var one = Folder("FolderOne", "folder/1");
        var two = Folder("FolderTwo", "folder/2");
        Assert.Empty(_account.FlattenedFeeds());
        await Sync([(Feeds(1, 2), one), (Feeds(1, 3), two)]);
        Assert.Equal(["feed/1", "feed/2", "feed/3"], _account.FlattenedFeeds().Select(f => f.FeedID).Order());
        Assert.Equal(["Feed One", "Feed Three", "Feed Two"], _account.FlattenedFeeds().Select(f => f.NameForDisplay).Order());
        Contains(one, "feed/1", "feed/2");
        Contains(two, "feed/1", "feed/3");
    }

    [Fact]
    public async Task SyncFeedsForCollectionFoldersRemovesFeedsNoLongerInCollection()
    {
        var one = Folder("FolderOne", "folder/1");
        var two = Folder("FolderTwo", "folder/2");
        await Sync([(Feeds(1, 2), one), (Feeds(1, 3), two)]);
        Assert.Equal(["feed/1", "feed/2", "feed/3"], _account.FlattenedFeeds().Select(f => f.FeedID).Order());
        await Sync([(Feeds(2), one), (Feeds(3), two)]);
        Contains(one, "feed/2");
        Contains(two, "feed/3");
    }

    [Fact]
    public async Task SyncFeedsForCollectionFoldersRenamesFeedWhenCollectionTitleChanges()
    {
        var folder = Folder("Folder", "folder/1");
        await Sync([([new("feed/1", "Original Title", null, null)], folder)]);
        var original = Assert.Single(folder.Feeds);
        Assert.Equal("Original Title", original.NameForDisplay);
        await Sync([([new("feed/1", "Updated Title", null, null)], folder)]);
        Assert.Equal("Updated Title", Assert.Single(folder.Feeds).NameForDisplay);
        Assert.Same(original, Assert.Single(folder.Feeds));
    }

    [Fact]
    public async Task SyncFeedsForCollectionFoldersLeavesUntitledFeedNameAlone()
    {
        var pairs = Mirror([new("collections/1", "One", [new("feed/1", null, null, null)])]);
        await Sync(pairs);
        await Sync(pairs);
        Assert.Null(Assert.Single(_account.FlattenedFeeds()).Name);
    }

    [Fact]
    public async Task SyncFeedsForCollectionFoldersKeepsRightToLeftFeedNameSanitized()
    {
        const string title = "\u05d7\u05d3\u05e9\u05d5\u05ea";
        var pairs = Mirror([new("collections/1", "One", [new("feed/1", $"<div style=\"direction:rtl;text-align:right\">{title}</div>", null, null)])]);
        await Sync(pairs);
        await Sync(pairs);
        Assert.Equal(title, Assert.Single(_account.FlattenedFeeds()).Name);
    }

    [Fact]
    public async Task SyncFeedsForCollectionFoldersRepairsStaleFeedID()
    {
        const string url = "https://example.com/feed.xml";
        var folder = Folder("Folder", "folder/1");
        var stale = _account.AddFeed(url, "Example", folder);
        stale.NewArticleNotificationsEnabled = true;
        await Sync([([new("feed/" + url, "Example", null, null)], folder)]);
        var repaired = Assert.Single(_account.FlattenedFeeds());
        Assert.Equal("feed/" + url, repaired.FeedID);
        Assert.Equal(url, repaired.Url);
        Contains(folder, "feed/" + url);
        Assert.True(repaired.NewArticleNotificationsEnabled);
    }

    [Fact]
    public async Task SyncFeedsForCollectionFoldersAfterRepairReusesFeedInstance()
    {
        const string url = "https://example.com/feed.xml";
        var folder = Folder("Folder", "folder/1");
        _account.AddFeed(url, "Example", folder);
        var feeds = new[] { new FeedlyFeed("feed/" + url, "Example", null, null) };
        await Sync([(feeds, folder)]);
        var repaired = Assert.Single(folder.Feeds);
        Assert.Equal("feed/" + url, repaired.FeedID);
        await Sync([(feeds, folder)]);
        Assert.Same(repaired, Assert.Single(folder.Feeds));
        Assert.Single(_account.FlattenedFeeds());
    }

    [Fact]
    public async Task MirrorThenRemoveAllRemovesFeedsToo()
    {
        await Sync(Mirror([Collection("One", "collections/1", 1, 2)]));
        Assert.NotEmpty(_account.FlattenedFeeds());
        Mirror([]);
        Assert.Empty(_account.FlattenedFeeds());
    }

    [Fact]
    public async Task IDRepairPreservesExistingArticlesStatusesAndSearch()
    {
        const string url = "https://example.com/feed.xml";
        var folder = Folder("Folder", "folder/1");
        _account.AddFeed(url, "Example", folder);
        var article = new Article(_account.AccountID, "article", url, "article", "searchable", null, null, null,
            null, null, null, null, DateTime.UtcNow, null, null, null, new ArticleStatus("article", true, true, DateTime.UtcNow));
        await _account.Database.UpdateArticlesAsync([article]);
        await Sync([([new("feed/" + url, "Example", null, null)], folder)]);
        Assert.Empty(await _account.Database.FetchArticlesAsync(url));
        var restored = Assert.Single(await _account.Database.FetchArticlesAsync("feed/" + url));
        Assert.Equal("article", restored.ArticleID);
        Assert.True(restored.Status.Read);
        Assert.True(restored.Status.Starred);
        Assert.Single(await _account.Database.SearchAsync("searchable", ["feed/" + url]));
    }

    [Fact]
    public async Task ReopeningReplaysAnInterruptedIDRepair()
    {
        const string url = "https://example.com/feed.xml";
        var folder = Folder("Folder", "folder/1");
        _account.AddFeed(url, "Example", folder);
        var article = new Article(_account.AccountID, "article", url, "article", "Title", null, null, null,
            null, null, null, null, null, null, null, null, new ArticleStatus("article", false, false, DateTime.UtcNow));
        await _account.Database.UpdateArticlesAsync([article]);
        await Sync([([new("feed/" + url, "Example", null, null)], folder)]);
        // Reproduce a crash after the settings snapshot but before the article transaction.
        await _account.Database.RemapFeedIDsAsync(new Dictionary<string, string> { ["feed/" + url] = url });
        var reopened = _manager.Reopen(_account, new FeedlyAccountDelegate());
        Assert.Equal("feed/" + url, Assert.Single(reopened.FlattenedFeeds()).FeedID);
        Assert.Single(await reopened.Database.FetchArticlesAsync("feed/" + url));
        Assert.Empty(await reopened.Database.FetchArticlesAsync(url));
    }

    private IReadOnlyList<(IReadOnlyList<FeedlyFeed> Feeds, Folder Folder)> Mirror(IReadOnlyList<FeedlyCollection> collections)
        => FeedlyFolderReconciliation.MirrorCollectionsAsFolders(_account, collections);

    private Task Sync(IEnumerable<(IReadOnlyList<FeedlyFeed> Feeds, Folder Folder)> pairs)
        => FeedlyFolderReconciliation.SyncFeedsForCollectionFoldersAsync(_account, pairs);

    private Folder Folder(string name, string id)
    {
        var folder = _account.AddFolder(name);
        folder.ExternalID = id;
        return folder;
    }

    private static FeedlyCollection Collection(string name, string id, params int[] feeds) => new(id, name, Feeds(feeds));
    private static List<FeedlyFeed> Feeds(params int[] ids) => ids.Select(id => new FeedlyFeed("feed/" + id,
        "Feed " + (id switch { 1 => "One", 2 => "Two", _ => "Three" }), null, null)).ToList();
    private static void Contains(Folder folder, params string[] ids) => Assert.Equal(ids.Order(), folder.Feeds.Select(f => f.FeedID).Order());
    public void Dispose() => _manager.Dispose();
}
