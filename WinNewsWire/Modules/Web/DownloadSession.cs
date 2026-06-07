using System.Net;
using System.Net.Http.Headers;

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
    string? RetryAfter)
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
public sealed class DownloadSession : IDisposable
{
    private readonly IDownloadSessionDelegate _delegate;
    private readonly HttpClient _httpClient;
    private readonly DownloadCache _cache;

    // Synchronization: all mutable state accessed under this lock.
    private readonly object _lock = new();

    // Task tracking
    private readonly HashSet<Task> _tasksInProgress = [];
    private readonly Queue<Uri> _pendingQueue = new();
    private HashSet<Uri> _urlsInSession = [];
    private int _completedCount;
    private CancellationTokenSource _cts = new();

    // Concurrency: max pending tasks before queueing overflow
    private const int MaxPendingTasks = 500;

    // 429 tracking (per host)
    private readonly Dictionary<string, HttpResponse429> _retryAfterMessages = new(StringComparer.OrdinalIgnoreCase);

    // 4xx caching (skip for 53 hours)
    private readonly Dictionary<Uri, Http4xxResponse> _http4xxResponses = [];
    private static readonly TimeSpan Http4xxCacheExpiry = TimeSpan.FromHours(53);

    // Redirect caching
    private readonly Dictionary<Uri, Uri> _redirectCache = [];

    // OpenRSS throttle (persisted across sessions within the app lifetime)
    private static DateTime _lastOpenRSSRefresh = DateTime.MinValue;
    private static readonly object _openRSSGate = new();

    /// <summary>Current download progress.</summary>
    public ProgressInfo Progress { get; private set; }

    /// <summary>Raised when <see cref="Progress"/> changes.</summary>
    public event EventHandler<ProgressInfo>? ProgressChanged;

    public DownloadSession(IDownloadSessionDelegate @delegate, HttpClient? httpClient = null)
    {
        _delegate = @delegate;
        _cache = DownloadCache.Shared;

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

    /// <summary>
    /// Start downloading the given set of URLs. Each URL triggers delegate callbacks
    /// as it completes. When all are done, <see cref="IDownloadSessionDelegate.DownloadSessionDidCompleteAsync"/>
    /// is called.
    /// </summary>
    public void Download(IReadOnlySet<Uri> urls)
    {
        lock (_lock)
        {
            CleanUp4xxResponsesCache();
            var filtered = FilterUrls(urls);
            _urlsInSession = filtered;
            _completedCount = 0;
            _cts = new CancellationTokenSource();

            foreach (var url in filtered)
                AddDataTask(url);

            UpdateProgress();
        }
    }

    /// <summary>Cancel all in-progress and pending downloads.</summary>
    public void CancelAll()
    {
        lock (_lock)
        {
            _cts.Cancel();
            _pendingQueue.Clear();
            _tasksInProgress.Clear();
            _cts = new CancellationTokenSource();
            UpdateProgress();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _httpClient.Dispose();
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
            return;
        if (RequestShouldBeDroppedDueToPrevious4xx(urlToUse))
            return;

        // Check in-memory cache
        if (_cache[urlToUse.AbsoluteUri] is { } cached)
        {
            var cachedResponse = new DownloadSessionResponse(
                cached.StatusCode, urlToUse, null, null, null, null, null);
            _ = _delegate.DownloadDidCompleteAsync(url, cachedResponse, cached.Data ?? [], null);
            Interlocked.Increment(ref _completedCount);
            UpdateProgress();
            return;
        }

        var ct = _cts.Token;
        var task = Task.Run(() => ExecuteDownloadAsync(url, urlToUse, ct), ct);
        _tasksInProgress.Add(task);

        // When completed, remove from tracking and potentially start the next queued item
        _ = task.ContinueWith(_ =>
        {
            lock (_lock)
            {
                _tasksInProgress.Remove(task);
                Interlocked.Increment(ref _completedCount);
                AddDataTaskFromQueueIfNecessary();
                UpdateProgress();
                CheckCompletion();
            }
        }, TaskScheduler.Default);
    }

    private void AddDataTaskFromQueueIfNecessary()
    {
        // Must be called under _lock.
        if (_tasksInProgress.Count < MaxPendingTasks && _pendingQueue.TryDequeue(out var url))
            AddDataTask(url);
    }

    private void CheckCompletion()
    {
        // Must be called under _lock.
        if (Progress.IsComplete)
        {
            _urlsInSession.Clear();
            _ = _delegate.DownloadSessionDidCompleteAsync();
        }
    }

    // ── Private: HTTP execution ──

    private async Task ExecuteDownloadAsync(Uri originalUrl, Uri urlToUse, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, urlToUse);

            // Apply conditional GET headers
            var conditionalGetInfo = _delegate.ConditionalGetInfoFor(originalUrl);
            conditionalGetInfo?.ApplyTo(request);

            // Apply special-case user agent
            SpecialCases.AddSpecialCaseUserAgentIfNeeded(request);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Handle redirects manually
            if (IsRedirectStatusCode((int)response.StatusCode))
            {
                await FollowRedirectsAsync(originalUrl, urlToUse, response, ct).ConfigureAwait(false);
                return; // FollowRedirectsAsync handles delegate callbacks
            }

            var statusCode = (int)response.StatusCode;

            // Handle 4xx/5xx
            if (statusCode >= 400)
            {
                var snapshot = DownloadSessionResponse.FromHttpResponse(response);

                // Cache non-429 4xx responses
                if (statusCode != 429 && statusCode is >= 400 and <= 499)
                {
                    _cache.Add(urlToUse.AbsoluteUri, null, statusCode, null);
                    lock (_lock) { Cache4xxResponse(urlToUse, new Http4xxResponse(statusCode)); }
                }

                // Handle 429
                if (statusCode == 429)
                    Handle429Response(urlToUse, snapshot);

                await _delegate.HttpErrorAsync(statusCode, originalUrl).ConfigureAwait(false);
                return;
            }

            // Read data with streaming + early-cancel support
            var data = await ReadDataWithCallbackAsync(originalUrl, response, ct).ConfigureAwait(false);

            if (data is null)
                return; // Cancelled by delegate

            // Cache successful response
            var responseSnapshot = DownloadSessionResponse.FromHttpResponse(response);
            _cache.Add(urlToUse.AbsoluteUri, data, statusCode, null);

            await _delegate.DownloadDidCompleteAsync(originalUrl, responseSnapshot, data, null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var emptyResponse = new DownloadSessionResponse(0, urlToUse, null, null, null, null, null);
            await _delegate.DownloadDidCompleteAsync(originalUrl, emptyResponse, [], ex).ConfigureAwait(false);
        }
    }

    // Hard cap on accumulated response body size to prevent a malicious or
    // misconfigured feed from exhausting memory. 64 MB is well above any
    // reasonable feed size.
    private const int MaxAccumulatedBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Read data from the response stream in chunks, calling the delegate's
    /// ShouldContinueAfterReceivingData after each chunk with a read-only view
    /// over the accumulated buffer (no per-chunk copy).
    /// </summary>
    private async Task<byte[]?> ReadDataWithCallbackAsync(Uri originalUrl, HttpResponseMessage response, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (bytesRead == 0) break;

            if (ms.Length + bytesRead > MaxAccumulatedBytes)
                throw new InvalidDataException(
                    $"Response body for {originalUrl} exceeded maximum allowed size of {MaxAccumulatedBytes} bytes.");

            ms.Write(buffer, 0, bytesRead);

            // Hand the delegate a view over the already-accumulated bytes rather
            // than allocating a fresh copy on every chunk (previous ms.ToArray()
            // made this O(n^2) in allocations for large feeds).
            var view = new ReadOnlyMemory<byte>(ms.GetBuffer(), 0, (int)ms.Length);
            if (!_delegate.ShouldContinueAfterReceivingData(view, originalUrl))
                return null;
        }

        return ms.ToArray();
    }

