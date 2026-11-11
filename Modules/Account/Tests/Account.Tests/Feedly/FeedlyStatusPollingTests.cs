using System.Net;
using System.Text;
using WinNewsWire.Articles;
using WinNewsWire.Feedly;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.Account.Tests.Feedly;

public sealed class FeedlyStatusPollingTests : IDisposable
{
    private readonly TestAccountManager _storage = new();

    [Fact]
    public async Task IdlePollingSkipsStarredAndBacksOffForExactlyThirtyMinutes()
    {
        var clock = new Clock();
        int requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Contains("global.all", request.RequestUri!.AbsoluteUri);
            Assert.Contains("unreadOnly=true", request.RequestUri.Query);
            requests++;
            return Task.FromResult(Json("""{"ids":[]}"""));
        }));
        var account = Create(http, clock);
        Assert.False(await account.SyncArticleStatusAsync());
        clock.Now += TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1);
        Assert.False(await account.SyncArticleStatusAsync());
        Assert.Equal(1, requests);
        clock.Now += TimeSpan.FromTicks(1);
        Assert.False(await account.SyncArticleStatusAsync());
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task AQueuedStarExitsBackoffAndSendsWithoutWalkingTheStarredStream()
    {
        int reads = 0, sends = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.EndsWith("/markers", request.RequestUri!.AbsolutePath);
                sends++;
                return Task.FromResult(Json("{}"));
            }
            Assert.DoesNotContain("global.saved", request.RequestUri!.AbsoluteUri);
            reads++;
            return Task.FromResult(Json("""{"ids":[]}"""));
        }));
        var account = Create(http, new Clock());
        Assert.False(await account.SyncArticleStatusAsync());
        await account.Database.CreateStatusesIfNeededAsync(["article"]);
        await account.MarkAsync(["article"], ArticleStatus.Key.Starred, true);
        Assert.True(await account.SyncArticleStatusAsync());
        Assert.Equal(1, sends);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task AnEditQueuedDuringAnIdleResponsePreventsRearmingTheProviderBackoff()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0, sends = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) { sends++; return Json("{}"); }
            if (++reads == 1) { started.SetResult(); await release.Task.WaitAsync(ct); }
            return Json("""{"ids":[]}""");
        }));
        var account = Create(http, new Clock());
        await account.Database.CreateStatusesIfNeededAsync(["article"]);
        var syncing = account.SyncArticleStatusAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await account.MarkAsync(["article"], ArticleStatus.Key.Starred, true);
        release.SetResult();
        Assert.False(await syncing);
        Assert.True(await account.SyncArticleStatusAsync());
        Assert.Equal(1, sends);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task FailedPollingDoesNotLookIdleAndCanRetryImmediately()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json("""{"ids":[]}"""))));
        var account = Create(http, new Clock());
        await Assert.ThrowsAsync<HttpRequestException>(() => account.SyncArticleStatusAsync());
        Assert.False(await account.SyncArticleStatusAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RateLimitedPollingWaitsInsteadOfRepeatingTheRequest()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        var account = Create(http, new Clock());
        Assert.False(await account.SyncArticleStatusAsync());
        Assert.False(await account.SyncArticleStatusAsync());
        Assert.Equal(1, calls);
    }

    private Account Create(HttpClient http, TimeProvider clock) => _storage.CreateAccount(accountDelegate:
        new FeedlyAccountDelegate(new Credentials(CredentialsType.OAuthAccessToken, "user", "test-access"), http: http, timeProvider: clock));
    private static HttpResponseMessage Json(string value)
        => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose() => _storage.Dispose();
}
