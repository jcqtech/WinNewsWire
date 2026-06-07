using System.Net;
using System.Net.Http;

namespace WinNewsWire.Web;

public sealed record DownloadResult(
    string Url,
    HttpStatusCode StatusCode,
    byte[]? Data,
    string? ContentType,
    ConditionalGetInfo? ConditionalGet)
{
    public bool NotModified => StatusCode == HttpStatusCode.NotModified;
    public bool Success => (int)StatusCode is >= 200 and < 300;
}

/// <summary>Port of <c>Downloader</c> / <c>OneShotDownloadManager</c>. Single-shot HTTP GET over a shared HttpClient.</summary>
public sealed class Downloader
{
    public static Downloader Shared { get; } = new();

    private readonly HttpClient _http;

    public Downloader(HttpClient? http = null)
    {
        if (http is not null) { _http = http; return; }
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent.Value);
    }

    public async Task<DownloadResult> DownloadAsync(string url, ConditionalGetInfo? conditional = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        conditional?.ApplyTo(req);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotModified)
            return new DownloadResult(url, resp.StatusCode, null, null, null);
        var ct2 = resp.Content.Headers.ContentType?.MediaType;
        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var cg = resp.IsSuccessStatusCode ? ConditionalGetInfo.FromResponse(resp.Headers, resp.Content.Headers) : null;
        return new DownloadResult(url, resp.StatusCode, data, ct2, cg);
    }
}
