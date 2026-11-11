using System.Net;
using System.Text;
using WinNewsWire.Articles;
using WinNewsWire.Feedbin;
using WinNewsWire.Parsers;
using WinNewsWire.SyncDatabase;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedbin;

public class FeedbinImportRoundTripTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task ImportRefreshMapsFeedIdsAndPreservesMultipleFolderMemberships()
    {
        using var handler = new ServiceHandler();
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.ImportOpmlAsync(new ParserData("test.opml", Encoding.UTF8.GetBytes(
            OpmlRoundTripTests.Document("""<outline text="Example" xmlUrl="https://example.com/feed"/>"""))));
        Assert.True(handler.Imported);
        Assert.Empty(account.TopLevelFeeds);
        var feed = Assert.Single(account.FlattenedFeeds());
        Assert.Equal("77", feed.FeedID);
        Assert.Equal("101", feed.ExternalID);
        Assert.Equal(2, account.Folders.Count);
        Assert.All(account.Folders, f => Assert.Same(feed, Assert.Single(f.Feeds)));
        var article = Assert.Single(await account.Database.FetchArticlesAsync(feed.FeedID));
        Assert.Equal("500", article.ArticleID);
        Assert.False(article.Status.Read);
        Assert.True(article.Status.Starred);
    }

    [Fact]
    public async Task ExistingUrlBasedFeedIdsRemainCompatible()
    {
        using var http = new HttpClient(new ServiceHandler());
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var existing = account.AddFeed("https://example.com/feed", "Legacy");
        await account.RefreshAllAsync();
        Assert.Same(existing, Assert.Single(account.FlattenedFeeds()));
        Assert.Equal("https://example.com/feed", existing.FeedID);
        Assert.Single(await account.Database.FetchArticlesAsync(existing.FeedID));
    }

    [Fact]
    public async Task RefreshDoesNotRevertAnUnsentReadChange()
    {
        using var handler = new ServiceHandler();
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        await account.MarkAsync(["500"], ArticleStatus.Key.Read, true);
        handler.FailStatusSend = true;
        await account.RefreshAllAsync();
        Assert.True(Assert.Single(await account.Database.FetchArticlesAsync("77")).Status.Read);
        Assert.Contains("500", await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Read));
    }

    [Fact]
    public async Task RemoteUnsubscribeRemovesSubscriptionButKeepsCachedArticles()
    {
        using var handler = new ServiceHandler();
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        handler.Subscribed = false;
        await account.RefreshAllAsync();
        Assert.Empty(account.FlattenedFeeds());
        Assert.Single(await account.Database.FetchArticlesAsync("77"));
    }

    private sealed class ServiceHandler : HttpMessageHandler
    {
        public bool Imported { get; private set; }
        public bool FailStatusSend { get; set; }
        public bool Subscribed { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailStatusSend && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Delete)
                && request.RequestUri!.AbsolutePath == "/v2/unread_entries.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/v2/imports.json" => """{"id":1,"complete":true}""",
                "/v2/subscriptions.json" => Subscribed
                    ? """[{"id":101,"feed_id":77,"title":"Example","feed_url":"https://example.com/feed","site_url":"https://example.com/"}]""" : "[]",
                "/v2/tags.json" => """[{"id":1,"name":"One"},{"id":2,"name":"Two"}]""",
                "/v2/taggings.json" => """[{"id":11,"feed_id":77,"name":"One"},{"id":12,"feed_id":77,"name":"Two"}]""",
                "/v2/unread_entries.json" => "[500]",
                "/v2/starred_entries.json" => "[500]",
                "/v2/entries.json" => """[{"id":500,"feed_id":77,"title":"Article","content":"<p>Body</p>","published":"2026-10-15T12:00:00Z"}]""",
                _ => "[]",
            };
            if (request.RequestUri.AbsolutePath == "/v2/imports.json") Imported = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    public void Dispose() => _manager.Dispose();
}
