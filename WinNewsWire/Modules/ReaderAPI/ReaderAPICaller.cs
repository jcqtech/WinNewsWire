using System.Net.Http.Headers;
using System.Net.Http.Json;
using WinNewsWire.Secrets;

namespace WinNewsWire.ReaderAPI;

/// <summary>Port of <c>ReaderAPICaller</c> (core methods). Uses the Google Reader–compatible
/// API exposed by FreshRSS / Inoreader / BazQux / The Old Reader.</summary>
public sealed class ReaderAPICaller
{
    public ReaderAPIVariant Variant { get; }
    public string Host { get; set; }
    public Credentials? Credentials { get; set; }
    public string? AuthToken { get; set; }
    private readonly HttpClient _http;

    public ReaderAPICaller(ReaderAPIVariant variant, string? host = null, HttpClient? http = null)
    {
        Variant = variant;
        Host = string.IsNullOrEmpty(host) ? variant.DefaultHost() : host!;
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinNewsWire/1.0");
    }

    private Uri Url(string path) => new(new Uri(Host.TrimEnd('/') + "/"), path.TrimStart('/'));

    private HttpRequestMessage Auth(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(AuthToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("GoogleLogin", $"auth={AuthToken}");
        return req;
    }

    /// <summary>ClientLogin to get an Auth token.</summary>
    public async Task<string?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = username, ["Passwd"] = password, ["client"] = "WinNewsWire",
            ["accountType"] = "HOSTED_OR_GOOGLE", ["service"] = "reader",
        });
        using var resp = await _http.PostAsync(Url("accounts/ClientLogin"), form, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("Auth=", StringComparison.Ordinal)) { AuthToken = line[5..].Trim(); return AuthToken; }
        return null;
    }

    public async Task<ReaderAPISubscriptionsResponse?> RetrieveSubscriptionsAsync(CancellationToken ct = default)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Get, Url("reader/api/0/subscription/list?output=json")));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReaderAPISubscriptionsResponse>(cancellationToken: ct);
    }

    public async Task<ReaderAPITagsResponse?> RetrieveTagsAsync(CancellationToken ct = default)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Get, Url("reader/api/0/tag/list?output=json")));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReaderAPITagsResponse>(cancellationToken: ct);
    }

    public async Task<ReaderAPIStreamContentsResponse?> RetrieveStreamContentsAsync(
        string streamId, int count = 100, string? continuation = null, CancellationToken ct = default)
    {
        var qs = $"reader/api/0/stream/contents/{Uri.EscapeDataString(streamId)}?output=json&n={count}";
        if (continuation is not null) qs += "&c=" + Uri.EscapeDataString(continuation);
        using var req = Auth(new HttpRequestMessage(HttpMethod.Get, Url(qs)));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReaderAPIStreamContentsResponse>(cancellationToken: ct);
    }

    public Task MarkAsReadAsync(IEnumerable<string> itemIds, CancellationToken ct = default)
        => EditTagAsync(itemIds, addTag: "user/-/state/com.google/read", removeTag: null, ct);
    public Task MarkAsUnreadAsync(IEnumerable<string> itemIds, CancellationToken ct = default)
        => EditTagAsync(itemIds, addTag: null, removeTag: "user/-/state/com.google/read", ct);
    public Task MarkAsStarredAsync(IEnumerable<string> itemIds, CancellationToken ct = default)
        => EditTagAsync(itemIds, addTag: "user/-/state/com.google/starred", removeTag: null, ct);
    public Task MarkAsUnstarredAsync(IEnumerable<string> itemIds, CancellationToken ct = default)
        => EditTagAsync(itemIds, addTag: null, removeTag: "user/-/state/com.google/starred", ct);

    private async Task EditTagAsync(IEnumerable<string> itemIds, string? addTag, string? removeTag, CancellationToken ct)
    {
        var token = await GetEditTokenAsync(ct);
        foreach (var chunk in itemIds.Chunk(250))
        {
            var pairs = new List<KeyValuePair<string, string>> { new("T", token ?? "") };
            foreach (var id in chunk) pairs.Add(new("i", id));
            if (addTag is not null) pairs.Add(new("a", addTag));
            if (removeTag is not null) pairs.Add(new("r", removeTag));
            using var req = Auth(new HttpRequestMessage(HttpMethod.Post, Url("reader/api/0/edit-tag"))
            { Content = new FormUrlEncodedContent(pairs) });
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }
    }

    private async Task<string?> GetEditTokenAsync(CancellationToken ct)
    {
        using var req = Auth(new HttpRequestMessage(HttpMethod.Get, Url("reader/api/0/token")));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return (await resp.Content.ReadAsStringAsync(ct)).Trim();
    }
}
