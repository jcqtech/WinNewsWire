using Xunit;
using WinNewsWire.Articles;
using WinNewsWire.ArticlesDatabase;
using WinNewsWire.SyncDatabase;
using WinNewsWire.FeedFinder;
using WinNewsWire.Tree;

namespace WinNewsWire.Tests;

public class ArticlesDbSmoke
{
    [Fact]
    public async Task RoundTripInsertFetchMark()
    {
        var path = Path.Combine(Path.GetTempPath(), $"awdb-{Guid.NewGuid():N}.sqlite");
        using var store = new ArticlesDatabaseStore(path, "acct");
        var status = new ArticleStatus("x", read: false, DateTime.UtcNow);
        var a = new Article("acct", "x", "feed1", "uniq1", "Title", "<p>Hello</p>", null, null,
            "http://x/1", null, "sum", null, DateTime.UtcNow, null, null, null, status);
        await store.UpdateArticlesAsync(new[] { a });
        Assert.Equal(1, await store.UnreadCountAsync(new[] { "feed1" }));
        await store.MarkAsync(new[] { "x" }, ArticleStatus.Key.Read, true);
        Assert.Equal(0, await store.UnreadCountAsync(new[] { "feed1" }));
        // Multi-feed FetchArticlesAsync returns read articles too (UX parity with single-feed overload).
        var all = await store.FetchArticlesAsync(new[] { "feed1" });
        Assert.Single(all);
        var unread = await store.FetchUnreadArticlesAsync(new[] { "feed1" });
        Assert.Empty(unread);
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(path); } catch { }
    }
}

public class SyncDbSmoke
{
    [Fact]
    public async Task InsertAndSelectForProcessing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"syncdb-{Guid.NewGuid():N}.sqlite");
        using var s = new SyncDatabaseStore(path);
        await s.InsertStatusesAsync(new[]
        {
            new SyncStatus("a1", SyncStatus.SyncKey.Read, true),
            new SyncStatus("a2", SyncStatus.SyncKey.Starred, true),
        });
        Assert.Equal(2, await s.SelectPendingCountAsync());
        var sel = await s.SelectForProcessingAsync();
        Assert.Equal(2, sel.Count);
        var again = await s.SelectForProcessingAsync();
        Assert.Empty(again);
        s.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(path); } catch { }
    }
}

public class FeedSpecifierTests
{
    [Fact]
    public void BestFeedPrefersUserEntered()
    {
        var a = new FeedSpecifier(null, "https://e.com/feed", FeedSpecifierSource.UserEntered, 1);
        var b = new FeedSpecifier(null, "https://e.com/rss", FeedSpecifierSource.HtmlLink, 1);
        Assert.Equal(a, FeedSpecifier.BestFeed(new[] { a, b }));
    }
}

public class TreeSmoke
{
    private sealed class D : ITreeControllerDelegate
    {
        public IReadOnlyList<Node>? ChildNodesFor(Node node)
        {
            if (node.IsRoot)
                return new[] { new Node("A"), new Node("B") };
            return null;
        }
    }

    [Fact]
    public void RebuildPopulatesChildren()
    {
        var t = new TreeController(new D());
        Assert.Equal(2, t.RootNode.ChildNodes.Count);
        Assert.NotNull(t.NodeInTreeRepresenting("A"));
    }
}
