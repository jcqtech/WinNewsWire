using System.Net;
using System.Text;
using System.Text.Json;
using WinNewsWire.Feedbin;
using WinNewsWire.Feedly;
using WinNewsWire.ReaderAPI;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.RemoteAccounts.Tests;

public class OpmlImportTests
{
    private static readonly byte[] Opml = Encoding.UTF8.GetBytes("<opml version=\"1.1\"><body/></opml>");

    [Fact]
    public async Task FeedbinUsesNativeImportEndpointAndPreservesCompletionStatus()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.feedbin.com/v2/imports.json", request.RequestUri!.AbsoluteUri);
            Assert.Equal("text/xml", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("utf-8", request.Content.Headers.ContentType.CharSet);
            Assert.Equal(Opml, await request.Content.ReadAsByteArrayAsync());
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            return Json("""{"id":42,"complete":false}""");
        }));
        var api = new FeedbinAPICaller(http) { Credentials = new(CredentialsType.Basic, "test-user", "test-password") };
        var result = await api.ImportOpmlAsync(Opml);
        Assert.Equal(42, result.ImportResultID);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task FeedbinPollsTheReturnedImportId()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v2/imports/42.json", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"id":42,"complete":true}"""));
        }));
        Assert.True((await new FeedbinAPICaller(http).RetrieveOpmlImportResultAsync(42)).Complete);
    }

    [Fact]
    public async Task FeedbinMalformedImportResultIsNotAnEndlessPendingImport()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json("{}"))));
        await Assert.ThrowsAsync<JsonException>(() => new FeedbinAPICaller(http).ImportOpmlAsync(Opml));
    }

    [Fact]
    public async Task FeedlyPostsRawOpmlAndDoesNotDecodeTheSuccessBody()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v3/opml", request.RequestUri!.AbsolutePath);
            Assert.Equal("OAuth", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization.Parameter);
            Assert.Equal("text/xml", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(Opml, await request.Content.ReadAsByteArrayAsync());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("accepted") };
        }));
        var api = new FeedlyAPICaller(http: http) { AccessToken = new(CredentialsType.OAuthAccessToken, "test-user", "test-token") };
        await api.ImportOpmlAsync(Opml);
    }

    [Fact]
    public async Task FeedlyImportRefreshesAuthenticationAndResendsTheBody()
    {
        int attempts = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(Opml, await request.Content!.ReadAsByteArrayAsync());
            return new HttpResponseMessage(++attempts == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        }));
        var api = new FeedlyAPICaller(http: http)
        {
            AccessToken = new(CredentialsType.OAuthAccessToken, "test-user", "test-token"),
            ReauthorizeAsync = _ => Task.FromResult(true),
        };
        await api.ImportOpmlAsync(Opml);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ReaderApiPostsRawOpmlToTheConfiguredHost()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.com/api/greader.php/reader/api/0/subscription/import", request.RequestUri!.AbsoluteUri);
            Assert.Equal("GoogleLogin", request.Headers.Authorization!.Scheme);
            Assert.Equal("auth=test-token", request.Headers.Authorization.Parameter);
            Assert.Equal("text/xml", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(Opml, await request.Content.ReadAsByteArrayAsync());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var api = new ReaderAPICaller(ReaderAPIVariant.FreshRSS, "https://example.com/api/greader.php/", http) { AuthToken = "test-token" };
        await api.ImportOpmlAsync(Opml);
    }

    [Fact]
    public async Task ImportHttpFailuresAreReported()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))));
        await Assert.ThrowsAsync<HttpRequestException>(() => new FeedbinAPICaller(http).ImportOpmlAsync(Opml));
        var feedly = new FeedlyAPICaller(http: http) { AccessToken = new(CredentialsType.OAuthAccessToken, "test-user", "test-token") };
        await Assert.ThrowsAsync<HttpRequestException>(() => feedly.ImportOpmlAsync(Opml));
        var reader = new ReaderAPICaller(ReaderAPIVariant.FreshRSS, "https://example.com/", http) { AuthToken = "test-token" };
        await Assert.ThrowsAsync<HttpRequestException>(() => reader.ImportOpmlAsync(Opml));
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}
