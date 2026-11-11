using System.Net;
using WinNewsWire.Account;
using WinNewsWire.Feedbin;
using WinNewsWire.Feedly;
using WinNewsWire.NewsBlur;
using WinNewsWire.ReaderAPI;
using WinNewsWire.Secrets;
using Xunit;

namespace WinNewsWire.RemoteAccounts.Tests;

public sealed class RemoteClientLifetimeTests
{
    [Theory]
    [InlineData("Feedbin", false)]
    [InlineData("Feedbin", true)]
    [InlineData("Feedly", false)]
    [InlineData("Feedly", true)]
    [InlineData("Reader", false)]
    [InlineData("Reader", true)]
    [InlineData("NewsBlur", false)]
    [InlineData("NewsBlur", true)]
    public async Task CallersCloseOnlyOwnedHttpClientsAndRejectFurtherRequests(string provider, bool owns)
    {
        using var http = new TrackingClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var (caller, request) = Caller(provider, http, owns);
        caller.Dispose();
        caller.Dispose();
        Assert.Equal(owns ? 1 : 0, http.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(request);
        if (!owns)
        {
            using var response = await http.GetAsync("https://fixture.test/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("Feedbin")]
    [InlineData("Reader")]
    [InlineData("NewsBlur")]
    public async Task DefaultAsyncDelegateDisposalClosesItsApiButNotInjectedTransport(string provider)
    {
        using var http = new TrackingClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        IAccountDelegate accountDelegate;
        Func<Task> request;
        switch (provider)
        {
            case "Feedbin":
                var feedbin = new FeedbinAccountDelegate(http: http);
                accountDelegate = feedbin;
                request = () => feedbin.API.ValidateCredentialsAsync();
                break;
            case "Reader":
                var reader = new ReaderAPIAccountDelegate(ReaderAPIVariant.FreshRSS, "https://fixture.test", http: http);
                accountDelegate = reader;
                request = () => reader.API.LoginAsync("user", "fixture");
                break;
            default:
                var newsBlur = new NewsBlurAccountDelegate(http: http);
                accountDelegate = newsBlur;
                request = () => newsBlur.API.LoginAsync("user", "fixture");
                break;
        }
        Assert.True(accountDelegate.CanDisposeSynchronously);
        await accountDelegate.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(request);
        Assert.Equal(0, http.DisposeCount);
    }

    [Fact]
    public async Task FeedlyAsyncDisposalCancelsAndDrainsTokenRefreshWithoutPersistingCredentials()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new TrackingClient(new Handler(async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await release.Task;
                throw;
            }
            throw new InvalidOperationException("Unreachable");
        }));
        var accountDelegate = new FeedlyAccountDelegate(
            new(CredentialsType.OAuthAccessToken, "user", "fixture-access"),
            new(CredentialsType.OAuthRefreshToken, "user", "fixture-refresh"),
            new("fixture-client", "fixture-secret", "fixture.test"), http);
        var pending = accountDelegate.API.ReauthorizeAsync!(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(accountDelegate.CanDisposeSynchronously);
            Assert.Throws<InvalidOperationException>(() => accountDelegate.Dispose());
            var disposal = accountDelegate.DisposeAsync().AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(disposal.IsCompleted);
            Assert.Same(disposal, accountDelegate.DisposeAsync().AsTask());
            release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Null(accountDelegate.AccessToken);
            Assert.Null(accountDelegate.RefreshToken);
            Assert.Null(accountDelegate.API.ReauthorizeAsync);
            Assert.Equal(0, http.DisposeCount);
            accountDelegate.Dispose();
        }
        finally
        {
            release.TrySetResult();
            await accountDelegate.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CancelledWaiterDoesNotHidePendingFeedlyAuthentication()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new TrackingClient(new Handler(async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await release.Task;
                throw;
            }
            throw new InvalidOperationException("Unreachable");
        }));
        var accountDelegate = new FeedlyAccountDelegate(refreshToken: new(CredentialsType.OAuthRefreshToken, "user", "fixture"),
            config: new("fixture", "fixture", "fixture.test"), http: http);
        using var cancellation = new CancellationTokenSource();
        var pending = accountDelegate.API.ReauthorizeAsync!(cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var idle = accountDelegate.WaitForPendingWorkAsync();
            Assert.False(idle.IsCompleted);
            release.TrySetResult();
            await idle.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(accountDelegate.CanDisposeSynchronously);
        }
        finally
        {
            release.TrySetResult();
            await accountDelegate.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task FeedlyDisposalStillDrainsAuthenticationWhenACancellationCallbackThrows()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackError = new InvalidOperationException("Cancellation callback failed.");
        using var http = new TrackingClient(new Handler(async (_, ct) =>
        {
            using var registration = ct.Register(() => throw callbackError);
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { await release.Task; throw; }
            throw new InvalidOperationException("Unreachable");
        }));
        var accountDelegate = new FeedlyAccountDelegate(refreshToken: new(CredentialsType.OAuthRefreshToken, "user", "fixture"),
            config: new("fixture", "fixture", "fixture.test"), http: http);
        var pending = accountDelegate.API.ReauthorizeAsync!(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposal = accountDelegate.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Same(disposal, accountDelegate.DisposeAsync().AsTask());
            release.TrySetResult();
            var error = await Assert.ThrowsAsync<AggregateException>(() => disposal.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains(callbackError, error.Flatten().InnerExceptions);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => accountDelegate.API.SearchFeedsAsync("https://fixture.test/feed"));
            Assert.Null(accountDelegate.RefreshToken);
            Assert.Equal(0, http.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
            try { await accountDelegate.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (AggregateException error) when (error.Flatten().InnerExceptions.Contains(callbackError)) { }
        }
    }

    [Fact]
    public void DisposedFeedlyDelegateRejectsTokenPersistenceAndCredentialChanges()
    {
        var accountDelegate = new FeedlyAccountDelegate();
        accountDelegate.Dispose();
        Assert.Throws<ObjectDisposedException>(() => accountDelegate.ApplyTokenResponse(
            new("fixture-user", "fixture-access", "fixture-refresh", "Bearer", 3600, "")));
        Assert.Throws<ObjectDisposedException>(() => accountDelegate.AccessToken = new(CredentialsType.OAuthAccessToken, "user", "fixture"));
        Assert.Throws<ObjectDisposedException>(() => accountDelegate.RefreshToken = new(CredentialsType.OAuthRefreshToken, "user", "fixture"));
    }

    private static (IDisposable Owner, Func<Task> Request) Caller(string provider, HttpClient http, bool owns)
    {
        switch (provider)
        {
            case "Feedbin":
                var feedbin = new FeedbinAPICaller(http, owns);
                return (feedbin, () => feedbin.ValidateCredentialsAsync());
            case "Feedly":
                var feedly = new FeedlyAPICaller("fixture.test", http, owns);
                return (feedly, () => feedly.SearchFeedsAsync("https://fixture.test/feed"));
            case "Reader":
                var reader = new ReaderAPICaller(ReaderAPIVariant.FreshRSS, "https://fixture.test", http, owns);
                return (reader, () => reader.LoginAsync("user", "fixture"));
            default:
                var newsBlur = new NewsBlurAPICaller(http, owns);
                return (newsBlur, () => newsBlur.LoginAsync("user", "fixture"));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
    private sealed class TrackingClient(HttpMessageHandler handler) : HttpClient(handler)
    {
        public int DisposeCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
