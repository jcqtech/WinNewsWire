using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text;
using WinNewsWire.Secrets;
using WinNewsWire.Web;

namespace WinNewsWire.Feedbin;

/// <summary>Port of <c>FeedbinAPICaller</c> (core methods).</summary>
public sealed class FeedbinAPICaller : IDisposable
{
    public static class ConditionalGetKeys
    {
        public const string Subscriptions = "subscriptions";
        public const string Tags = "tags";
        public const string Taggings = "taggings";
        public const string UnreadEntries = "unreadEntries";
        public const string StarredEntries = "starredEntries";
    }

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private int _disposed;
    private readonly Uri _baseUrl = new("https://api.feedbin.com/v2/");

    public Credentials? Credentials { get; set; }
    public Account.AccountSettings? AccountSettings { get; set; }

    public FeedbinAPICaller(HttpClient? http = null, bool disposeHttpClient = false)
    {
        _ownsHttp = http is null || disposeHttpClient;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinNewsWire/1.0");
    }

    private HttpRequestMessage Build(HttpMethod m, string path, HttpContent? body = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var req = new HttpRequestMessage(m, new Uri(_baseUrl, path)) { Content = body };
        if (Credentials is { } c)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.Secret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return req;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Credentials = null;
        AccountSettings = null;
        if (_ownsHttp) _http.Dispose();
    }

    public async Task<bool> ValidateCredentialsAsync(CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Get, "authentication.json");
        using var resp = await _http.SendAsync(request, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return false;
        resp.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<FeedbinImportResult> ImportOpmlAsync(byte[] data, CancellationToken ct = default)
    {
        var body = new ByteArrayContent(data);
        body.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        using var request = Build(HttpMethod.Post, "imports.json", body);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FeedbinImportResult>(cancellationToken: ct)
            ?? throw new InvalidDataException("Feedbin returned an empty OPML import result.");
    }

    public async Task<FeedbinImportResult> RetrieveOpmlImportResultAsync(int importID, CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Get, $"imports/{importID}.json");
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FeedbinImportResult>(cancellationToken: ct)
            ?? throw new InvalidDataException("Feedbin returned an empty OPML import status.");
    }

    public Task<FeedbinConditionalResponse<FeedbinSubscription>> RetrieveSubscriptionsAsync(CancellationToken ct = default, bool forceRefresh = false)
        => GetConditionalArrayAsync<FeedbinSubscription>("subscriptions.json?mode=extended", ConditionalGetKeys.Subscriptions, ct, forceRefresh);

    public Task<FeedbinConditionalResponse<FeedbinTag>> RetrieveTagsAsync(CancellationToken ct = default)
        => GetConditionalArrayAsync<FeedbinTag>("tags.json", ConditionalGetKeys.Tags, ct);

    public Task<FeedbinConditionalResponse<FeedbinTagging>> RetrieveTaggingsAsync(CancellationToken ct = default, bool forceRefresh = false)
        => GetConditionalArrayAsync<FeedbinTagging>("taggings.json", ConditionalGetKeys.Taggings, ct, forceRefresh);

    public Task<FeedbinConditionalResponse<long>> RetrieveUnreadEntryIdsAsync(CancellationToken ct = default)
        => GetConditionalArrayAsync<long>("unread_entries.json", ConditionalGetKeys.UnreadEntries, ct);

    public Task<FeedbinConditionalResponse<long>> RetrieveStarredEntryIdsAsync(CancellationToken ct = default)
        => GetConditionalArrayAsync<long>("starred_entries.json", ConditionalGetKeys.StarredEntries, ct);

    internal void StoreConditionalGetIfNeeded<T>(string key, FeedbinConditionalResponse<T> response)
    {
        if (response.Items is not null) AccountSettings?.SetConditionalGetInfo(key, response.ConditionalGetInfo);
    }

