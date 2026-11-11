using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Diagnostics;
using WinNewsWire.Core;

namespace WinNewsWire.Web;

/// <summary>
/// Port of <c>DownloadSessionDelegate</c>. Callbacks for download lifecycle events.
/// </summary>
public interface IDownloadSessionDelegate
{
    /// <summary>Return conditional GET info (ETag/Last-Modified) for the given feed URL, if available.</summary>
    ConditionalGetInfo? ConditionalGetInfoFor(Uri url);

    /// <summary>Called when a single URL download completes (success or failure).</summary>
    Task DownloadDidCompleteAsync(Uri url, DownloadSessionResponse response, byte[] data, Exception? error);

    /// <summary>
    /// Called after receiving data. Return false to cancel the download early
    /// (e.g. if the data is clearly not a feed — image bytes detected).
    /// The <paramref name="dataSoFar"/> is a read-only view over the accumulated
    /// data buffer; it is only valid for the duration of the call and must not
    /// be stored by the implementation.
    /// </summary>
    bool ShouldContinueAfterReceivingData(ReadOnlyMemory<byte> dataSoFar, Uri url);

    /// <summary>Called when the server returns an HTTP error (4xx/5xx).</summary>
    Task HttpErrorAsync(int statusCode, Uri url);

    /// <summary>Called when all URLs in the session have completed.</summary>
    Task DownloadSessionDidCompleteAsync();
}

/// <summary>Immutable snapshot of an HTTP response (not a live <c>HttpResponseMessage</c>).</summary>
public sealed record DownloadSessionResponse(
    int StatusCode,
    Uri? FinalUri,
    string? ETag,
    string? LastModified,
    string? CacheControl,
    string? ContentType,
    string? RetryAfter,
    bool ReturnedFromCache = false)
{
    public bool StatusIsOK => StatusCode is >= 200 and <= 299;
    public bool IsNotModified => StatusCode == (int)HttpStatusCode.NotModified;

    public ConditionalGetInfo? ToConditionalGetInfo()
    {
        if (ETag is null && LastModified is null) return null;
        return new ConditionalGetInfo(LastModified, ETag);
    }

    public CacheControlInfo? ToCacheControlInfo()
        => CacheControlInfo.Parse(CacheControl);

    internal static DownloadSessionResponse FromHttpResponse(HttpResponseMessage resp)
    {
        string? etag = resp.Headers.ETag?.Tag;
        string? lastModified = resp.Content.Headers.LastModified?.ToString("R");
        string? cacheControl = resp.Headers.CacheControl?.ToString();
        string? contentType = resp.Content.Headers.ContentType?.MediaType;
        string? retryAfter = null;
        if (resp.Headers.RetryAfter?.Delta is { } delta)
            retryAfter = ((int)delta.TotalSeconds).ToString();
        else if (resp.Headers.RetryAfter?.Date is { } date)
            retryAfter = ((int)(date - DateTimeOffset.UtcNow).TotalSeconds).ToString();

        return new DownloadSessionResponse(
            (int)resp.StatusCode,
            resp.RequestMessage?.RequestUri,
            etag, lastModified, cacheControl, contentType, retryAfter);
    }
}

/// <summary>
/// Port of <c>DownloadSession.swift</c>. Manages batched, concurrent HTTP feed downloads
/// with conditional GET, 429/4xx handling, redirect caching, and progress reporting.
///
/// <para>Usage: create a <c>DownloadSession</c> with a delegate, then call
/// <see cref="Download"/> with a set of URLs. The delegate receives callbacks as
/// each download completes.</para>
/// </summary>
public sealed class DownloadSession : IDisposable, IAsyncDisposable
{
    private readonly IDownloadSessionDelegate _delegate;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    // Synchronization: all mutable state accessed under this lock.
    private readonly object _lock = new();

