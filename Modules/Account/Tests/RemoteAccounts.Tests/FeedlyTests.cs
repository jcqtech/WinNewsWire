using System.Text.Json;
using WinNewsWire.Feedly;
using Xunit;

namespace WinNewsWire.RemoteAccounts.Tests;

public class FeedlyTests
{
    [Fact]
    public void ResourceIds_FeedUrlRoundTrip()
    {
        var id = FeedlyResourceIds.FeedIdForUrl("https://example.com/feed.xml");
        Assert.Equal("feed/https://example.com/feed.xml", id);
        Assert.Equal("https://example.com/feed.xml", FeedlyResourceIds.UrlFromFeedId(id));
        // non-feed-prefixed is returned unchanged
        Assert.Equal("arbitrary", FeedlyResourceIds.UrlFromFeedId("arbitrary"));
    }

    [Fact]
    public void ResourceIds_BuildsUserStreams()
    {
        Assert.Equal("user/abc/category/global.all", FeedlyResourceIds.GlobalAllFor("abc"));
        Assert.Equal("user/abc/tag/global.saved", FeedlyResourceIds.GlobalSavedFor("abc"));
        Assert.Equal("user/abc/category/global.uncategorized", FeedlyResourceIds.GlobalUncategorizedFor("abc"));
    }

    [Fact]
    public void MarkAction_ActionValueMapping()
    {
        Assert.Equal("markAsRead", FeedlyMarkAction.Read.ActionValue());
        Assert.Equal("keepUnread", FeedlyMarkAction.Unread.ActionValue());
        Assert.Equal("markAsSaved", FeedlyMarkAction.Saved.ActionValue());
        Assert.Equal("markAsUnsaved", FeedlyMarkAction.Unsaved.ActionValue());
    }

    [Fact]
    public void BuildAuthorizeUri_IncludesAllParamsEncoded()
    {
        var req = new FeedlyOAuthAuthorizeRequest(
            ClientId: "my id",
            RedirectUri: "http://127.0.0.1:7878/feedly-callback/",
            Scope: "https://cloud.feedly.com/subscriptions",
            State: "abc 123");
        var uri = FeedlyBrowserAuth.BuildAuthorizeUri("cloud.feedly.com", req);
        Assert.Equal("cloud.feedly.com", uri.Host);
        Assert.Equal("/v3/auth/auth", uri.AbsolutePath);
        var q = uri.Query;
        Assert.Contains("response_type=code", q);
        Assert.Contains("client_id=my%20id", q);
        Assert.Contains("scope=https%3A%2F%2Fcloud.feedly.com%2Fsubscriptions", q);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A7878%2Ffeedly-callback%2F", q);
        Assert.Contains("state=abc%20123", q);
    }

    [Fact]
    public void ParseAuthorizeRedirect_HappyPath()
    {
        var uri = new Uri("http://127.0.0.1:7878/feedly-callback/?code=THE_CODE&state=xyz");
        var resp = FeedlyOAuthParser.ParseAuthorizeRedirect(uri, "xyz");
        Assert.Equal("THE_CODE", resp.Code);
        Assert.Equal("xyz", resp.State);
    }

    [Fact]
    public void ParseAuthorizeRedirect_RejectsStateMismatch()
    {
        var uri = new Uri("http://127.0.0.1:7878/feedly-callback/?code=C&state=wrong");
        Assert.Throws<FeedlyOAuthAuthorizeException>(() =>
            FeedlyOAuthParser.ParseAuthorizeRedirect(uri, "expected"));
    }

    [Fact]
    public void ParseAuthorizeRedirect_SurfacesErrorCode()
    {
        var uri = new Uri("http://127.0.0.1:7878/feedly-callback/?error=access_denied&state=s");
        var ex = Assert.Throws<FeedlyOAuthAuthorizeException>(() =>
            FeedlyOAuthParser.ParseAuthorizeRedirect(uri, "s"));
        Assert.Equal(FeedlyOAuthAuthorizeError.AccessDenied, ex.Error);
        Assert.Equal("s", ex.State);
    }

    [Fact]
    public void OAuthAccessTokenResponse_DeserializesSnakeCase()
    {
        const string json = """
        {"id":"user/abc","access_token":"A","refresh_token":"R","token_type":"Bearer","expires_in":604800,"scope":"s"}
        """;
        var resp = JsonSerializer.Deserialize<FeedlyOAuthAccessTokenResponse>(json)!;
        Assert.Equal("user/abc", resp.Id);
        Assert.Equal("A", resp.AccessToken);
        Assert.Equal("R", resp.RefreshToken);
        Assert.Equal(604800, resp.ExpiresIn);
    }

    [Fact]
    public void Entry_ExternalUrl_PrefersHtmlLinks()
    {
        const string json = """
        {"id":"e1","crawled":1700000000000,"unread":true,
         "alternate":[{"href":"https://rss","type":"application/rss+xml"},{"href":"https://page","type":"text/html"}]}
        """;
        var entry = JsonSerializer.Deserialize<FeedlyEntry>(json)!;
        Assert.Equal("https://page", entry.ExternalUrl);
    }

    [Fact]
    public void Entry_DatePublished_DecodesMilliseconds()
    {
        const string json = """{"id":"e1","crawled":1700000000000,"unread":false}""";
        var entry = JsonSerializer.Deserialize<FeedlyEntry>(json)!;
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), entry.DatePublished);
    }

    [Fact]
    public void Collection_DeserializesWithFeeds()
    {
        const string json = """
        {"id":"user/x/category/Tech","label":"Tech","feeds":[
          {"id":"feed/https://example.com/rss","title":"Example","website":"https://example.com"}]}
        """;
        var c = JsonSerializer.Deserialize<FeedlyCollection>(json)!;
        Assert.Equal("Tech", c.Label);
        Assert.Single(c.Feeds);
        Assert.Equal("feed/https://example.com/rss", c.Feeds[0].Id);
    }
}
