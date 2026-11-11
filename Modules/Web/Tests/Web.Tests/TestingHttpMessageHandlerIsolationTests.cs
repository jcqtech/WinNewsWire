using System.Net;
using WinNewsWire.Web;
using Xunit;

namespace Web.Tests;

public sealed class TestingHttpMessageHandlerIsolationTests
{
    private const string Url = "https://example.com/subscriptions.json";
    private const string UrlSubstring = "subscriptions.json";

    [Fact]
    public async Task ConcurrentTestsDoNotSeeEachOthersResponses()
    {
        var results = await Task.WhenAll(Task.Run(() => StatusCodeAsync(418)), Task.Run(() => StatusCodeAsync(503)));
        Assert.Equal(418, results[0]);
        Assert.Equal(503, results[1]);
    }

    [Fact]
    public async Task ResetClearsOnlyTheCurrentTestsResponses()
    {
        using var keeper = new TestingHttpMessageHandler();
        using var resetter = new TestingHttpMessageHandler();
        keeper.SetResponse(new((HttpStatusCode)418), UrlSubstring);
        resetter.SetResponse(new(HttpStatusCode.ServiceUnavailable), UrlSubstring);
        resetter.Reset();

        using var http = new HttpClient(keeper, disposeHandler: false);
        using var response = await http.GetAsync(Url);
        Assert.Equal(418, (int)response.StatusCode);
    }

    [Fact]
    public async Task AClientBuiltOutsideATestScopeUsesItsExplicitlySharedResponses()
    {
        using var shared = new TestingHttpMessageHandler();
        using var http = new HttpClient(shared, disposeHandler: false);
        shared.SetResponse(new(HttpStatusCode.NotFound), UrlSubstring);
        using var response = await http.GetAsync(Url);
        Assert.Equal(404, (int)response.StatusCode);
    }

    private static async Task<int> StatusCodeAsync(int statusCode)
    {
        using var responses = new TestingHttpMessageHandler();
        responses.SetResponse(new((HttpStatusCode)statusCode), UrlSubstring);
        using var http = new HttpClient(responses, disposeHandler: false);
        await Task.Yield();
        using var response = await http.GetAsync(Url);
        return (int)response.StatusCode;
    }
}
