using System.Net;
using System.Net.Http;
using System.Diagnostics;

namespace WinNewsWire.Web;

public sealed record DownloadResult(
    string Url,
    HttpStatusCode StatusCode,
    byte[]? Data,
    string? ContentType,
    ConditionalGetInfo? ConditionalGet,
    bool ReturnedFromCache = false)
{
    public bool NotModified => StatusCode == HttpStatusCode.NotModified;
    public bool Success => (int)StatusCode is >= 200 and < 300;
}

/// <summary>Port of <c>Downloader</c> / <c>OneShotDownloadManager</c>. Single-shot HTTP GET over a shared HttpClient.</summary>
public sealed class Downloader
{
    public static Downloader Shared { get; } = new();

    private readonly HttpClient _http;
    private readonly DownloadCache _cache;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingDownload> _pending = new(StringComparer.Ordinal);

    public Downloader(HttpClient? http = null, TimeProvider? clock = null)
    {
        _cache = clock is null ? DownloadCache.Shared : new DownloadCache(clock);
        if (http is not null) { _http = http; return; }
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            MaxConnectionsPerServer = 1,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.Value);
    }

    internal Downloader(HttpClient http, DownloadCache cache) : this(http) => _cache = cache;

    public void EmptyCache() => _cache.RemoveAll();

    public async Task<DownloadResult> DownloadAsync(string url, ConditionalGetInfo? conditional = null, CancellationToken ct = default,
        UserAgentStyle userAgentStyle = UserAgentStyle.Feed, bool cachesResponse = true)
    {
        ct.ThrowIfCancellationRequested();
        var key = new Uri(url, UriKind.Absolute).AbsoluteUri;
        PendingDownload pending;
        bool coalesced;
        lock (_gate)
        {
            if (_cache[key] is { } cached) return cached with { ReturnedFromCache = true };
            coalesced = _pending.TryGetValue(key, out var existing);
            if (existing is not null) pending = existing;
            else
            {
                var cancellation = new CancellationTokenSource();
                pending = new(Task.Run(() => DownloadCoreAsync(url, key, conditional, userAgentStyle, cachesResponse,
                    cancellation.Token), cancellation.Token), cancellation);
                _pending.Add(key, pending);
            }
            pending.Waiters++;
        }
        try
        {
            var result = await pending.Task.WaitAsync(ct).ConfigureAwait(false);
            return result with { ReturnedFromCache = coalesced };
        }
        finally
        {
            lock (_gate)
            {
                pending.Waiters--;
                if (pending.Waiters == 0)
                {
                    if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(key);
                    if (!pending.Task.IsCompleted) pending.Cancellation.Cancel();
                    _ = pending.Task.ContinueWith(completed =>
                    {
                        if (completed.IsFaulted && ct.IsCancellationRequested)
                            Trace.TraceError($"Download failed after cancellation: {completed.Exception}");
                        pending.Cancellation.Dispose();
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }

    private async Task<DownloadResult> DownloadCoreAsync(string url, string key, ConditionalGetInfo? conditional,
        UserAgentStyle userAgentStyle, bool cachesResponse, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        conditional?.ApplyTo(req);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgentStyle switch
        {
            UserAgentStyle.Browser => UserAgent.BrowserValue,
            UserAgentStyle.SpecialCaseFeed => UserAgent.ExtendedValue,
            _ => UserAgent.Value
        });
        SpecialCases.AddSpecialCaseUserAgentIfNeeded(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var contentType = resp.Content.Headers.ContentType?.MediaType;
        var data = resp.StatusCode == HttpStatusCode.NotModified ? null : await HttpContentReader.ReadAsync(resp.Content, ct).ConfigureAwait(false);
        var conditionalGet = ConditionalGetInfo.FromResponse(resp.Headers, resp.Content.Headers);
        var result = new DownloadResult(resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url, resp.StatusCode, data, contentType, conditionalGet);
        ct.ThrowIfCancellationRequested();
        if (cachesResponse) _cache.Add(key, result);
        return result;
    }

    private sealed class PendingDownload(Task<DownloadResult> task, CancellationTokenSource cancellation)
    {
        public Task<DownloadResult> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int Waiters { get; set; }
    }
}