    // Task tracking
    private readonly Dictionary<Task, (Uri Url, CancellationTokenSource Cancellation)> _tasksInProgress = new();
    private readonly Queue<Uri> _pendingQueue = new();
    private HashSet<Uri> _urlsInSession = [];
    private int _completedCount;
    private CancellationTokenSource _cts = new();
    private bool _completionNotified;
    private bool _disposed;
    private TaskCompletionSource? _completion;
    private readonly List<Exception> _callbackErrors = [];

    // Concurrency: max pending tasks before queueing overflow
    private const int MaxPendingTasks = 500;
    internal const int MaximumBufferedResponses = 4;
    private readonly SemaphoreSlim _responseSlots = new(MaximumBufferedResponses, MaximumBufferedResponses);

    // 429 tracking (per host)
    private readonly Dictionary<string, HttpResponse429> _retryAfterMessages = new(StringComparer.OrdinalIgnoreCase);

    // 4xx caching (skip for 53 hours)
    private readonly Dictionary<Uri, Http4xxResponse> _http4xxResponses = [];
    private static readonly TimeSpan Http4xxCacheExpiry = TimeSpan.FromHours(53);

    // Redirect caching
    private readonly Dictionary<Uri, Uri> _redirectCache = [];

    private static readonly object _openRSSGate = new();
    private const string LastOpenRSSRefreshKey = "lastOpenRSSOrgFeedRefresh";
    private readonly Core.AppDefaults? _defaults;
    private readonly TimeProvider _clock = TimeProvider.System;

    public Task Completion { get { lock (_lock) return _completion?.Task ?? Task.CompletedTask; } }

    /// <summary>Current download progress.</summary>
    public ProgressInfo Progress { get; private set; }

    /// <summary>Raised when <see cref="Progress"/> changes.</summary>
    public event EventHandler<ProgressInfo>? ProgressChanged;

