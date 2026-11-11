using System.Net;
using System.Text;
using System.Text.Json;
using WinNewsWire.Feedbin;
using WinNewsWire.Feedly;
using WinNewsWire.NewsBlur;
using WinNewsWire.ReaderAPI;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class AccountMutationTests : IDisposable
{
    private const string FeedUrl = "https://example.com/feed";
    private const string FeedlyFeeds = """[{"id":"feed/https://example.com/feed","title":"Example"}]""";
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task LocalMoveAndDeleteAffectOnlyTheSelectedMembership()
    {
        var account = _manager.CreateAccount();
        var first = await account.CreateFolderAsync("First");
        var second = await account.CreateFolderAsync("Second");
        var third = await account.CreateFolderAsync("Third");
        var feed = account.AddFeed(FeedUrl, folder: first);
        await account.AddFeedAsync(feed, second);
        await account.AddFeedAsync(feed, null);
        await account.MoveFeedAsync(feed, first, third);
        Assert.Empty(first.Feeds);
        Assert.Contains(feed, second.Feeds);
        Assert.Contains(feed, third.Feeds);
        Assert.Contains(feed, account.TopLevelFeeds);
        await account.RemoveFeedAsync(feed, second);
        Assert.Empty(second.Feeds);
        Assert.Contains(feed, third.Feeds);
        Assert.Contains(feed, account.TopLevelFeeds);
    }

    [Fact]
    public async Task ForeignAndStaleContainersAreRejected()
    {
        var account = _manager.CreateAccount();
        var other = _manager.CreateAccount();
        var feed = account.AddFeed(FeedUrl);
        var foreign = await other.CreateFolderAsync("Foreign");
        await Assert.ThrowsAsync<ArgumentException>(() => account.MoveFeedAsync(feed, null, foreign));
        var stale = await account.CreateFolderAsync("Stale");
        await account.RemoveFolderAsync(stale);
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.MoveFeedAsync(feed, null, stale));
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Empty(foreign.Feeds);
    }

    [Fact]
    public async Task RemovedFolderIDsAreNotReusedBeforeUndo()
    {
        var account = _manager.CreateAccount();
        var removed = await account.CreateFolderAsync("Removed");
        await account.RemoveFolderAsync(removed);
        var added = await account.CreateFolderAsync("Added");
        await account.RestoreFolderAsync(removed);
        Assert.NotEqual(removed.FolderID, added.FolderID);
        Assert.Equal(2, account.Folders.Count);
    }

    [Fact]
    public async Task ReaderAPICannotCopyAFeedIntoMultipleFolders()
    {
        var account = _manager.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS));
        var first = account.AddFolder("First");
        var second = account.AddFolder("Second");
        var feed = account.AddFeed(FeedUrl, folder: first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.AddFeedAsync(feed, second));
        await Assert.ThrowsAsync<InvalidOperationException>(() => account.MoveFeedAsync(feed, first, null));
        Assert.Contains(feed, first.Feeds);
    }

    [Fact]
    public async Task FeedbinRenamesSubscriptionNotFeedID()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v2/subscriptions/100/update.json", request.Uri.AbsolutePath);
            Assert.Equal("New name", JsonDocument.Parse(request.Body).RootElement.GetProperty("title").GetString());
            return Json("{}");
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var feed = account.AddFeed(FeedUrl, "Old name", feedID: "22");
        feed.ExternalID = "100";
        await account.RenameFeedAsync(feed, "New name");
        Assert.Equal("New name", feed.NameForDisplay);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task FeedbinTaggingUsesReturnedIDAndRemovesRootMembership()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/v2/taggings.json", request.Uri.AbsolutePath);
            using var json = JsonDocument.Parse(request.Body);
            Assert.Equal(22, json.RootElement.GetProperty("feed_id").GetInt64());
            Assert.Equal("Folder", json.RootElement.GetProperty("name").GetString());
            var response = Json("{}", HttpStatusCode.Created);
            response.Headers.Location = new Uri("https://api.feedbin.com/v2/taggings/77.json");
            return response;
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var feed = account.AddFeed(FeedUrl, feedID: "22");
        feed.ExternalID = "100";
        var folder = await account.CreateFolderAsync("Folder");
        await account.AddFeedAsync(feed, folder);
        Assert.Empty(account.TopLevelFeeds);
        Assert.Same(feed, Assert.Single(folder.Feeds));
        Assert.Equal("77", feed.FolderRelationship!["Folder"]);
    }

    [Fact]
    public async Task FeedbinRemovingSharedFeedDeletesOnlyItsTagging()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/v2/taggings/77.json", request.Uri.AbsolutePath);
            return Json("{}");
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var first = account.AddFolder("First");
        var second = account.AddFolder("Second");
        var feed = account.AddFeed(FeedUrl, folder: first, feedID: "22");
        account.AddFeed(FeedUrl, folder: second);
        feed.ExternalID = "100";
        feed.FolderRelationship = new() { ["First"] = "77", ["Second"] = "88" };
        await account.RemoveFeedAsync(feed, first);
        Assert.Empty(first.Feeds);
        Assert.Same(feed, Assert.Single(second.Feeds));
        Assert.Equal("88", Assert.Single(feed.FolderRelationship).Value);
    }

    [Fact]
    public async Task FeedbinFailedMoveRollsBackOnlyTheNewTagging()
    {
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = Json("{}", HttpStatusCode.Created);
                response.Headers.Location = new Uri("https://api.feedbin.com/v2/taggings/99.json");
                return response;
            }
            return Json("{}", request.Uri.AbsolutePath.EndsWith("/77.json")
                ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        var first = account.AddFolder("First");
        var second = account.AddFolder("Second");
        var feed = account.AddFeed(FeedUrl, folder: first, feedID: "22");
        feed.ExternalID = "100";
        feed.FolderRelationship = new() { ["First"] = "77" };
        await Assert.ThrowsAsync<HttpRequestException>(() => account.MoveFeedAsync(feed, first, second));
        Assert.Equal("/v2/taggings/99.json", handler.Requests.Last().Uri.AbsolutePath);
        Assert.Same(feed, Assert.Single(first.Feeds));
        Assert.Empty(second.Feeds);
        Assert.Equal("77", Assert.Single(feed.FolderRelationship).Value);
    }

    [Fact]
    public async Task FeedlyCreatesAndRenamesCollectionsUsingArrayResponses()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v3/collections", request.Uri.AbsolutePath);
            using var json = JsonDocument.Parse(request.Body);
            var label = json.RootElement.GetProperty("label").GetString();
            if (label == "Renamed") Assert.Equal("user/1/category/new", json.RootElement.GetProperty("id").GetString());
            return Json($$"""[{"id":"user/1/category/new","label":"{{label}}","feeds":[]}]""");
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate(http: http));
        var folder = await account.CreateFolderAsync("Folder");
        Assert.Equal("user/1/category/new", folder.ExternalID);
        await account.RenameFolderAsync(folder, "Renamed");
        Assert.Equal("Renamed", folder.Name);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FeedlyFailedMoveRollsBackDestinationAndPreservesLocalMembership()
    {
        using var handler = new Handler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                Assert.EndsWith("/destination/feeds", Uri.UnescapeDataString(request.Uri.AbsolutePath));
                return Json(FeedlyFeeds);
            }
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.EndsWith("/feeds/.mdelete", request.Uri.AbsolutePath);
            Assert.Equal("feed/" + FeedUrl, JsonDocument.Parse(request.Body).RootElement[0].GetProperty("id").GetString());
            return Json("{}", Uri.UnescapeDataString(request.Uri.AbsolutePath).Contains("/source/")
                ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate(http: http));
        var source = account.AddFolder("Source"); source.ExternalID = "user/1/category/source";
        var destination = account.AddFolder("Destination"); destination.ExternalID = "user/1/category/destination";
        var feed = account.AddFeed(FeedUrl, folder: source, feedID: "feed/" + FeedUrl);
        feed.ExternalID = feed.FeedID;
        await Assert.ThrowsAsync<HttpRequestException>(() => account.MoveFeedAsync(feed, source, destination));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Same(feed, Assert.Single(source.Feeds));
        Assert.Empty(destination.Feeds);
    }

    [Fact]
    public async Task FailedMoveNeverRollsBackAPreexistingDestinationMembership()
    {
        using var handler = new Handler(_ => Json("{}", HttpStatusCode.InternalServerError));
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate(http: http));
        var source = account.AddFolder("Source"); source.ExternalID = "source";
        var destination = account.AddFolder("Destination"); destination.ExternalID = "destination";
        var feed = account.AddFeed(FeedUrl, folder: source);
        account.AddFeed(FeedUrl, folder: destination);
        await Assert.ThrowsAsync<HttpRequestException>(() => account.MoveFeedAsync(feed, source, destination));
        Assert.Single(handler.Requests);
        Assert.Contains(feed, source.Feeds);
        Assert.Contains(feed, destination.Feeds);
    }

    [Fact]
    public async Task FeedlyRestoreReplacesDeletedCollectionIDAndPreservesFeedIdentity()
    {
        using var handler = new Handler(request =>
        {
            if (request.Uri.AbsolutePath == "/v3/collections")
                return Json("""[{"id":"new-collection","label":"Folder","feeds":[]}]""");
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal("/v3/collections/new-collection/feeds", request.Uri.AbsolutePath);
                return Json(FeedlyFeeds);
            }
            if (request.Uri.AbsolutePath == "/v3/search/feeds")
                return Json("""{"results":[{"feedId":"feed/https://example.com/feed","title":"Example"}]}""");
            if (request.Uri.AbsolutePath == "/v3/streams/ids")
                return Json("""{"ids":[]}""");
            if (request.Uri.AbsolutePath == "/v3/streams/contents")
                return Json("""{"id":"feed/https://example.com/feed","items":[]}""");
            Assert.Equal(HttpMethod.Delete, request.Method);
            return Json("{}");
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate(
            new Secrets.Credentials(Secrets.CredentialsType.OAuthAccessToken, "user", "test-access"), http: http));
        var folder = account.AddFolder("Folder"); folder.ExternalID = "old-collection";
        var original = account.AddFeed(FeedUrl, folder: folder);
        original.ExternalID = "feed/" + FeedUrl;
        original.ReaderViewAlwaysEnabled = true;
        await account.RemoveFolderAsync(folder);
        await account.RestoreFolderAsync(folder);
        Assert.Same(folder, Assert.Single(account.Folders));
        Assert.Equal("new-collection", folder.ExternalID);
        Assert.Same(original, Assert.Single(folder.Feeds));
        Assert.Equal(FeedUrl, original.FeedID);
        Assert.True(original.ReaderViewAlwaysEnabled);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task FeedlyAllowsRemovalOfAlreadyMissingSubscriptions(HttpStatusCode response)
    {
        using var http = new HttpClient(new Handler(_ => Json("{}", response)));
        var account = _manager.CreateAccount(accountDelegate: new FeedlyAccountDelegate(http: http));
        var folder = account.AddFolder("Folder"); folder.ExternalID = "collection";
        var feed = account.AddFeed(FeedUrl, folder: folder);
        await account.RemoveFeedAsync(feed, folder);
        Assert.Empty(folder.Feeds);
    }

    [Fact]
    public async Task ReaderAPICreatesSubscriptionAndTagsItOnTheServer()
    {
        using var handler = new Handler(request => request.Uri.AbsolutePath.Split('/').Last() switch
        {
            "token" => Plain("write-token"),
            "quickadd" => Json("""{"numResults":1,"streamId":"feed/123"}"""),
            "list" => Json("""{"subscriptions":[{"id":"feed/123","title":"Server title","url":"https://example.com/feed"}]}"""),
            "edit" => Plain("OK"),
            _ => throw new InvalidOperationException("Unexpected endpoint."),
        });
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(
            ReaderAPIVariant.FreshRSS, "https://reader.example/api/greader.php/", authToken: "auth-token", http: http));
        var folder = account.AddFolder("Folder & things");
        var feed = Assert.IsType<Feed>(await account.CreateFeedAsync(FeedUrl, "Custom title", folder));
        Assert.Equal("feed/123", feed.ExternalID);
        Assert.Equal("Custom title", feed.EditedName);
        var creation = handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/quickadd"));
        Assert.Equal(FeedUrl, Form(creation)["quickadd"]);
        Assert.Equal("write-token", Form(creation)["T"]);
        var edits = handler.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/edit")).Select(Form).ToList();
        Assert.Contains(edits, form => form.GetValueOrDefault("a") == "user/-/label/Folder & things");
        Assert.Contains(edits, form => form.GetValueOrDefault("t") == "Custom title");
        Assert.All(edits, form => Assert.Equal("feed/123", form["s"]));
    }

    [Fact]
    public async Task ReaderAPIMovesWithOneEditInsteadOfUnsubscribing()
    {
        using var handler = new Handler(request => request.Uri.AbsolutePath.EndsWith("/token") ? Plain("token") : Plain("OK"));
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new ReaderAPIAccountDelegate(
            ReaderAPIVariant.Inoreader, authToken: "auth", http: http));
        var source = account.AddFolder("Source");
        var destination = account.AddFolder("Destination");
        var feed = account.AddFeed(FeedUrl, folder: source); feed.ExternalID = "feed/123";
        await account.MoveFeedAsync(feed, source, destination);
        var edit = Form(handler.Requests.Single(r => r.Method == HttpMethod.Post));
        Assert.Equal("edit", edit["ac"]);
        Assert.Equal("user/-/label/Source", edit["r"]);
        Assert.Equal("user/-/label/Destination", edit["a"]);
        Assert.Empty(source.Feeds);
        Assert.Same(feed, Assert.Single(destination.Feeds));
    }

    [Fact]
    public async Task NewsBlurMutationDoesNotCommitBeforeTheServerAcceptsIt()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(_ => pending.Task);
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new NewsBlurAccountDelegate(http: http));
        var feed = account.AddFeed(FeedUrl, "Old name", feedID: "1"); feed.ExternalID = "1";
        var rename = account.RenameFeedAsync(feed, "New name");
        Assert.False(rename.IsCompleted);
        Assert.Equal("Old name", feed.NameForDisplay);
        pending.SetResult(Json("""{"code":-1,"message":"Rename rejected"}"""));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rename);
        Assert.Equal("Rename rejected", error.Message);
        Assert.Equal("Old name", feed.NameForDisplay);
    }

    [Fact]
    public async Task NewsBlurMovesAndDeletesInTheSelectedFolder()
    {
        using var handler = new Handler(_ => Json("""{"code":1}"""));
        using var http = new HttpClient(handler);
        var account = _manager.CreateAccount(accountDelegate: new NewsBlurAccountDelegate(http: http));
        var source = await account.CreateFolderAsync("Source");
        var destination = await account.CreateFolderAsync("Destination");
        var feed = account.AddFeed(FeedUrl, folder: source); feed.ExternalID = "123";
        await account.MoveFeedAsync(feed, source, destination);
        await account.RemoveFeedAsync(feed, destination);
        var move = Form(handler.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/move_feed_to_folder")));
        Assert.Equal("123", move["feed_id"]);
        Assert.Equal("Source", move["in_folder"]);
        Assert.Equal("Destination", move["to_folder"]);
        var deletion = Form(handler.Requests.Last());
        Assert.Equal("Destination", deletion["in_folder"]);
        Assert.Empty(account.FlattenedFeeds());
    }

    [Fact]
    public async Task FailedFolderCreationNeverAppearsAsLocalSuccess()
    {
        using var http = new HttpClient(new Handler(_ => Json("{}", HttpStatusCode.Unauthorized)));
        var account = _manager.CreateAccount(accountDelegate: new NewsBlurAccountDelegate(http: http));
        await Assert.ThrowsAsync<HttpRequestException>(() => account.CreateFolderAsync("Folder"));
        Assert.Empty(account.Folders);
    }

    private static Dictionary<string, string> Form(Request request) => request.Body.Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Plain(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed record Request(HttpMethod Method, Uri Uri, string Body);

    private sealed class Handler(Func<Request, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Handler(Func<Request, HttpResponseMessage> send) : this(request => Task.FromResult(send(request))) { }
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new Request(request.Method, request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(captured);
            return await send(captured);
        }
    }

    public void Dispose() => _manager.Dispose();
}
