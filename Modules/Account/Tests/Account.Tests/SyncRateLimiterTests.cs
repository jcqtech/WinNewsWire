using System.Net;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class SyncRateLimiterTests
{
    [Fact]
    public void RetryAfterPausesOnlyUntilTheServerDeadline()
    {
        var clock = new Clock();
        var limiter = new SyncRateLimiter("Service", false, clock);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(90));
        Assert.Throws<SyncRateLimitException>(() => limiter.CheckResponse(response));
        Assert.Equal(clock.Now.AddSeconds(90), limiter.ResumeDate);
        clock.Now = clock.Now.AddSeconds(89);
        Assert.True(limiter.ShouldSkip());
        clock.Now = clock.Now.AddSeconds(1);
        Assert.False(limiter.ShouldSkip());
        Assert.Null(limiter.ResumeDate);
    }

    [Fact]
    public void ForbiddenIsAFeedlyBanButNotAUniversalRateLimit()
    {
        var clock = new Clock();
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        var feedly = new SyncRateLimiter("Feedly", true, clock);
        Assert.Throws<SyncRateLimitException>(() => feedly.CheckResponse(response));
        Assert.Equal(clock.Now.AddHours(1), feedly.ResumeDate);
        var other = new SyncRateLimiter("Other", false, clock);
        other.CheckResponse(response);
        Assert.False(other.ShouldSkip());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
