using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinNewsWire.Secrets;

namespace WinNewsWire.Feedly;

/// <summary>
/// Port of <c>FeedlyAPICaller</c> (Modules/Account/Sources/Account/Feedly/FeedlyAPICaller.swift).
/// Every authenticated endpoint performs a transparent "401 → refresh → retry once" fallback,
/// delegated back to <see cref="ReauthorizeAsync"/> (wired up by <see cref="FeedlyAccountDelegate"/>).
/// </summary>
public sealed class FeedlyAPICaller
{
    public string Host { get; }
    public Credentials? AccessToken { get; set; }
    public Func<CancellationToken, Task<bool>>? ReauthorizeAsync { get; set; }

    private readonly HttpClient _http;
    private bool _suspended;

    public FeedlyAPICaller(string host = "cloud.feedly.com", HttpClient? http = null)
    {
        Host = host;
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinNewsWire/1.0");
    }

    public void Suspend() => _suspended = true;
    public void Resume() => _suspended = false;

    private Uri UrlFor(string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        var b = new StringBuilder();
        b.Append("https://").Append(Host).Append(path);
        if (query is not null)
        {
            var first = true;
            foreach (var kv in query)
            {
                b.Append(first ? '?' : '&'); first = false;
                b.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
        }
        return new Uri(b.ToString());
    }

    private HttpRequestMessage BuildAuthed(HttpMethod method, Uri url, HttpContent? body = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = body };
        req.Headers.Accept.ParseAdd("application/json");
        if (AccessToken is { Secret: { Length: > 0 } tok })
            req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", tok);
        return req;
    }

    private async Task<T?> SendAsync<T>(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        if (_suspended) throw new InvalidOperationException("FeedlyAPICaller is suspended.");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = build();
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0 && ReauthorizeAsync is not null)
            {
                if (await ReauthorizeAsync(ct)) continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Feedly {(int)resp.StatusCode} {req.RequestUri}: {text}");
            }
            if (typeof(T) == typeof(Unit)) return default;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
        }
        throw new InvalidOperationException("Unreachable");
    }

    public struct Unit { }

    // --- Collections ---

    public async Task<List<FeedlyCollection>> GetCollectionsAsync(CancellationToken ct = default)
        => await SendAsync<List<FeedlyCollection>>(
            () => BuildAuthed(HttpMethod.Get, UrlFor("/v3/collections")), ct) ?? new();

    // --- Streams ---

    /// <summary>GET /v3/streams/contents.</summary>
    public async Task<FeedlyStream?> GetStreamContentsAsync(
        string streamId, string? continuation = null, DateTime? newerThan = null, bool? unreadOnly = null,
        int count = 1000, CancellationToken ct = default)
    {
        var q = BuildStreamQuery(streamId, continuation, newerThan, unreadOnly, count);
        return await SendAsync<FeedlyStream>(
            () => BuildAuthed(HttpMethod.Get, UrlFor("/v3/streams/contents", q)), ct);
    }

    /// <summary>GET /v3/streams/ids.</summary>
    public async Task<FeedlyStreamIds?> GetStreamIdsAsync(
        string streamId, string? continuation = null, DateTime? newerThan = null, bool? unreadOnly = null,
        int count = 10000, CancellationToken ct = default)
    {
        var q = BuildStreamQuery(streamId, continuation, newerThan, unreadOnly, count);
        return await SendAsync<FeedlyStreamIds>(
            () => BuildAuthed(HttpMethod.Get, UrlFor("/v3/streams/ids", q)), ct);
    }

    private static List<KeyValuePair<string, string>> BuildStreamQuery(
        string streamId, string? continuation, DateTime? newerThan, bool? unreadOnly, int count)
    {
        var q = new List<KeyValuePair<string, string>>();
        if (newerThan is DateTime d)
            q.Add(new("newerThan", ((DateTimeOffset)DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds().ToString()));
        if (unreadOnly is bool u) q.Add(new("unreadOnly", u ? "true" : "false"));
        if (!string.IsNullOrEmpty(continuation)) q.Add(new("continuation", continuation!));
        q.Add(new("count", count.ToString()));
        q.Add(new("streamId", streamId));
        return q;
    }

    // --- Entries ---

    public async Task<List<FeedlyEntry>> GetEntriesAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var arr = ids.ToArray();
        if (arr.Length == 0) return new();
        return await SendAsync<List<FeedlyEntry>>(() =>
        {
            var content = JsonContent.Create(arr);
            return BuildAuthed(HttpMethod.Post, UrlFor("/v3/entries/.mget"), content);
        }, ct) ?? new();
    }

    // --- Markers / mark as read/unread/saved/unsaved ---

    private sealed record MarkerEntriesBody(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("entryIds")] List<string> EntryIds);

    public async Task MarkAsync(IEnumerable<string> entryIds, FeedlyMarkAction action, CancellationToken ct = default)
    {
        foreach (var chunk in entryIds.Chunk(300))
        {
            var body = new MarkerEntriesBody("entries", action.ActionValue(), chunk.ToList());
            await SendAsync<Unit>(() =>
            {
                var content = JsonContent.Create(body);
                return BuildAuthed(HttpMethod.Post, UrlFor("/v3/markers"), content);
            }, ct);
        }
    }

    // --- Feed management ---

    public async Task AddFeedToCollectionAsync(string feedId, string collectionId, string? title, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string> { ["id"] = feedId };
        if (!string.IsNullOrEmpty(title)) body["title"] = title!;
        await SendAsync<Unit>(() =>
        {
            var content = JsonContent.Create(body);
            return BuildAuthed(HttpMethod.Post,
                UrlFor($"/v3/collections/{Uri.EscapeDataString(collectionId)}/feeds"), content);
        }, ct);
    }

    public async Task RemoveFeedFromCollectionAsync(string feedId, string collectionId, CancellationToken ct = default)
    {
        await SendAsync<Unit>(() => BuildAuthed(HttpMethod.Delete,
            UrlFor($"/v3/collections/{Uri.EscapeDataString(collectionId)}/feeds/{Uri.EscapeDataString(feedId)}")), ct);
    }

    // --- Search ---

    public async Task<FeedlyFeedsSearchResponse?> SearchFeedsAsync(string query, int count = 25, string locale = "en", CancellationToken ct = default)
    {
        var q = new[]
        {
            new KeyValuePair<string, string>("query", query),
            new KeyValuePair<string, string>("count", count.ToString()),
            new KeyValuePair<string, string>("locale", locale),
        };
        return await SendAsync<FeedlyFeedsSearchResponse>(
            () => BuildAuthed(HttpMethod.Get, UrlFor("/v3/search/feeds", q)), ct);
    }

    // --- Auth ---

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await SendAsync<Unit>(() => BuildAuthed(HttpMethod.Post, UrlFor("/v3/auth/logout")), ct);
    }
}