    // ── Private: Redirect handling ──

    private static readonly HashSet<int> RedirectStatusCodes = [301, 302, 303, 307, 308];

    private static bool IsRedirectStatusCode(int statusCode) => RedirectStatusCodes.Contains(statusCode);

    private async Task FollowRedirectsAsync(Uri originalUrl, Uri currentUrl, HttpResponseMessage response, CancellationToken ct)
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

            // Now process the final response
            var statusCode = (int)current.StatusCode;

            if (statusCode >= 400)
            {
                if (statusCode != 429 && statusCode is >= 400 and <= 499)
                {
                    _cache.Add(currentUri.AbsoluteUri, null, statusCode, null);
                    lock (_lock) { Cache4xxResponse(currentUri, new Http4xxResponse(statusCode)); }
                }

                if (statusCode == 429)
                {
                    var snapshot = DownloadSessionResponse.FromHttpResponse(current);
                    Handle429Response(currentUri, snapshot);
                }

                await _delegate.HttpErrorAsync(statusCode, originalUrl).ConfigureAwait(false);
                return;
            }

            var data = await ReadDataWithCallbackAsync(originalUrl, current, ct).ConfigureAwait(false);
            if (data is null) return;

            var responseSnapshot = DownloadSessionResponse.FromHttpResponse(current);
            _cache.Add(currentUri.AbsoluteUri, data, statusCode, null);
            await _delegate.DownloadDidCompleteAsync(originalUrl, responseSnapshot, data, null).ConfigureAwait(false);
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

    private void Handle429Response(Uri url, DownloadSessionResponse response)
    {
        if (response.RetryAfter is null) return;
        if (!double.TryParse(response.RetryAfter, out var seconds) || seconds <= 0) return;

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
        // Remove queued URLs for this host
        var remaining = new Queue<Uri>();
        while (_pendingQueue.TryDequeue(out var url))
        {
            if (!url.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                remaining.Enqueue(url);
        }
        while (remaining.TryDequeue(out var url))
            _pendingQueue.Enqueue(url);
    }

    private bool RequestShouldBeDroppedDueToActive429(Uri url)
    {
        // Must be called under _lock.
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

    private static HashSet<Uri> FilterUrls(IReadOnlySet<Uri> urls)
    {
        // Throttle OpenRSS.org: one feed per refresh cycle, with a 10-minute app-wide cooldown.
        // The read-compare-write must be atomic so concurrent sessions/calls cannot both pass.
        bool canDownloadOpenRSS;
        lock (_openRSSGate)
        {
            canDownloadOpenRSS = DateTime.UtcNow > _lastOpenRSSRefresh + TimeSpan.FromMinutes(10);
            if (canDownloadOpenRSS)
                _lastOpenRSSRefresh = DateTime.UtcNow;
        }

        if (canDownloadOpenRSS)
            return SpecialCases.FilterOpenRSSUrls(urls);

        // Remove all OpenRSS URLs if within cooldown
        var result = new HashSet<Uri>();
        foreach (var url in urls)
        {
            if (!url.IsOpenRSSOrgUrl())
                result.Add(url);
        }
        return result;
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
