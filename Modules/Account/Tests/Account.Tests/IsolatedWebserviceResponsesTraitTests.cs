using System.Net;
using WinNewsWire.Web;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class IsolatedWebserviceResponsesTraitTests : IDisposable
{
    private readonly TestingHttpMessageHandler _responses = new();

    [Fact]
    public void EachTestHasAnExplicitlyOwnedResponseScope()
    {
        Assert.NotNull(_responses);
        Assert.Equal(0, _responses.RequestCount("subscriptions.json"));
    }

    [Fact]
    public async Task ResponsesRegisteredHereAreInvisibleToAnotherTestScope()
    {
        const string url = "https://example.com/subscriptions.json";
        _responses.SetResponse(new((HttpStatusCode)418), "subscriptions.json");
        using var mine = new HttpClient(_responses, disposeHandler: false);
        using var myResponse = await mine.GetAsync(url);
        Assert.Equal(418, (int)myResponse.StatusCode);
        using var otherResponses = new TestingHttpMessageHandler();
        using var theirs = new HttpClient(otherResponses, disposeHandler: false);
        using var theirResponse = await theirs.GetAsync(url);
        Assert.Equal(200, (int)theirResponse.StatusCode);
    }

    public void Dispose() => _responses.Dispose();
}
