using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WinNewsWire.Secrets;

namespace WinNewsWire.NewsBlur;

/// <summary>Port of <c>NewsBlurAPICaller</c> (core methods).</summary>
public sealed class NewsBlurAPICaller
{
    public const string SessionIDCookieKey = "newsblur_sessionid";
    private readonly Uri _baseUrl = new("https://www.newsblur.com/");
    private readonly HttpClient _http;

    public Credentials? Credentials { get; set; }

    public NewsBlurAPICaller(HttpClient? http = null)
    {
        var handler = new HttpClientHandler { UseCookies = false };
        _http = http ?? new HttpClient(handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinNewsWire/1.0");
    }

    private HttpRequestMessage Build(HttpMethod m, string path, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(m, new Uri(_baseUrl, path)) { Content = body };
        if (Credentials is { } c && c.Type == CredentialsType.NewsBlurSessionId)
            req.Headers.Add("Cookie", $"{SessionIDCookieKey}={c.Secret}");
        return req;
    }

    public async Task<string?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = password,
        });
        using var resp = await _http.SendAsync(Build(HttpMethod.Post, "api/login", form), ct);
        if (!resp.IsSuccessStatusCode) return null;
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var c in cookies)
        {
            var idx = c.IndexOf(SessionIDCookieKey + "=", StringComparison.Ordinal);
            if (idx < 0) continue;
            var start = idx + SessionIDCookieKey.Length + 1;
            var end = c.IndexOf(';', start);
            return end > 0 ? c[start..end] : c[start..];
        }
        return null;
    }

    public async Task<IReadOnlyList<NewsBlurFeed>> RetrieveFeedsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Build(HttpMethod.Get, "reader/feeds?flat=true"), ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<NewsBlurFeed>();
        using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("feeds", out var feedsObj)) return Array.Empty<NewsBlurFeed>();
        var list = new List<NewsBlurFeed>();
        foreach (var p in feedsObj.EnumerateObject())
            list.Add(p.Value.Deserialize<NewsBlurFeed>()!);
        return list;
    }

    public async Task<IReadOnlyList<NewsBlurStory>> RetrieveStoriesAsync(int page = 1, CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Build(HttpMethod.Get, $"reader/river_stories?page={page}"), ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<NewsBlurStory>();
        var body = await resp.Content.ReadFromJsonAsync<NewsBlurStoriesResponse>(cancellationToken: ct);
        return body?.Stories ?? new List<NewsBlurStory>();
    }

    public Task MarkAsReadAsync(IEnumerable<string> storyHashes, CancellationToken ct = default)
        => SendHashesAsync("reader/mark_story_hashes_as_read", storyHashes, ct);
    public Task MarkAsUnreadAsync(IEnumerable<string> storyHashes, CancellationToken ct = default)
        => SendHashesAsync("reader/mark_story_hash_as_unread", storyHashes, ct);
    public Task MarkAsStarredAsync(IEnumerable<string> storyHashes, CancellationToken ct = default)
        => SendHashesAsync("reader/mark_story_hash_as_starred", storyHashes, ct);
    public Task MarkAsUnstarredAsync(IEnumerable<string> storyHashes, CancellationToken ct = default)
        => SendHashesAsync("reader/mark_story_hash_as_unstarred", storyHashes, ct);

    private async Task SendHashesAsync(string path, IEnumerable<string> hashes, CancellationToken ct)
    {
        foreach (var chunk in hashes.Chunk(100))
        {
            var pairs = chunk.Select(h => new KeyValuePair<string, string>("story_hash", h));
            var form = new FormUrlEncodedContent(pairs);
            using var resp = await _http.SendAsync(Build(HttpMethod.Post, path, form), ct);
            resp.EnsureSuccessStatusCode();
        }
    }
}
