using System.Text.Json;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Scripting;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

[Collection("Account storage")]
public sealed class ScriptingServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scripting-" + Guid.NewGuid().ToString("N"));
    private readonly List<Account.Account> _accounts = [];
    private readonly List<AppActivationRequest> _opened = [];
    private readonly Account.Account _account;
    private readonly Feed _feed;
    private readonly Folder _folder;
    private readonly ScriptingService _service;
    private ScriptingSelection _selection = ScriptingSelection.Empty;

    public ScriptingServiceTests()
    {
        AppConfig.SetDataDirectoryOverride(_directory);
        _account = new(AccountManager.DefaultAccountID, AccountType.OnMyMac, "On My PC", new FixtureDelegate());
        _accounts.Add(_account);
        _feed = _account.AddFeed("https://example.com/feed", "Publication name");
        _feed.EditedName = "Edited label";
        _feed.Authors = new HashSet<Author> { Author.Create("author", "Fixture Author", "https://example.com/author", null, "author@example.test")! };
        _folder = _account.AddFolder("News");
        _account.AddFeed("https://example.com/nested", "Nested", _folder);
        _service = new ScriptingService(() => _accounts, () => _account, () => _selection, _opened.Add, "1.0");
    }

    [Fact]
    public void NativeGenericScriptRetainsItsHarnessResult()
    {
        var result = ScriptingProtocol.Result(null, "Geoducks!");
        Assert.True(result.Success);
        Assert.Equal("Geoducks!", result.Value!.Value.GetString());
    }

    [Fact]
    public async Task NativeOpenLocationHasNoReturnValue()
    {
        var response = await _service.HandleAsync(new("openLocation", Url: "http://scripting.com/rss"));
        Assert.True(response.Success);
        Assert.False(response.HasValue);
        Assert.Null(response.Value);
        Assert.Equal(new AppActivationRequest(AppActivationKind.AddFeed, "http://scripting.com/rss"), Assert.Single(_opened));
    }

    [Fact]
    public async Task NativeNameAndUrlOfEveryFeedOfEveryAccount()
    {
        foreach (var account in await References(new("get", Property: "accounts")))
            foreach (var feed in await References(new("get", account, "feeds")))
            {
                Assert.Equal("Publication name", await Text(new("get", feed, "name")));
                Assert.Equal("https://example.com/feed", await Text(new("get", feed, "url")));
            }
    }

    [Fact]
    public async Task NativeNameOfEveryFolderOfEveryAccount()
    {
        foreach (var account in await References(new("get", Property: "accounts")))
            Assert.Equal("News", await Text(new("get", Assert.Single(await References(new("get", account, "folders"))), "name")));
    }

    [Fact]
    public async Task NativeNameOfAuthorsHasAResult()
    {
        var names = new List<string>();
        foreach (var account in await References(new("get", Property: "accounts")))
            foreach (var feed in await References(new("get", account, "feeds")))
                foreach (var author in await References(new("get", feed, "authors")))
                    names.Add(await Text(new("get", author, "name")));
        Assert.NotEmpty(names);
        Assert.Equal("Fixture Author", Assert.Single(names));
    }

    [Fact]
    public async Task NativeFeedExists()
    {
        var account = (await References(new("get", Property: "accounts")))[0];
        var feed = (await References(new("get", account, "feeds")))[0];
        var result = await _service.HandleAsync(new("exists", feed));
        Assert.True(result.Success);
        Assert.True(result.Value!.Value.GetBoolean());
    }

    [Fact]
    public async Task RootFeedAndAllFeedCollectionsRetainTheirDifferentScopes()
    {
        var account = new ScriptingReference(ScriptingObjectKind.Account, _account.AccountID);
        Assert.Single(await References(new("get", Property: "feeds")));
        Assert.Single(await References(new("get", account, "feeds")));
        Assert.Equal(2, (await References(new("get", account, "allFeeds"))).Length);
        var count = await _service.HandleAsync(new("count", account, "allFeeds"));
        Assert.Equal(2, count.Value!.Value.GetInt32());
        var set = await _service.HandleAsync(new("set", account, "active", JsonSerializer.SerializeToElement(false)));
        Assert.True(set.Success);
        Assert.Empty(await References(new("get", Property: "feeds")));
        Assert.Single(await References(new("get", Property: "accounts")));
    }

    [Fact]
    public async Task ArticleIdsAreFeedSuppliedAndWritableFlagsUseTheAccountFacade()
    {
        var article = new Article(_account.AccountID, "database-id", _feed.FeedID, "feed-supplied-id", "Title",
            "<p>HTML body</p>", "Text body", null, "https://example.com/permalink", "https://example.com/external",
            "Summary", null, DateTime.UtcNow, null, _feed.Authors, null, new ArticleStatus("database-id", true, false, DateTime.UtcNow));
        await _account.Database.UpdateArticlesAsync([article]);
        _selection = new(article, [article], [(_feed, null)]);
        var reference = Assert.Single(await References(new("get", Property: "selectedArticles")));
        Assert.Equal("feed-supplied-id", reference.ArticleID);
        Assert.Equal("feed-supplied-id", await Text(new("get", reference, "id")));
        Assert.Equal("https://example.com/permalink", await Text(new("get", reference, "url")));
        Assert.Equal("https://example.com/external", await Text(new("get", reference, "externalUrl")));
        Assert.Equal("<p>HTML body</p>", await Text(new("get", reference, "html")));
        Assert.True((await _service.HandleAsync(new("set", reference, "read", JsonSerializer.SerializeToElement(false)))).Success);
        Assert.True((await _service.HandleAsync(new("set", reference, "starred", JsonSerializer.SerializeToElement(true)))).Success);
        var stored = Assert.Single(await _account.Database.FetchArticlesByIDsAsync(["database-id"]));
        Assert.False(stored.Status.Read);
        Assert.True(stored.Status.Starred);
        var invalidDatabaseReference = reference with { ArticleID = "database-id" };
        Assert.False((await _service.HandleAsync(new("exists", invalidDatabaseReference))).Value!.Value.GetBoolean());
    }

    [Fact]
    public async Task MakeAndDeleteAreContainerScopedAndDoNotDuplicateExistingFeeds()
    {
        var parent = new ScriptingReference(ScriptingObjectKind.Folder, _account.AccountID, _folder.FolderID);
        var made = await _service.HandleAsync(new("make", parent, NewKind: ScriptingObjectKind.Feed,
            Name: "Made", Url: "https://example.com/made"));
        Assert.True(made.Success, made.Error?.Message);
        var reference = made.Value!.Value.Deserialize<ScriptingReference>(ScriptingProtocol.Options)!;
        Assert.Equal(_folder.FolderID, reference.FolderID);
        Assert.Contains(_folder.Feeds, feed => feed.Url == "https://example.com/made");
        var duplicate = await _service.HandleAsync(new("make", parent, NewKind: ScriptingObjectKind.Feed, Url: _feed.Url));
        var existing = duplicate.Value!.Value.Deserialize<ScriptingReference>(ScriptingProtocol.Options)!;
        Assert.Null(existing.FolderID);
        Assert.DoesNotContain(_feed, _folder.Feeds);
        Assert.True((await _service.HandleAsync(new("delete", reference))).Success);
        Assert.DoesNotContain(_folder.Feeds, feed => feed.Url == "https://example.com/made");
        Assert.Contains(_feed, _account.TopLevelFeeds);
    }

    [Fact]
    public async Task InvalidScopesReadonlyFieldsAndSensitivePropertiesAreRejected()
    {
        var account = new ScriptingReference(ScriptingObjectKind.Account, _account.AccountID);
        Assert.False((await _service.HandleAsync(new("get", account, "password"))).Success);
        Assert.False((await _service.HandleAsync(new("set", account, "name", JsonSerializer.SerializeToElement(true)))).Success);
        Assert.Equal("On My PC", _account.NameForDisplay);
        Assert.False((await _service.HandleAsync(new("delete", account))).Success);
        var wrongFolder = new ScriptingReference(ScriptingObjectKind.Feed, _account.AccountID, _folder.FolderID, _feed.FeedID);
        Assert.False((await _service.HandleAsync(new("exists", wrongFolder))).Value!.Value.GetBoolean());
        Assert.False((await _service.HandleAsync(new("get", new(ScriptingObjectKind.Application, _account.AccountID)))).Success);
        Assert.Throws<JsonException>(() => ScriptingProtocol.Parse("""{"command":"get","credentials":true}"""));
        Assert.Throws<JsonException>(() => ScriptingProtocol.Parse("""{"command":"get","target":{"kind":1}}"""));
    }

    private async Task<ScriptingReference[]> References(ScriptingRequest request)
    {
        var result = await _service.HandleAsync(request);
        Assert.True(result.Success, result.Error?.Message);
        return result.Value!.Value.Deserialize<ScriptingReference[]>(ScriptingProtocol.Options)!;
    }

    private async Task<string> Text(ScriptingRequest request)
    {
        var result = await _service.HandleAsync(request);
        Assert.True(result.Success, result.Error?.Message);
        return result.Value!.Value.GetString()!;
    }

    private sealed class FixtureDelegate : IAccountDelegate
    {
        private readonly LocalAccountDelegate _local = new();
        public AccountType Type => AccountType.OnMyMac;
        public Task RefreshAllAsync(Account.Account account, IProgress<ProgressInfo>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task<Feed?> CreateFeedAsync(Account.Account account, string url, string? name, Folder? folder, CancellationToken ct)
            => Task.FromResult<Feed?>(account.AddFeed(url, name, folder));
        public Task<Folder> CreateFolderAsync(Account.Account account, string name, CancellationToken ct = default)
            => _local.CreateFolderAsync(account, name, ct);
        public Task RemoveFeedAsync(Account.Account account, Feed feed, Folder? folder, CancellationToken ct = default)
            => _local.RemoveFeedAsync(account, feed, folder, ct);
        public Task RemoveFolderAsync(Account.Account account, Folder folder, CancellationToken ct = default)
            => _local.RemoveFolderAsync(account, folder, ct);
    }

    public void Dispose()
    {
        foreach (var account in _accounts) account.Dispose();
        AppConfig.SetDataDirectoryOverride(null);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