    public async Task<IReadOnlyList<FeedbinEntry>> RetrieveEntriesAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var entries = new List<FeedbinEntry>();
        foreach (var chunk in ids.Distinct().Chunk(100))
            entries.AddRange(await GetJsonAsync<List<FeedbinEntry>>("entries.json?ids=" + string.Join(",", chunk), ct));
        return entries;
    }

    public async Task<IReadOnlyList<FeedbinEntry>> RetrieveRecentEntriesAsync(int perPage = 100, CancellationToken ct = default)
    {
        var entries = new List<FeedbinEntry>();
        await foreach (var page in RetrieveEntryPagesAsync(DateTime.UtcNow.AddMonths(-3), perPage, ct))
            entries.AddRange(page.Entries);
        return entries;
    }

    public async IAsyncEnumerable<FeedbinEntryPage> RetrieveEntryPagesAsync(DateTime since, int perPage = 100,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var timestamp = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        var path = $"entries.json?since={Uri.EscapeDataString(timestamp)}&per_page={perPage}&mode=extended";
        await foreach (var page in GetPagesAsync<FeedbinEntry>(path, ct))
            yield return new(page.Items ?? throw new InvalidDataException("Feedbin returned no entry page."), page.ServerDate);
    }

    public Task MarkAsReadAsync(IEnumerable<long> ids, CancellationToken ct = default) => SendIdsAsync(HttpMethod.Delete, "unread_entries.json", ids, "unread_entries", ct);
    public Task MarkAsUnreadAsync(IEnumerable<long> ids, CancellationToken ct = default) => SendIdsAsync(HttpMethod.Post, "unread_entries.json", ids, "unread_entries", ct);
    public Task MarkAsStarredAsync(IEnumerable<long> ids, CancellationToken ct = default) => SendIdsAsync(HttpMethod.Post, "starred_entries.json", ids, "starred_entries", ct);
    public Task MarkAsUnstarredAsync(IEnumerable<long> ids, CancellationToken ct = default) => SendIdsAsync(HttpMethod.Delete, "starred_entries.json", ids, "starred_entries", ct);

    private async Task SendIdsAsync(HttpMethod method, string path, IEnumerable<long> ids, string key, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(1000))
        {
            var dict = new Dictionary<string, long[]> { [key] = chunk };
            var content = JsonContent.Create(dict);
            using var request = Build(method, path, content);
            using var resp = await _http.SendAsync(request, ct);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<FeedbinCreateSubscriptionResult> CreateSubscriptionAsync(string feedUrl, CancellationToken ct = default)
    {
        var body = JsonContent.Create(new { feed_url = feedUrl });
        using var request = Build(HttpMethod.Post, "subscriptions.json?mode=extended", body);
        using var response = await _http.SendAsync(request, ct);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Created:
                return new FeedbinCreateSubscriptionResult.Created(await response.Content.ReadFromJsonAsync<FeedbinSubscription>(cancellationToken: ct)
                    ?? throw new InvalidDataException("Feedbin returned no subscription."));
            case HttpStatusCode.MultipleChoices:
                return new FeedbinCreateSubscriptionResult.MultipleChoice(await response.Content.ReadFromJsonAsync<List<FeedbinSubscriptionChoice>>(cancellationToken: ct)
                    ?? throw new InvalidDataException("Feedbin returned no feed choices."));
            case HttpStatusCode.Found:
            case HttpStatusCode.Unauthorized:
                return new FeedbinCreateSubscriptionResult.AlreadySubscribed();
            case HttpStatusCode.NotFound:
                return new FeedbinCreateSubscriptionResult.NotFound();
            default:
                response.EnsureSuccessStatusCode();
                throw new InvalidDataException("Feedbin returned an unexpected subscription response.");
        }
    }

    public async Task DeleteSubscriptionAsync(long subscriptionID, CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Delete, $"subscriptions/{subscriptionID}.json");
        using var resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task RenameSubscriptionAsync(long subscriptionID, string newTitle, CancellationToken ct = default)
    {
        var body = JsonContent.Create(new { title = newTitle });
        using var request = Build(HttpMethod.Post, $"subscriptions/{subscriptionID}/update.json", body);
        using var resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task RenameTagAsync(string oldName, string newName, CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Post, "tags.json", JsonContent.Create(new { old_name = oldName, new_name = newName }));
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<long> CreateTaggingAsync(long feedID, string tagName, CancellationToken ct = default)
    {
        var body = JsonContent.Create(new { feed_id = feedID, name = tagName });
        using var request = Build(HttpMethod.Post, "taggings.json", body);
        using var resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Headers.Location is { } location
            && long.TryParse(Path.GetFileNameWithoutExtension(new Uri(_baseUrl, location).AbsolutePath), out var id))
            return id;
        throw new InvalidDataException("Feedbin did not return the new tagging ID.");
    }

    public async Task DeleteTaggingAsync(long taggingID, CancellationToken ct = default)
    {
        using var request = Build(HttpMethod.Delete, $"taggings/{taggingID}.json");
        using var resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var request = Build(HttpMethod.Get, path);
        using var resp = await _http.SendAsync(request, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidDataException("Feedbin returned an empty response.");
    }

    private async Task<FeedbinConditionalResponse<T>> GetConditionalArrayAsync<T>(string path, string key, CancellationToken ct, bool forceRefresh = false)
    {
        var result = new List<T>();
        ConditionalGetInfo? info = null;
        int pages = 0;
        await foreach (var page in GetPagesAsync<T>(path, ct, forceRefresh ? null : AccountSettings?.ConditionalGetInfo(key)))
        {
            if (page.Items is null) return new(null, null);
            result.AddRange(page.Items);
            // A validator for page one cannot validate subsequent pages.
            info = ++pages == 1 ? page.ConditionalGetInfo : null;
        }
        return new(result, info);
    }

    private async IAsyncEnumerable<(IReadOnlyList<T>? Items, DateTime? ServerDate, ConditionalGetInfo? ConditionalGetInfo)> GetPagesAsync<T>(
        string path, [EnumeratorCancellation] CancellationToken ct, ConditionalGetInfo? conditional = null)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = path;
        while (next is not null)
        {
            var uri = new Uri(_baseUrl, next);
            if (uri.Scheme != _baseUrl.Scheme || uri.Authority != _baseUrl.Authority || !visited.Add(uri.AbsoluteUri))
                throw new InvalidDataException("Feedbin returned an invalid or repeated pagination link.");
            using var request = Build(HttpMethod.Get, uri.AbsoluteUri);
            if (visited.Count == 1) conditional?.ApplyTo(request);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (visited.Count != 1 || !request.Headers.Contains("If-None-Match") && !request.Headers.Contains("If-Modified-Since"))
                    throw new InvalidDataException("Feedbin returned 304 without a conditional snapshot request.");
                yield return (null, response.Headers.Date?.UtcDateTime, null);
                yield break;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("Feedbin returned an unexpected list response.");
            var items = await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken: ct)
                ?? throw new InvalidDataException("Feedbin returned an empty list response.");
            yield return (items, response.Headers.Date?.UtcDateTime, ConditionalGetInfo.FromResponse(response.Headers, response.Content.Headers));
            next = HttpLinkPagingInfo.FromResponse(response).NextPage;
        }
    }
}