    public DownloadSession(IDownloadSessionDelegate @delegate, HttpClient? httpClient = null)
    {
        _delegate = @delegate;
        _ownsHttpClient = httpClient is null;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // Handle redirects manually for caching
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 1,
                UseCookies = false,
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.Value);
        }
    }

    internal DownloadSession(IDownloadSessionDelegate @delegate, HttpClient httpClient, Core.AppDefaults defaults, TimeProvider clock)
        : this(@delegate, httpClient)
    {
        _defaults = defaults;
        _clock = clock;
    }

    /// <summary>
    /// Start downloading the given set of URLs. Each URL triggers delegate callbacks
    /// as it completes. When all are done, <see cref="IDownloadSessionDelegate.DownloadSessionDidCompleteAsync"/>
    /// is called.
    /// </summary>
    public void Download(IReadOnlySet<Uri> urls)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tasksInProgress.Count > 0 || _pendingQueue.Count > 0 || _completion?.Task.IsCompleted == false)
                throw new InvalidOperationException("Wait for the current download batch before starting another.");
            CleanUp4xxResponsesCache();
            var filtered = FilterUrls(urls);
            _urlsInSession = filtered;
            _completedCount = 0;
            _completionNotified = false;
            _callbackErrors.Clear();
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _cts.Dispose();
            _cts = new CancellationTokenSource();

            foreach (var url in filtered)
                AddDataTask(url);

            UpdateProgress();
            CheckCompletion();
        }
    }

    /// <summary>Cancel all in-progress and pending downloads.</summary>
    public void CancelAll()
    {
        lock (_lock)
        {
            if (!_disposed) CancelAllCore();
        }
    }

    private void CancelAllCore()
    {
        _cts.Cancel();
        _completedCount += _pendingQueue.Count;
        _pendingQueue.Clear();
        UpdateProgress();
        CheckCompletion();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            CancelAllCore();
        }
        if (_ownsHttpClient) _httpClient.Dispose();
        _ = Completion.ContinueWith(completed =>
        {
            if (completed.IsFaulted) Trace.TraceError($"Download session failed during disposal: {completed.Exception}");
            _cts.Dispose();
            _responseSlots.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Completion.ConfigureAwait(false);
    }

    // ── Private: Task management ──

    private void AddDataTask(Uri url)
    {
        // Must be called under _lock.
        if (_tasksInProgress.Count >= MaxPendingTasks)
        {
            _pendingQueue.Enqueue(url);
            return;
        }

        var urlToUse = CachedRedirect(url) ?? url;

        if (RequestShouldBeDroppedDueToActive429(urlToUse))
        {
            _completedCount++;
            return;
        }
        if (RequestShouldBeDroppedDueToPrevious4xx(urlToUse))
        {
            _completedCount++;
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = cancellation.Token;
        var task = Task.Run(() => ExecuteDownloadAsync(url, urlToUse, ct), ct);
        _tasksInProgress.Add(task, (urlToUse, cancellation));

        // When completed, remove from tracking and potentially start the next queued item
        _ = task.ContinueWith(_ =>
        {
            lock (_lock)
            {
                cancellation.Dispose();
                if (task.IsFaulted)
                {
                    Trace.TraceError($"Download callback failed: {task.Exception}");
                    _callbackErrors.AddRange(task.Exception!.InnerExceptions);
                }
                if (!_tasksInProgress.Remove(task)) return;
                Interlocked.Increment(ref _completedCount);
                if (!_disposed) AddDataTaskFromQueueIfNecessary();
                UpdateProgress();
                CheckCompletion();
            }
        }, TaskScheduler.Default);
    }

    private void AddDataTaskFromQueueIfNecessary()
    {
        // Must be called under _lock.
        while (_tasksInProgress.Count < MaxPendingTasks && _pendingQueue.TryDequeue(out var url))
            AddDataTask(url);
    }

    private void CheckCompletion()
    {
        // Must be called under _lock.
        if (_completion is not null && !_completionNotified && (Progress.IsComplete || _urlsInSession.Count == 0))
        {
            _completionNotified = true;
            _urlsInSession.Clear();
            _ = NotifyCompletionAsync(_completion, _callbackErrors.ToArray());
        }
    }

    private async Task NotifyCompletionAsync(TaskCompletionSource completion, IReadOnlyList<Exception> errors)
    {
        try
        {
            await _delegate.DownloadSessionDidCompleteAsync().ConfigureAwait(false);
            if (errors.Count == 0) completion.TrySetResult();
            else completion.TrySetException(errors);
        }
        catch (Exception error)
        {
            Trace.TraceError($"Download completion callback failed: {error}");
            completion.TrySetException(errors.Append(error));
        }
    }

    // ── Private: HTTP execution ──

    private async Task ExecuteDownloadAsync(Uri originalUrl, Uri urlToUse, CancellationToken ct)
    {
        bool responseAdmitted = false;
        try
        {
            DownloadSessionResponse responseSnapshot;
            byte[]? data;
            Exception? error = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, urlToUse);

                // Apply conditional GET headers
                var conditionalGetInfo = _delegate.ConditionalGetInfoFor(originalUrl);
                conditionalGetInfo?.ApplyTo(request);

                // Apply special-case user agent
                SpecialCases.AddSpecialCaseUserAgentIfNeeded(request);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)response.StatusCode < 400)
                {
                    await _responseSlots.WaitAsync(ct).ConfigureAwait(false);
                    responseAdmitted = true;
                }
                ct.ThrowIfCancellationRequested();

                // ResponseHeadersRead stops HttpClient's timeout at the headers.
                using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bodyCancellation.CancelAfter(_httpClient.Timeout);
                if (IsRedirectStatusCode((int)response.StatusCode))
                {
                    (responseSnapshot, data) = await FollowRedirectsAsync(originalUrl, urlToUse, response, bodyCancellation.Token).ConfigureAwait(false);
                }
                else
                {
                    (responseSnapshot, data) = await ReadResponseAsync(originalUrl, urlToUse, response, bodyCancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                responseSnapshot = new(0, urlToUse, null, null, null, null, null);
                data = [];
                error = ex;
            }
            if (responseSnapshot.StatusCode >= 400)
                await _delegate.HttpErrorAsync(responseSnapshot.StatusCode, originalUrl).ConfigureAwait(false);
            else if (data is not null)
                await _delegate.DownloadDidCompleteAsync(originalUrl, responseSnapshot, data, error).ConfigureAwait(false);
        }
        finally
        {
            // Hold the permit until parsing/storage releases the complete response body.
            if (responseAdmitted) _responseSlots.Release();
        }
    }

    private async Task<(DownloadSessionResponse Response, byte[]? Data)> ReadResponseAsync(
        Uri originalUrl, Uri responseUrl, HttpResponseMessage response, CancellationToken ct)
    {
        var snapshot = DownloadSessionResponse.FromHttpResponse(response);
        if (snapshot.StatusCode >= 400)
        {
            if (snapshot.StatusCode != 429 && snapshot.StatusCode <= 499)
                lock (_lock) Cache4xxResponse(responseUrl, new Http4xxResponse(snapshot.StatusCode));
            if (snapshot.StatusCode == 429) Handle429Response(responseUrl, snapshot);
            return (snapshot, null);
        }
        return (snapshot, await ReadDataWithCallbackAsync(originalUrl, response, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Read data from the response stream in chunks, calling the delegate's
    /// ShouldContinueAfterReceivingData after each chunk with a read-only view
    /// over the accumulated buffer (no per-chunk copy).
    /// </summary>
    private Task<byte[]?> ReadDataWithCallbackAsync(Uri originalUrl, HttpResponseMessage response, CancellationToken ct)
        => HttpContentReader.ReadAsync(response.Content, ct, data => _delegate.ShouldContinueAfterReceivingData(data, originalUrl));

    // ── Private: Redirect handling ──

    private static readonly HashSet<int> RedirectStatusCodes = [301, 302, 303, 307, 308];

    private static bool IsRedirectStatusCode(int statusCode) => RedirectStatusCodes.Contains(statusCode);

    private async Task<(DownloadSessionResponse Response, byte[]? Data)> FollowRedirectsAsync(
        Uri originalUrl, Uri currentUrl, HttpResponseMessage response, CancellationToken ct)
    {
        const int maxRedirects = 10;
        var redirectCount = 0;
        var current = response;
        var currentUri = currentUrl;

        try
        {
            while (IsRedirectStatusCode((int)current.StatusCode) && redirectCount < maxRedirects)
            {
                redirectCount++;
                var locationHeader = current.Headers.Location;
                if (locationHeader is null) break;

                var newUrl = locationHeader.IsAbsoluteUri ? locationHeader : new Uri(currentUri, locationHeader);

                // Cache redirect (with captive portal detection)
                lock (_lock) { CacheRedirect(currentUri, newUrl); }

                current.Dispose();

                using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, newUrl);
                SpecialCases.AddSpecialCaseUserAgentIfNeeded(redirectRequest);

                var conditionalGetInfo = _delegate.ConditionalGetInfoFor(originalUrl);
                conditionalGetInfo?.ApplyTo(redirectRequest);

                current = await _httpClient.SendAsync(redirectRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                currentUri = newUrl;
            }

            return await ReadResponseAsync(originalUrl, currentUri, current, ct).ConfigureAwait(false);
        }
        finally
        {
            current.Dispose();
        }
    }

    private void CacheRedirect(Uri oldUrl, Uri newUrl)
    {
        // Must be called under _lock.
        if (SpecialCases.IsDisallowedRedirect(newUrl.AbsoluteUri))
            return;
        _redirectCache[oldUrl] = newUrl;
    }

    private Uri? CachedRedirect(Uri url)
    {
        // Must be called under _lock. Follow chains, avoid loops.
        var visited = new HashSet<Uri> { url };
        var current = url;

        while (_redirectCache.TryGetValue(current, out var redirect))
        {
            if (!visited.Add(redirect))
                return null; // Cycle detected
            current = redirect;
        }

        return current == url ? null : current;
    }

    // ── Private: 429 handling ──

    internal void Handle429Response(Uri url, DownloadSessionResponse response)
    {
        if (response.RetryAfter is null) return;
        if (!double.TryParse(response.RetryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds) || seconds <= 0)
        {
            Trace.TraceWarning("Ignoring an invalid Retry-After interval.");
            return;
        }

        var message = HttpResponse429.Create(url, TimeSpan.FromSeconds(seconds));
        if (message is null) return;

        lock (_lock)
        {
            _retryAfterMessages[message.Host] = message;
            CancelAndRemoveTasksWithHost(message.Host);
        }
    }

    private void CancelAndRemoveTasksWithHost(string host)
    {
        // Must be called under _lock.
        var activeURLs = TasksWithHost(host, _tasksInProgress.Values.Select(task => task.Url));
        foreach (var task in _tasksInProgress.Values.Where(task => activeURLs.Contains(task.Url)).ToList())
            task.Cancellation.Cancel();
        var remaining = new Queue<Uri>();
        while (_pendingQueue.TryDequeue(out var url))
        {
            if (!url.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                remaining.Enqueue(url);
            else _completedCount++;
        }
        while (remaining.TryDequeue(out var url))
            _pendingQueue.Enqueue(url);
    }

    internal static HashSet<Uri> TasksWithHost(string host, IEnumerable<Uri> urls)
        => urls.Where(url => url.Host.Equals(host, StringComparison.OrdinalIgnoreCase)).ToHashSet();

    internal bool RequestShouldBeDroppedDueToActive429(Uri url)
    {
        lock (_lock)
        {
            var host = url.Host;
            if (string.IsNullOrEmpty(host)) return false;
            if (!_retryAfterMessages.TryGetValue(host, out var message)) return false;
            if (message.CanResume)
            {
                _retryAfterMessages.Remove(host);
                return false;
            }
            return true;
        }
    }

    // ── Private: 4xx handling ──

    private void Cache4xxResponse(Uri url, Http4xxResponse response)
    {
        // Must be called under _lock.
        if (url.IsYouTubeUrl()) return;
        _http4xxResponses[url] = response;
    }

    private void CleanUp4xxResponsesCache()
    {
        // Must be called under _lock.
        var cutoff = DateTime.UtcNow - Http4xxCacheExpiry;
        var toRemove = new List<Uri>();
        foreach (var (url, response) in _http4xxResponses)
        {
            if (response.Date < cutoff)
                toRemove.Add(url);
        }
        foreach (var url in toRemove)
            _http4xxResponses.Remove(url);
    }

    private bool RequestShouldBeDroppedDueToPrevious4xx(Uri url)
    {
        // Must be called under _lock.
        if (_http4xxResponses.ContainsKey(url)) return true;
        if (CachedRedirect(url) is { } redirected && _http4xxResponses.ContainsKey(redirected)) return true;
        return false;
    }

    // ── Private: URL filtering ──

    private HashSet<Uri> FilterUrls(IReadOnlySet<Uri> urls)
    {
        if (!urls.Any(url => url.IsOpenRSSOrgUrl())) return new(urls);
        lock (_openRSSGate)
        {
            var defaults = _defaults ?? Core.AppDefaults.Shared;
            var now = _clock.GetUtcNow().UtcDateTime;
            var last = defaults.GetValue<DateTime?>(LastOpenRSSRefreshKey);
            if (last is null || now - last > TimeSpan.FromHours(1))
            {
                defaults.SetValue(LastOpenRSSRefreshKey, now);
                return SpecialCases.FilterOpenRSSUrls(urls);
            }
        }
        return urls.Where(url => !url.IsOpenRSSOrgUrl()).ToHashSet();
    }

    // ── Private: Progress ──

    private void UpdateProgress()
    {
        // Must be called under _lock (or atomically).
        var total = _urlsInSession.Count;
        if (total == 0)
        {
            Progress = ProgressInfo.Empty;
        }
        else
        {
            var remaining = _tasksInProgress.Count + _pendingQueue.Count;
            Progress = new ProgressInfo(remaining, _completedCount, total);
        }

        ProgressChanged?.Invoke(this, Progress);
    }
}
