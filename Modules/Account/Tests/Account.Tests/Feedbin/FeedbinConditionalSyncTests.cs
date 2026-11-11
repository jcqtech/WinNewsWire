using System.Net;
using System.Text;
using WinNewsWire.Articles;
using WinNewsWire.Feedbin;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedbin;

public sealed class FeedbinConditionalSyncTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task NotModifiedSnapshotsSurviveAReopenedAccountAndNewCaller()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        var id = account.AccountID;
        account.Dispose();
        fixture.Requests.Clear();
        var reopened = _manager.CreateAccount(id, new FeedbinAccountDelegate(http: http));
        await reopened.RefreshAllAsync();

        var folder = Assert.Single(reopened.Folders);
        Assert.Equal("Folder", folder.Name);
        Assert.Equal("1", Assert.Single(folder.Feeds).FeedID);
        Assert.Empty(reopened.TopLevelFeeds);
        var articles = await reopened.Database.FetchArticlesAsync("1");
        Assert.False(articles.Single(article => article.ArticleID == "101").Status.Read);
        Assert.True(articles.Single(article => article.ArticleID == "102").Status.Starred);
        foreach (var key in Fixture.Keys)
        {
            var request = Assert.Single(fixture.Requests, request => request.Path == key + ".json");
            Assert.Equal(fixture.Snapshots[key].ETag, request.ETag);
            Assert.Equal("Wed, 01 Jan 2025 00:00:00 GMT", request.LastModified);
        }
        Assert.Equal("W/\"subscriptions-1\"", reopened.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.Subscriptions)!.ETag);
    }

    [Fact]
    public async Task ReopeningLegacyUrlBasedIdsFetchesTheMissingServiceMappingBeforeDownloading()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var original = account.AddFeed("https://example.com/feed");
        await account.RefreshAllAsync();
        account.Dispose();
        fixture.Requests.Clear();
        var reopened = _manager.CreateAccount(account.AccountID, new FeedbinAccountDelegate(http: http));
        await reopened.RefreshAllAsync();

        Assert.Null(Assert.Single(fixture.Requests, request => request.Path == "subscriptions.json").ETag);
        Assert.Equal(original.FeedID, Assert.Single(Assert.Single(reopened.Folders).Feeds).FeedID);
        Assert.Equal(2, (await reopened.Database.FetchArticlesAsync(original.FeedID)).Count);
        Assert.Empty(await reopened.Database.FetchArticlesAsync("1"));
    }

    [Fact]
    public async Task ChangedSubscriptionsDoNotMoveFeedsOutOfUnchangedFolders()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        var folder = Assert.Single(account.Folders);
        var existing = Assert.Single(folder.Feeds);
        existing.EditedName = "My name";
        fixture.Snapshots["subscriptions"] = new("""
            [{"id":11,"feed_id":1,"title":"New title","feed_url":"https://example.com/feed"},
             {"id":12,"feed_id":2,"title":"New feed","feed_url":"https://example.com/second"}]
            """, "\"subscriptions-2\"");
        await account.RefreshAllAsync();

        Assert.Same(existing, Assert.Single(folder.Feeds));
        Assert.Equal("New title", existing.Name);
        Assert.Equal("My name", existing.EditedName);
        Assert.Equal("2", Assert.Single(account.TopLevelFeeds).FeedID);
    }

    [Fact]
    public async Task RenamedTagForcesTheTaggingRequestEvenWhenItsETagDidNotChange()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        fixture.Requests.Clear();
        fixture.Snapshots["tags"] = new("""[{"id":10,"name":"Renamed"}]""", "\"tags-2\"");
        fixture.Snapshots["taggings"] = fixture.Snapshots["taggings"] with
        {
            Body = """[{"id":21,"feed_id":1,"name":"Renamed"}]"""
        };
        await account.RefreshAllAsync();

        Assert.Null(Assert.Single(fixture.Requests, request => request.Path == "taggings.json").ETag);
        var folder = Assert.Single(account.Folders);
        Assert.Equal("Renamed", folder.Name);
        Assert.Equal("21", Assert.Single(folder.Feeds).FolderRelationship!["Renamed"]);
        Assert.Empty(account.TopLevelFeeds);
    }

    [Fact]
    public async Task EmptyChangedTaggingsReturnFeedsToTheRoot()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        fixture.Snapshots["taggings"] = new("[]", "\"taggings-2\"");
        await account.RefreshAllAsync();
        Assert.Empty(Assert.Single(account.Folders).Feeds);
        Assert.Empty(Assert.Single(account.TopLevelFeeds).FolderRelationship!);
    }

    [Fact]
    public async Task FailedSnapshotFetchDoesNotCommitAnyNewValidators()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        fixture.Snapshots["subscriptions"] = new("""
            [{"id":11,"feed_id":1,"title":"New title","feed_url":"https://example.com/feed"}]
            """, "\"subscriptions-2\"");
        fixture.Snapshots["taggings"] = fixture.Snapshots["taggings"] with { Status = HttpStatusCode.ServiceUnavailable };
        await Assert.ThrowsAsync<HttpRequestException>(() => account.RefreshAllAsync());
        Assert.Equal("W/\"subscriptions-1\"", account.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.Subscriptions)!.ETag);
        Assert.Equal("Feed", Assert.Single(account.FlattenedFeeds()).Name);
        fixture.Snapshots["taggings"] = fixture.Snapshots["taggings"] with { Status = HttpStatusCode.OK };
        await account.RefreshAllAsync();
        Assert.Equal("New title", Assert.Single(account.FlattenedFeeds()).Name);
        Assert.Equal("\"subscriptions-2\"", account.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.Subscriptions)!.ETag);
    }

    [Fact]
    public async Task InvalidSnapshotDoesNotAdvanceValidatorsOrEraseSubscriptions()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        fixture.Snapshots["subscriptions"] = new("""[{"id":11,"feed_id":1,"title":"Invalid"}]""", "\"subscriptions-2\"");
        await Assert.ThrowsAsync<InvalidDataException>(() => account.RefreshAllAsync());
        Assert.Equal("W/\"subscriptions-1\"", account.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.Subscriptions)!.ETag);
        Assert.Equal("1", Assert.Single(account.FlattenedFeeds()).FeedID);
    }

    [Fact]
    public async Task FailedModelSaveKeepsThePreviousValidatorUntilTheRetrySucceeds()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        fixture.Snapshots["subscriptions"] = new("""
            [{"id":11,"feed_id":1,"title":"New title","feed_url":"https://example.com/feed"}]
            """, "\"subscriptions-2\"");
        using (File.Open(Path.Combine(account.AccountDirectory, "Settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => account.RefreshAllAsync());
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("W/\"subscriptions-1\"", account.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.Subscriptions)!.ETag);
        var reopened = _manager.Reopen(account, new FeedbinAccountDelegate(http: http));
        await reopened.RefreshAllAsync();
        Assert.Equal("New title", Assert.Single(reopened.FlattenedFeeds()).Name);
        Assert.Equal("\"subscriptions-2\"", reopened.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.Subscriptions)!.ETag);
    }

    [Fact]
    public async Task UnreadRequestFailureDoesNotPreventApplyingStarredChanges()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        fixture.Snapshots["unread_entries"] = fixture.Snapshots["unread_entries"] with { Status = HttpStatusCode.ServiceUnavailable };
        fixture.Snapshots["starred_entries"] = new("[101,102]", "\"starred_entries-2\"");
        await Assert.ThrowsAsync<HttpRequestException>(() => account.Delegate.RefreshArticleStatusAsync(account));

        var article = (await account.Database.FetchArticlesAsync("1")).Single(article => article.ArticleID == "101");
        Assert.False(article.Status.Read);
        Assert.True(article.Status.Starred);
        Assert.Equal("\"unread_entries-1\"", account.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.UnreadEntries)!.ETag);
        Assert.Equal("\"starred_entries-2\"", account.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.StarredEntries)!.ETag);
    }

    [Fact]
    public async Task FailedStatusWriteDoesNotAdvanceItsValidator()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        account.Database.Queue.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_read BEFORE UPDATE OF read ON statuses BEGIN SELECT RAISE(ABORT, 'test write failure'); END;";
            command.ExecuteNonQuery();
        });
        fixture.Snapshots["unread_entries"] = new("[]", "\"unread_entries-2\"");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => account.Delegate.RefreshArticleStatusAsync(account));
        Assert.Equal("\"unread_entries-1\"", account.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.UnreadEntries)!.ETag);
        account.Database.Queue.Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER reject_read;";
            command.ExecuteNonQuery();
        });
        await account.Delegate.RefreshArticleStatusAsync(account);
        Assert.Equal("\"unread_entries-2\"", account.Settings.ConditionalGetInfo(FeedbinAPICaller.ConditionalGetKeys.UnreadEntries)!.ETag);
        Assert.All(await account.Database.FetchArticlesAsync("1"), article => Assert.True(article.Status.Read));
    }

    [Fact]
    public async Task NotModifiedStatusesPreservePendingLocalEdits()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        await account.MarkAsync(["101"], ArticleStatus.Key.Read, true);
        await account.Delegate.RefreshArticleStatusAsync(account);
        Assert.True((await account.Database.FetchArticlesAsync("1")).Single(article => article.ArticleID == "101").Status.Read);
        Assert.Equal(1, await account.SyncDatabase.SelectPendingCountAsync());
    }

    [Fact]
    public async Task StatusPollingReportsOnlyRealSendsAndChanges()
    {
        using var fixture = new Fixture();
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        Assert.False(await account.SyncArticleStatusAsync());
        await account.MarkAsync(["101"], ArticleStatus.Key.Starred, true);
        Assert.True(await account.SyncArticleStatusAsync());
        Assert.False(await account.SyncArticleStatusAsync());
    }

    private sealed record Snapshot(string Body, string ETag, HttpStatusCode Status = HttpStatusCode.OK);
    private sealed record Request(string Path, string? ETag, string? LastModified);

    private sealed class Fixture : HttpMessageHandler
    {
        public static readonly string[] Keys = ["tags", "subscriptions", "taggings", "unread_entries", "starred_entries"];
        public Dictionary<string, Snapshot> Snapshots { get; } = new()
        {
            ["tags"] = new("""[{"id":10,"name":"Folder"}]""", "\"tags-1\""),
            ["subscriptions"] = new("""[{"id":11,"feed_id":1,"title":"Feed","feed_url":"https://example.com/feed"}]""", "W/\"subscriptions-1\""),
            ["taggings"] = new("""[{"id":21,"feed_id":1,"name":"Folder"}]""", "\"taggings-1\""),
            ["unread_entries"] = new("[101]", "\"unread_entries-1\""),
            ["starred_entries"] = new("[102]", "\"starred_entries-1\"")
        };
        public List<Request> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Path.GetFileName(request.RequestUri!.AbsolutePath);
            var etag = request.Headers.TryGetValues("If-None-Match", out var tags) ? tags.Single() : null;
            var modified = request.Headers.TryGetValues("If-Modified-Since", out var dates) ? dates.Single() : null;
            Requests.Add(new(path, etag, modified));
            if (request.Method != HttpMethod.Get) return Task.FromResult(Json("{}"));
            if (!Snapshots.TryGetValue(Path.GetFileNameWithoutExtension(path), out var snapshot))
            {
                Assert.Equal("entries.json", path);
                return Task.FromResult(Json("""
                    [{"id":101,"feed_id":1,"title":"Unread","content":"Body"},
                     {"id":102,"feed_id":1,"title":"Starred","content":"Body"}]
                    """));
            }
            if (snapshot.Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(snapshot.Status));
            if (etag == snapshot.ETag) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            var response = Json(snapshot.Body);
            response.Headers.TryAddWithoutValidation("ETag", snapshot.ETag);
            response.Content.Headers.LastModified = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
            return Task.FromResult(response);
        }
        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public void Dispose() => _manager.Dispose();
}
