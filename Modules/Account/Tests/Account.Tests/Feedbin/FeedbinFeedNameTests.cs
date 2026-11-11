using System.Net;
using System.Text;
using WinNewsWire.Feedbin;
using WinNewsWire.SyncDatabase;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedbin;

/// <summary>FeedbinFeedNameTests.swift and FeedbinStatusSendTests.swift, with the same upstream JSON fixtures.</summary>
public class FeedbinFeedNameTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task RefreshLeavesEditedNameAlone()
    {
        using var http = new HttpClient(new FixtureHandler());
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        var feed = account.FlattenedFeeds().Single(f => f.FeedID == "1296379");
        feed.EditedName = "Edited Name";
        await account.RefreshAllAsync();
        Assert.Equal("Edited Name", feed.EditedName);
        Assert.Equal("Daring Fireball", feed.Name);
        Assert.Equal("Edited Name", feed.NameForDisplay);
    }

    [Fact]
    public async Task FailingUnreadGroupStillSendsStarredGroup()
    {
        using var http = new HttpClient(new FixtureHandler(failUnread: true));
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.SyncDatabase.InsertStatusesAsync([
            new("1", SyncStatus.SyncKey.Read, false),
            new("2", SyncStatus.SyncKey.Starred, true),
        ]);
        await Assert.ThrowsAsync<ArticleStatusSyncException>(() => account.Delegate.SendArticleStatusAsync(account));
        Assert.Equal(new HashSet<string> { "1" }, await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Read));
        Assert.Empty(await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Starred));
    }

    [Fact]
    public async Task FailingStatusSendDoesNotBlockArticleRefresh()
    {
        using var http = new HttpClient(new FixtureHandler(failUnread: true, postOnly: true));
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.SyncDatabase.InsertStatusesAsync([new("1", SyncStatus.SyncKey.Read, false)]);
        await account.RefreshAllAsync();
        Assert.Equal(new HashSet<string> { "1" }, await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Read));
        Assert.Equal(224, account.FlattenedFeeds().Count());
    }

    private sealed class FixtureHandler(bool failUnread = false, bool postOnly = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failUnread && request.RequestUri!.AbsolutePath.Contains("unread_entries.json")
                && (!postOnly || request.Method == HttpMethod.Post))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
            var fixture = request.RequestUri!.AbsolutePath switch
            {
                "/v2/tags.json" => "tags_add.json",
                "/v2/subscriptions.json" => "subscriptions_initial.json",
                _ => null,
            };
            var json = fixture is null ? "[]" : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "JSON", fixture));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    public void Dispose() => _manager.Dispose();
}
