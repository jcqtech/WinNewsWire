using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using WinNewsWire.Secrets;

namespace WinNewsWire.Feedbin;

/// <summary>Port of <c>FeedbinAPICaller</c> (core methods).</summary>
public sealed class FeedbinAPICaller
{
    private readonly HttpClient _http;
    private readonly Uri _baseUrl = new("https://api.feedbin.com/v2/");

    public Credentials? Credentials { get; set; }

    public FeedbinAPICaller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinNewsWire/1.0");
    }

    private HttpRequestMessage Build(HttpMethod m, string path, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(m, new Uri(_baseUrl, path)) { Content = body };
        if (Credentials is { } c)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.Secret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return req;
    }

    public async Task<bool> ValidateCredentialsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Build(HttpMethod.Get, "authentication.json"), ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return false;
        resp.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<IReadOnlyList<FeedbinSubscription>> RetrieveSubscriptionsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<FeedbinSubscription>>("subscriptions.json", ct) ?? new();

    public async Task<IReadOnlyList<FeedbinTag>> RetrieveTagsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<FeedbinTag>>("tags.json", ct) ?? new();

    public async Task<IReadOnlyList<FeedbinTagging>> RetrieveTaggingsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<FeedbinTagging>>("taggings.json", ct) ?? new();

    public async Task<IReadOnlyList<long>> RetrieveUnreadEntryIdsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<long>>("unread_entries.json", ct) ?? new();

    public async Task<IReadOnlyList<long>> RetrieveStarredEntryIdsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<long>>("starred_entries.json", ct) ?? new();

    public async Task<IReadOnlyList<FeedbinEntry>> RetrieveEntriesAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var list = ids.Take(100).ToList();
        if (list.Count == 0) return Array.Empty<FeedbinEntry>();
        var path = "entries.json?ids=" + string.Join(",", list);
        return await GetJsonAsync<List<FeedbinEntry>>(path, ct) ?? new();
    }

    public async Task<IReadOnlyList<FeedbinEntry>> RetrieveRecentEntriesAsync(int perPage = 100, CancellationToken ct = default)
        => await GetJsonAsync<List<FeedbinEntry>>($"entries.json?per_page={perPage}", ct) ?? new();

    public Task MarkAsReadAsync(IEnumerable<long> ids, CancellationToken ct = default) => PostIdsAsync("unread_entries/delete.json", ids, "unread_entries", ct);
    public Task MarkAsUnreadAsync(IEnumerable<long> ids, CancellationToken ct = default) => PostIdsAsync("unread_entries.json", ids, "unread_entries", ct);
    public Task MarkAsStarredAsync(IEnumerable<long> ids, CancellationToken ct = default) => PostIdsAsync("starred_entries.json", ids, "starred_entries", ct);
    public Task MarkAsUnstarredAsync(IEnumerable<long> ids, CancellationToken ct = default) => PostIdsAsync("starred_entries/delete.json", ids, "starred_entries", ct);

    private async Task PostIdsAsync(string path, IEnumerable<long> ids, string key, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(1000))
        {
            var dict = new Dictionary<string, long[]> { [key] = chunk };
            var content = JsonContent.Create(dict);
            using var resp = await _http.SendAsync(Build(HttpMethod.Post, path, content), ct);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<FeedbinSubscription?> CreateSubscriptionAsync(string feedUrl, CancellationToken ct = default)
    {
        var body = JsonContent.Create(new { feed_url = feedUrl });
        using var resp = await _http.SendAsync(Build(HttpMethod.Post, "subscriptions.json", body), ct);
        if ((int)resp.StatusCode == 302) return null;
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<FeedbinSubscription>(cancellationToken: ct);
    }

    public async Task DeleteSubscriptionAsync(long subscriptionID, CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Build(HttpMethod.Delete, $"subscriptions/{subscriptionID}.json"), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task RenameSubscriptionAsync(long subscriptionID, string newTitle, CancellationToken ct = default)
    {
        var body = JsonContent.Create(new { title = newTitle });
        using var resp = await _http.SendAsync(Build(new HttpMethod("PATCH"), $"subscriptions/{subscriptionID}.json", body), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CreateTaggingAsync(long feedID, string tagName, CancellationToken ct = default)
    {
        var body = JsonContent.Create(new { feed_id = feedID, name = tagName });
        using var resp = await _http.SendAsync(Build(HttpMethod.Post, "taggings.json", body), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteTaggingAsync(long taggingID, CancellationToken ct = default)
    {
        using var resp = await _http.SendAsync(Build(HttpMethod.Delete, $"taggings/{taggingID}.json"), ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(Build(HttpMethod.Get, path), ct);
        if (!resp.IsSuccessStatusCode) return default;
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }
}
