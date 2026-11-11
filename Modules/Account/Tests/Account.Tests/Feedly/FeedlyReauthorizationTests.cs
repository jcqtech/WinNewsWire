using System.Net;
using System.Runtime.Versioning;
using System.Text;
using WinNewsWire.Feedly;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

[SupportedOSPlatform("windows")]
public sealed class FeedlyReauthorizationTests : IDisposable
{
    private readonly TestAccountManager _manager = new();

    [Fact]
    public async Task ARejectedRefreshTokenIsOnlyTriedOnce()
    {
        using var handler = new Handler(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        using var http = new HttpClient(handler);
        var accountDelegate = Delegate(http);
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        account.StoreCredentials(new(CredentialsType.OAuthRefreshToken, Guid.NewGuid().ToString(), "rejected-token"));
        Assert.False(await accountDelegate.ReauthorizeAsync());
        Assert.Equal(1, handler.Calls);
        Assert.False(await accountDelegate.ReauthorizeAsync());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NewCredentialsAllowAnotherAttempt()
    {
        using var handler = new Handler(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        using var http = new HttpClient(handler);
        var accountDelegate = Delegate(http);
        var account = _manager.CreateAccount(accountDelegate: accountDelegate);
        var username = Guid.NewGuid().ToString();
        account.StoreCredentials(new(CredentialsType.OAuthRefreshToken, username, "rejected-token"));
        Assert.False(await accountDelegate.ReauthorizeAsync());
        Assert.Equal(1, handler.Calls);
        account.StoreCredentials(new(CredentialsType.OAuthAccessToken, username, "fresh-token"));
        Assert.False(await accountDelegate.ReauthorizeAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TransientFailuresCanRetry()
    {
        using var handler = new Handler(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        var accountDelegate = Delegate(http);
        accountDelegate.RefreshToken = new(CredentialsType.OAuthRefreshToken, "user", "refresh");
        Assert.False(await accountDelegate.ReauthorizeAsync());
        Assert.False(await accountDelegate.ReauthorizeAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ConcurrentUnauthorizedCallsShareOneRefresh()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(() => response.Task);
        using var http = new HttpClient(handler);
        var accountDelegate = Delegate(http);
        var username = Guid.NewGuid().ToString();
        accountDelegate.RefreshToken = new(CredentialsType.OAuthRefreshToken, username, "refresh");
        var first = accountDelegate.ReauthorizeAsync();
        var second = accountDelegate.ReauthorizeAsync();
        Assert.Equal(1, handler.Calls);
        response.SetResult(new(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"id":"{{username}}","access_token":"access","refresh_token":"rotated","token_type":"Bearer","expires_in":3600}""",
                Encoding.UTF8, "application/json"),
        });
        Assert.All(await Task.WhenAll(first, second), result => Assert.True(result));
        Assert.Equal("access", accountDelegate.AccessToken?.Secret);
        Assert.Equal("rotated", accountDelegate.RefreshToken?.Secret);
        Assert.Equal(1, handler.Calls);
    }

    private static FeedlyAccountDelegate Delegate(HttpClient http)
        => new(config: new("test-client", "test-secret"), http: http);

    private sealed class Handler(Func<Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v3/auth/token", request.RequestUri!.AbsolutePath);
            Interlocked.Increment(ref Calls);
            return send();
        }
    }

    public void Dispose() => _manager.Dispose();
}
