using System.Net;
using WinNewsWire.Web;
using Xunit;

namespace Web.Tests;

public sealed class TestingHttpMessageHandlerRequestCountTests
{
    private const string Url = "https://example.com/v3/auth/token";
    private const string UrlSubstring = "/v3/auth/token";

    [Fact]
    public async Task RequestsAreCounted()
    {
        using var responses = new TestingHttpMessageHandler();
        responses.SetResponse(new(HttpStatusCode.BadRequest), UrlSubstring);
        using var http = new HttpClient(responses, disposeHandler: false);
        Assert.Equal(0, responses.RequestCount(UrlSubstring));
        await SendAsync(http, HttpMethod.Post);
        Assert.Equal(1, responses.RequestCount(UrlSubstring));
        await SendAsync(http, HttpMethod.Post);
        Assert.Equal(2, responses.RequestCount(UrlSubstring));
    }

    [Fact]
    public async Task CountingCanBeNarrowedToOneMethod()
    {
        using var responses = new TestingHttpMessageHandler();
        using var http = new HttpClient(responses, disposeHandler: false);
        await SendAsync(http, HttpMethod.Post);
        await SendAsync(http, HttpMethod.Get);
        Assert.Equal(1, responses.RequestCount(UrlSubstring, HttpMethod.Post));
        Assert.Equal(1, responses.RequestCount(UrlSubstring, HttpMethod.Get));
        Assert.Equal(2, responses.RequestCount(UrlSubstring));
    }

    [Fact]
    public async Task UnstubbedRequestsAreCountedToo()
    {
        using var responses = new TestingHttpMessageHandler();
        using var http = new HttpClient(responses, disposeHandler: false);
        await SendAsync(http, HttpMethod.Get);
        Assert.Equal(1, responses.RequestCount(UrlSubstring));
        Assert.Equal(0, responses.RequestCount("/v3/never/called"));
    }

    [Fact]
    public async Task AnotherTestsRequestsAreNotCounted()
    {
        using var mine = new TestingHttpMessageHandler();
        using var theirs = new TestingHttpMessageHandler();
        using var myClient = new HttpClient(mine, disposeHandler: false);
        using var theirClient = new HttpClient(theirs, disposeHandler: false);
        await SendAsync(myClient, HttpMethod.Post);
        await SendAsync(theirClient, HttpMethod.Post);
        Assert.Equal(1, theirs.RequestCount(UrlSubstring));
        Assert.Equal(1, mine.RequestCount(UrlSubstring));
    }

    private static async Task SendAsync(HttpClient http, HttpMethod method)
    {
        using var request = new HttpRequestMessage(method, Url);
        using var response = await http.SendAsync(request);
    }
}
