using System.Net;
using System.Text;
using WinNewsWire.Feedbin;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedbin;

public sealed class FeedbinDownloadSyncTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task AccountFeedbinFolderContentsSyncTest()
    {
        using var fixture = new Fixture
        {
            Tags = "tags_add.json", Subscriptions = "subscriptions_initial.json", Taggings = "taggings_initial.json",
        };
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        var folder = account.Folders.Single(folder => folder.Name == "Developers");
        Assert.Equal(156, folder.Feeds.Count);
        Assert.Equal(2, account.TopLevelFeeds.Count);
        fixture.Taggings = "taggings_add.json";
        await account.RefreshAllAsync();
        Assert.Equal(157, folder.Feeds.Count);
        Assert.Single(account.TopLevelFeeds);
        fixture.Taggings = "taggings_delete.json";
        await account.RefreshAllAsync();
        Assert.Equal(153, folder.Feeds.Count);
        Assert.Equal(5, account.TopLevelFeeds.Count);
    }

    [Fact]
    public async Task AccountFeedbinFolderSyncTest()
    {
        using var fixture = new Fixture { Tags = "tags_initial.json" };
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        Assert.Equal(9, account.Folders.Count);
        Assert.Contains(account.Folders, folder => folder.Name == "Outdoors");
        fixture.Tags = "tags_delete.json";
        await account.RefreshAllAsync();
        Assert.Equal(8, account.Folders.Count);
        Assert.Contains(account.Folders, folder => folder.Name == "Outdoors");
        Assert.DoesNotContain(account.Folders, folder => folder.Name == "Tech Media");
        fixture.Tags = "tags_add.json";
        await account.RefreshAllAsync();
        Assert.Equal(10, account.Folders.Count);
        Assert.Contains(account.Folders, folder => folder.Name == "Vanlife");
    }

    [Fact]
    public async Task AccountFeedbinSyncTest()
    {
        using var fixture = new Fixture { Tags = "tags_add.json", Subscriptions = "subscriptions_initial.json" };
        using var http = new HttpClient(fixture);
        var account = _manager.CreateAccount(accountDelegate: new FeedbinAccountDelegate(http: http));
        await account.RefreshAllAsync();
        Assert.Equal(224, account.FlattenedFeeds().Count());
        var daringFireball = account.FlattenedFeeds().Single(feed => feed.FeedID == "1296379");
        Assert.Equal("Daring Fireball", daringFireball.Name);
        Assert.Equal("https://daringfireball.net/feeds/json", daringFireball.Url);
        Assert.Equal("https://daringfireball.net/", daringFireball.HomePageUrl);
        fixture.Subscriptions = "subscriptions_add.json";
        await account.RefreshAllAsync();
        Assert.Equal(225, account.FlattenedFeeds().Count());
        var beautifulPixels = account.FlattenedFeeds().Single(feed => feed.FeedID == "1096623");
        Assert.Equal("Beautiful Pixels", beautifulPixels.Name);
        Assert.Equal("https://feedpress.me/beautifulpixels", beautifulPixels.Url);
        Assert.Equal("https://beautifulpixels.com/", beautifulPixels.HomePageUrl);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public string? Tags { get; set; }
        public string? Subscriptions { get; set; }
        public string? Taggings { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fixture = request.RequestUri!.AbsolutePath switch
            {
                "/v2/tags.json" => Tags, "/v2/subscriptions.json" => Subscriptions, "/v2/taggings.json" => Taggings, _ => null,
            };
            var content = fixture is null ? "[]" : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "JSON", fixture));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }

    public void Dispose() => _manager.Dispose();
}
