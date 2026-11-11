using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WinNewsWire.Secrets;

namespace WinNewsWire.NewsBlur;

/// <summary>Port of <c>NewsBlurAPICaller</c> (core methods).</summary>
public sealed class NewsBlurAPICaller : IDisposable
{
    public const string SessionIDCookieKey = "newsblur_sessionid";
    private readonly Uri _baseUrl = new("https://www.newsblur.com/");
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private int _disposed;

    public Credentials? Credentials { get; set; }

    public NewsBlurAPICaller(HttpClient? http = null, bool disposeHttpClient = false)
    {
        _ownsHttp = http is null || disposeHttpClient;
        _http = http ?? new HttpClient(new HttpClientHandler { UseCookies = false });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinNewsWire/1.0");
    }

    private HttpRequestMessage Build(HttpMethod m, string path, HttpContent? body = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var req = new HttpRequestMessage(m, new Uri(_baseUrl, path)) { Content = body };
        if (Credentials is { } c && c.Type == CredentialsType.NewsBlurSessionId)
            req.Headers.Add("Cookie", $"{SessionIDCookieKey}={c.Secret}");
        return req;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Credentials = null;
        if (_ownsHttp) _http.Dispose();
    }

    public async Task<string?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = password,
        });
        using var request = Build(HttpMethod.Post, "api/login", form);
        using var resp = await _http.SendAsync(request, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (body.Length > 0)
        {
            using var document = JsonDocument.Parse(body);
            EnsureSuccessfulResult(document.RootElement);
        }
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
        => (await RetrieveFeedStructureAsync(ct)).Feeds;

    public Task<NewsBlurFeedsResponse> RetrieveFeedStructureAsync(CancellationToken ct = default)
        => GetAsync<NewsBlurFeedsResponse>("reader/feeds?flat=true&update_counts=true", ct);

    private async Task<T> GetAsync<T>(string endpoint, CancellationToken ct)
    {
        using var request = Build(HttpMethod.Get, endpoint);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        EnsureSuccessfulResult(document.RootElement);
        return document.RootElement.Deserialize<T>() ?? throw new InvalidDataException("NewsBlur returned an empty response.");
    }

    public async Task<IReadOnlyList<NewsBlurStory>> RetrieveStoriesAsync(int page = 1, CancellationToken ct = default)
    {
        var body = await GetAsync<NewsBlurStoriesResponse>($"reader/river_stories?page={page}&include_story_content=true", ct);
        return body.Stories ?? throw new InvalidDataException("NewsBlur returned no stories collection.");
    }

    public async Task<IReadOnlyList<NewsBlurStory>> RetrieveStoriesByHashesAsync(IEnumerable<string> hashes, CancellationToken ct = default)
    {
        var result = new List<NewsBlurStory>();
        foreach (var batch in hashes.Distinct(StringComparer.Ordinal).Chunk(100))
        {
            var query = string.Join("&", batch.Select(hash => "h=" + Uri.EscapeDataString(hash)));
            var page = await GetAsync<NewsBlurStoriesResponse>("reader/river_stories?include_story_content=true&" + query, ct);
            result.AddRange(page.Stories ?? throw new InvalidDataException("NewsBlur returned no stories collection."));
        }
        return result;
    }

    public async Task<IReadOnlyList<NewsBlurStory>> RetrieveFeedStoriesAsync(string feedID, int page, CancellationToken ct = default)
    {
        var result = await GetAsync<NewsBlurStoriesResponse>($"reader/feed/{Uri.EscapeDataString(feedID)}?page={page}&order=newest&read_filter=all&include_hidden=false&include_story_content=true", ct);
        return result.Stories ?? throw new InvalidDataException("NewsBlur returned no stories collection.");
    }

    public async Task<IReadOnlyList<NewsBlurStoryHash>> RetrieveUnreadStoryHashesAsync(CancellationToken ct = default)
        => (await GetAsync<NewsBlurStoryHashesResponse>("reader/unread_story_hashes?include_timestamps=true", ct)).Unread
            ?? throw new InvalidDataException("NewsBlur returned no unread story hashes.");

    public async Task<IReadOnlyList<NewsBlurStoryHash>> RetrieveStarredStoryHashesAsync(CancellationToken ct = default)
        => (await GetAsync<NewsBlurStoryHashesResponse>("reader/starred_story_hashes?include_timestamps=true", ct)).Starred
            ?? throw new InvalidDataException("NewsBlur returned no starred story hashes.");

    public Task MarkAsReadAsync(IEnumerable<string> storyHashes, CancellationToken ct = default)
        => SendHashesAsync("reader/mark_story_hashes_as_read", storyHashes, ct);
    public Task MarkAsUnreadAsync(IEnumerable<string> storyHashes, CancellationToken ct = default)
        => SendHashesAsync("reader/mark_story_hash_as_unread", storyHashes, ct);
    public Task MarkAsStarredAsync(IEnumerable<string> storyHashes, CancellationToken ct = default)
        => SendHashesAsync("reader/mark_story_hash_as_starred", storyHashes, ct);
    public Task MarkAsUnstarredAsync(IEnumerable<string> storyHashes, CancellationToken ct = default)
        => SendHashesAsync("reader/mark_story_hash_as_unstarred", storyHashes, ct);

    public Task AddFolderAsync(string name, CancellationToken ct = default)
        => SendFormAsync("reader/add_folder", [new("folder", name), new("parent_folder", "")], ct);

    public Task RenameFolderAsync(string from, string to, CancellationToken ct = default)
        => SendFormAsync("reader/rename_folder", [new("folder_to_rename", from), new("new_folder_name", to), new("in_folder", "")], ct);

    public Task RemoveFolderAsync(string name, IEnumerable<string> feedIDs, CancellationToken ct = default)
        => SendFormAsync("reader/delete_folder",
            new[] { new KeyValuePair<string, string>("folder_to_delete", name), new("in_folder", "") }
                .Concat(feedIDs.Select(id => new KeyValuePair<string, string>("feed_id", id))), ct);

    public async Task<NewsBlurFeed> AddUrlAsync(string url, string? folder, CancellationToken ct = default)
    {
        var values = new List<KeyValuePair<string, string>> { new("url", url) };
        if (folder is not null) values.Add(new("folder", folder));
        using var request = Build(HttpMethod.Post, "reader/add_url", new FormUrlEncodedContent(values));
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        EnsureSuccessfulResult(document.RootElement);
        if (document.RootElement.TryGetProperty("feed", out var value) && value.Deserialize<NewsBlurFeed>() is { } feed)
            return feed;
        throw new InvalidDataException("NewsBlur did not return the added feed.");
    }

    public Task RenameFeedAsync(string id, string name, CancellationToken ct = default)
        => SendFormAsync("reader/rename_feed", [new("feed_id", id), new("feed_title", name)], ct);

    public Task DeleteFeedAsync(string id, string? folder, CancellationToken ct = default)
    {
        var values = new List<KeyValuePair<string, string>> { new("feed_id", id) };
        if (folder is not null) values.Add(new("in_folder", folder));
        return SendFormAsync("reader/delete_feed", values, ct);
    }

    public Task MoveFeedAsync(string id, string? from, string? to, CancellationToken ct = default)
        => SendFormAsync("reader/move_feed_to_folder",
            [new("feed_id", id), new("in_folder", from ?? ""), new("to_folder", to ?? "")], ct);

    private async Task SendFormAsync(string endpoint, IEnumerable<KeyValuePair<string, string>> values, CancellationToken ct)
    {
        using var request = Build(HttpMethod.Post, endpoint, new FormUrlEncodedContent(values));
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 0)
        {
            using var document = JsonDocument.Parse(body);
            EnsureSuccessfulResult(document.RootElement);
        }
    }

    private static void EnsureSuccessfulResult(JsonElement result)
    {
        if (result.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) && value < 0)
            throw new InvalidOperationException(result.TryGetProperty("message", out var message)
                ? message.GetString() : "NewsBlur rejected the change.");
    }

    private async Task SendHashesAsync(string path, IEnumerable<string> hashes, CancellationToken ct)
    {
        foreach (var chunk in hashes.Chunk(100))
        {
            var pairs = chunk.Select(h => new KeyValuePair<string, string>("story_hash", h));
            await SendFormAsync(path, pairs, ct);
        }
    }
}
