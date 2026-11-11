using System.Net;
using WinNewsWire.Web;
using Xunit;

namespace Web.Tests;

public sealed class TestingHttpMessageHandlerTests
{
    private const string Url = "https://example.com/fixture";

    [Fact]
    public async Task ResetClearsBothResponsesAndRequestHistory()
    {
        using var responses = new TestingHttpMessageHandler();
        using var http = new HttpClient(responses, disposeHandler: false);
        responses.SetResponse(new(HttpStatusCode.BadRequest), "fixture");
        using var before = await http.GetAsync(Url);
        Assert.Equal(HttpStatusCode.BadRequest, before.StatusCode);
        Assert.Equal(1, responses.RequestCount("fixture"));
        responses.Reset();
        Assert.Equal(0, responses.RequestCount("fixture"));
        using var after = await http.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Empty(await after.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, responses.RequestCount("fixture"));
    }

    [Fact]
    public async Task RegisteringTheWildcardLastDoesNotOverrideAMethodSpecificResponse()
    {
        using var responses = new TestingHttpMessageHandler();
        using var http = new HttpClient(responses, disposeHandler: false);
        responses.SetResponse(new(HttpStatusCode.UnprocessableEntity), "fixture", HttpMethod.Post);
        responses.SetResponse(new(HttpStatusCode.NotFound), "fixture");
        using var post = await http.PostAsync(Url, null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, post.StatusCode);
        using var get = await http.GetAsync(Url);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task ResponsesOwnTheirBytesAndMatchingRegistrationsCanBeReplaced()
    {
        using var responses = new TestingHttpMessageHandler();
        using var http = new HttpClient(responses, disposeHandler: false);
        byte[] data = [1, 2, 3];
        responses.SetResponse(new(Data: data), "fixture");
        data[0] = 99;
        using var first = await http.GetAsync(Url);
        var firstBytes = await first.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 1, 2, 3 }, firstBytes);
        firstBytes[1] = 99;
        using var second = await http.GetAsync(Url);
        Assert.Equal(new byte[] { 1, 2, 3 }, await second.Content.ReadAsByteArrayAsync());
        responses.SetResponse(new(HttpStatusCode.NoContent), "fixture");
        using var replaced = await http.GetAsync(Url);
        Assert.Equal(HttpStatusCode.NoContent, replaced.StatusCode);
    }

    [Fact]
    public async Task ExplicitlySharedClientsKeepTheOwnersResponsesAndHistory()
    {
        using var responses = new TestingHttpMessageHandler();
        responses.SetResponse(new(HttpStatusCode.NotFound), "fixture");
        using var second = new HttpClient(responses, disposeHandler: false);
        using (var first = new HttpClient(responses, disposeHandler: false))
        using (var response = await first.GetAsync(Url))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var after = await second.GetAsync(Url);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.Equal(2, responses.RequestCount("fixture"));
    }

    [Fact]
    public async Task ConcurrentRequestsAreCountedWithoutLoss()
    {
        using var responses = new TestingHttpMessageHandler();
        using var http = new HttpClient(responses, disposeHandler: false);
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            using var response = await http.GetAsync(Url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        })));
        Assert.Equal(200, responses.RequestCount("fixture"));
    }

    [Fact]
    public async Task CancellationAndDisposalCannotProduceSuccessfulFixtureResponses()
    {
        using var responses = new TestingHttpMessageHandler();
        using var http = new HttpClient(responses, disposeHandler: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.GetAsync(Url, cancellation.Token));
        Assert.Equal(0, responses.RequestCount("fixture"));
        responses.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => http.GetAsync(Url));
        Assert.Throws<ObjectDisposedException>(() => responses.SetResponse(new(), "fixture"));
        Assert.Throws<ObjectDisposedException>(() => responses.RequestCount("fixture"));
        Assert.Throws<ObjectDisposedException>(responses.Reset);
    }
}
