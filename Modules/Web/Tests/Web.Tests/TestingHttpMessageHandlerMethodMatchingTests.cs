using System.Net;
using WinNewsWire.Web;
using Xunit;

namespace Web.Tests;

public sealed class TestingHttpMessageHandlerMethodMatchingTests
{
    private const string Url = "https://example.com/unread_entries.json";
    private const string UrlSubstring = "unread_entries.json";

    [Fact]
    public async Task AMethodSpecificResponseAnswersOnlyThatMethod()
    {
        using var responses = new TestingHttpMessageHandler();
        responses.SetResponse(new(HttpStatusCode.UnprocessableEntity), UrlSubstring, HttpMethod.Post);
        using var http = new HttpClient(responses, disposeHandler: false);
        Assert.Equal(422, await StatusCodeAsync(http, HttpMethod.Post));
        Assert.Equal(200, await StatusCodeAsync(http, HttpMethod.Get));
    }

    [Fact]
    public async Task AResponseWithNoMethodAnswersEveryMethod()
    {
        using var responses = new TestingHttpMessageHandler();
        responses.SetResponse(new(HttpStatusCode.NotFound), UrlSubstring);
        using var http = new HttpClient(responses, disposeHandler: false);
        Assert.Equal(404, await StatusCodeAsync(http, HttpMethod.Post));
        Assert.Equal(404, await StatusCodeAsync(http, HttpMethod.Get));
        Assert.Equal(404, await StatusCodeAsync(http, HttpMethod.Delete));
    }

    [Fact]
    public async Task AMethodSpecificResponseWinsOverOneWithNoMethod()
    {
        using var responses = new TestingHttpMessageHandler();
        responses.SetResponse(new(HttpStatusCode.NotFound), UrlSubstring);
        responses.SetResponse(new(HttpStatusCode.UnprocessableEntity), UrlSubstring, HttpMethod.Post);
        using var http = new HttpClient(responses, disposeHandler: false);
        Assert.Equal(422, await StatusCodeAsync(http, HttpMethod.Post));
        Assert.Equal(404, await StatusCodeAsync(http, HttpMethod.Get));
    }

    private static async Task<int> StatusCodeAsync(HttpClient http, HttpMethod method)
    {
        using var request = new HttpRequestMessage(method, Url);
        using var response = await http.SendAsync(request);
        return (int)response.StatusCode;
    }
}
