using System.Diagnostics;
using System.Net;

namespace WinNewsWire.Account;

public sealed class SyncRateLimiter(string serviceName, bool treatsForbiddenAsRateLimited, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    public DateTimeOffset? ResumeDate { get; private set; }

    public bool ShouldSkip()
    {
        if (ResumeDate > _timeProvider.GetUtcNow()) return true;
        ResumeDate = null;
        return false;
    }

    public void ThrowIfLimited()
    {
        if (ShouldSkip())
            throw new SyncRateLimitException(serviceName, HttpStatusCode.TooManyRequests, ResumeDate - _timeProvider.GetUtcNow());
    }

    public bool IsRateLimitError(Exception error)
        => error is AggregateException aggregate ? aggregate.InnerExceptions.Any(IsRateLimitError)
            : error is HttpRequestException http && (http.StatusCode == HttpStatusCode.TooManyRequests
                || treatsForbiddenAsRateLimited && http.StatusCode == HttpStatusCode.Forbidden);

    public void NoteRateLimited(Exception error)
    {
        if (!IsRateLimitError(error)) throw new ArgumentException("The error is not a service rate limit.", nameof(error));
        var retry = error is SyncRateLimitException limit ? limit.RetryAfter : null;
        ResumeDate = _timeProvider.GetUtcNow() + (retry ?? TimeSpan.FromHours(1));
        Trace.TraceWarning($"{serviceName} is rate limited until {ResumeDate:O}.");
    }

    public void CheckResponse(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests
            && !(treatsForbiddenAsRateLimited && response.StatusCode == HttpStatusCode.Forbidden)) return;
        var retry = response.Headers.RetryAfter?.Delta;
        if (retry is null && response.Headers.RetryAfter?.Date is { } date) retry = date - _timeProvider.GetUtcNow();
        if (retry < TimeSpan.Zero) retry = TimeSpan.Zero;
        var error = new SyncRateLimitException(serviceName, response.StatusCode, retry);
        NoteRateLimited(error);
        throw error;
    }
}

public sealed class SyncRateLimitException(string serviceName, HttpStatusCode status, TimeSpan? retryAfter)
    : HttpRequestException($"{serviceName} is rate limited. Try again after {(retryAfter ?? TimeSpan.FromHours(1)).TotalMinutes:0.#} minutes.", null, status)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
