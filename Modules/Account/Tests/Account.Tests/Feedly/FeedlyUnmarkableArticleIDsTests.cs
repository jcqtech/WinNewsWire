using System.Net;
using System.Text.Json;
using WinNewsWire.Articles;
using WinNewsWire.Feedly;
using WinNewsWire.SyncDatabase;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

public sealed class FeedlyUnmarkableArticleIDsTests
{
    private readonly DateTime _cutoff = DateTime.UnixEpoch.AddSeconds(1_000_000);

    [Fact]
    public void RecentArticlesAreMarkable()
        => Assert.Empty(Unmarkable(["a"], new() { ["a"] = _cutoff.AddSeconds(60) }));

    [Fact]
    public void OldArticlesAreUnmarkable()
        => Assert.Equal(["a"], Unmarkable(["a"], new() { ["a"] = _cutoff.AddSeconds(-60) }));

    [Fact]
    public void MissingArticlesAreUnmarkable()
        => Assert.Equal(["gone"], Unmarkable(["gone"], new()));

    [Fact]
    public void MixedArticlesPartitionCorrectly()
        => Assert.True(Unmarkable(["new", "old", "gone"],
            new() { ["new"] = _cutoff.AddSeconds(60), ["old"] = _cutoff.AddSeconds(-60) }).SetEquals(["old", "gone"]));

    private HashSet<string> Unmarkable(IEnumerable<string> ids, Dictionary<string, DateTime> dates)
        => FeedlyAccountDelegate.UnmarkableAsReadArticleIDs(ids, dates, _cutoff);

    [Fact]
    public async Task SendingDropsOnlyUnmarkableReadChanges()
    {
        using var manager = new TestAccountManager();
        using var http = new HttpClient(new Handler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal("markAsRead", root.GetProperty("action").GetString());
            Assert.Equal("recent", Assert.Single(root.GetProperty("entryIds").EnumerateArray()).GetString());
            return new(HttpStatusCode.OK);
        }));
        var accountDelegate = new FeedlyAccountDelegate(http: http);
        var account = manager.CreateAccount(accountDelegate: accountDelegate);
        var feed = account.AddFeed("https://example.com/feed");
        var oldDate = DateTime.UtcNow.AddDays(-90);
        Article Article(string id, DateTime arrived) => new(account.AccountID, id, feed.FeedID, id, null, null, null, null,
            null, null, null, null, oldDate, null, null, null, new ArticleStatus(id, false, false, arrived));
        await account.Database.UpdateArticlesAsync([Article("old", oldDate), Article("recent", DateTime.UtcNow)]);
        await account.SyncDatabase.InsertStatusesAsync(new[] { "old", "recent", "gone" }
            .Select(id => new SyncStatus(id, SyncStatus.SyncKey.Read, true)));
        await accountDelegate.SendArticleStatusAsync(account);
        Assert.Empty(await account.SyncDatabase.SelectForProcessingAsync());
    }

    [Fact]
    public async Task LaterBatchFailureDoesNotRequeueAcceptedChanges()
    {
        using var manager = new TestAccountManager();
        var accepted = new HashSet<string>();
        int calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var ids = document.RootElement.GetProperty("entryIds").EnumerateArray().Select(id => id.GetString()!).ToList();
            if (++calls > 1) return new(HttpStatusCode.ServiceUnavailable);
            Assert.Equal(300, ids.Count);
            accepted.UnionWith(ids);
            return new(HttpStatusCode.OK);
        }));
        var accountDelegate = new FeedlyAccountDelegate(http: http);
        var account = manager.CreateAccount(accountDelegate: accountDelegate);
        await account.SyncDatabase.InsertStatusesAsync(Enumerable.Range(1, 350)
            .Select(id => new SyncStatus(id.ToString(), SyncStatus.SyncKey.Starred, true)));
        await Assert.ThrowsAsync<ArticleStatusSyncException>(() => accountDelegate.SendArticleStatusAsync(account));
        var remaining = await account.SyncDatabase.SelectForProcessingAsync();
        Assert.Equal(50, remaining.Count);
        Assert.DoesNotContain(remaining, status => accepted.Contains(status.ArticleID));
        Assert.Equal(2, calls);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
