using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using WinNewsWire.Secrets;

namespace WinNewsWire.ReaderAPI;

/// <summary>Port of <c>ReaderAPICaller</c> (core methods). Uses the Google Reader–compatible
/// API exposed by FreshRSS / Inoreader / BazQux / The Old Reader.</summary>
public sealed partial class ReaderAPICaller : IDisposable
{
    public ReaderAPIVariant Variant { get; }
    public string Host { get; set; }
    public Credentials? Credentials { get; set; }
    private string? _authToken;
    private string? _editToken;
    public string? AuthToken
    {
        get => _authToken;
        set { _authToken = value; _editToken = null; }
    }
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private int _disposed;

    public ReaderAPICaller(ReaderAPIVariant variant, string? host = null, HttpClient? http = null, bool disposeHttpClient = false)
    {
        _ownsHttp = http is null || disposeHttpClient;
        Variant = variant;
        Host = string.IsNullOrEmpty(host) ? variant.DefaultHost() : host!;
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinNewsWire/1.0");
    }

    private Uri Url(string path) => new(new Uri(Host.TrimEnd('/') + "/"), path.TrimStart('/'));

    private HttpRequestMessage Auth(HttpRequestMessage req)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!string.IsNullOrEmpty(AuthToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("GoogleLogin", $"auth={AuthToken}");
        if (Variant == ReaderAPIVariant.Inoreader)
        {
            if (!string.IsNullOrEmpty(ApplicationID)) req.Headers.TryAddWithoutValidation("AppId", ApplicationID);
            if (!string.IsNullOrEmpty(ApplicationKey)) req.Headers.TryAddWithoutValidation("AppKey", ApplicationKey);
        }
        return req;
    }

    public async Task EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!string.IsNullOrEmpty(AuthToken)) return;
        await _authenticationGate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrEmpty(AuthToken)) return;
            var credentials = Credentials
                ?? throw new CredentialsException("Sign in to this account before syncing subscriptions.");
            if (await LoginAsync(credentials.Username, credentials.Secret, ct) is null)
                throw new CredentialsException("The account could not sign in. Check its credentials.");
        }
        finally { _authenticationGate.Release(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Credentials = null;
        AuthToken = null;
        AccountSettings = null;
        if (_ownsHttp) _http.Dispose();
    }

    public async Task ImportOpmlAsync(byte[] data, CancellationToken ct = default)
    {
        await EnsureAuthenticatedAsync(ct);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Url("reader/api/0/subscription/import"));
            request.Content = new ByteArrayContent(data);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
            return request;
        }, ct);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new HttpRequestException("The service returned an unexpected OPML import response.", null, response.StatusCode);
    }

    /// <summary>ClientLogin to get an Auth token.</summary>
    public async Task<string?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = username, ["Passwd"] = password, ["client"] = "WinNewsWire",
            ["accountType"] = "HOSTED_OR_GOOGLE", ["service"] = "reader",
        });
        RateLimiter.ThrowIfLimited();
        using var request = Auth(new HttpRequestMessage(HttpMethod.Post, Url("accounts/ClientLogin")) { Content = form });
        request.Headers.Authorization = null;
        using var resp = await _http.SendAsync(request, ct);
        RateLimiter.CheckResponse(resp);
        NoteUsageLimits(resp);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) return null;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        foreach (var line in body.Split('\n'))
            if (line.StartsWith("Auth=", StringComparison.Ordinal)) { AuthToken = line[5..].Trim(); return AuthToken; }
        return null;
    }

    public Task<ReaderAPISubscriptionsResponse?> RetrieveSubscriptionsAsync(CancellationToken ct = default, bool forceRefresh = false)
        => GetConditionalAsync<ReaderAPISubscriptionsResponse>("reader/api/0/subscription/list?output=json", "subscriptions", forceRefresh, ct);

    public Task<ReaderAPITagsResponse?> RetrieveTagsAsync(CancellationToken ct = default)
        => GetConditionalAsync<ReaderAPITagsResponse>("reader/api/0/tag/list?output=json"
            + (Variant == ReaderAPIVariant.Inoreader ? "&types=1" : ""), "tags", false, ct);

    public async Task<ReaderAPIStreamContentsResponse?> RetrieveStreamContentsAsync(
        string streamId, int count = 100, string? continuation = null, CancellationToken ct = default)
    {
        var qs = $"reader/api/0/stream/contents/{Uri.EscapeDataString(streamId)}?output=json&n={count}";
        if (continuation is not null) qs += "&c=" + Uri.EscapeDataString(continuation);
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(qs)), ct);
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

    public async Task<ReaderAPISubscription> CreateSubscriptionAsync(string url, CancellationToken ct = default)
    {
        using var response = await SendMutationAsync("subscription/quickadd", [new("quickadd", url)], ct);
        var result = await response.Content.ReadFromJsonAsync<ReaderAPIQuickAddResult>(cancellationToken: ct)
            ?? throw new InvalidDataException("The service returned no subscription result.");
        if (result.NumResults == 0 || string.IsNullOrEmpty(result.StreamId))
            throw new FeedFinder.FeedNotFoundException();
        var subscriptions = await RetrieveSubscriptionsAsync(ct, forceRefresh: true);
        return subscriptions?.Subscriptions.FirstOrDefault(s => s.Id == result.StreamId)
            ?? throw new InvalidDataException("The created subscription was not returned by the service.");
    }

    public async Task ChangeSubscriptionAsync(string id, string? title = null, string? addTag = null,
        string? removeTag = null, bool unsubscribe = false, CancellationToken ct = default)
    {
        var values = new List<KeyValuePair<string, string>> { new("s", id), new("ac", unsubscribe ? "unsubscribe" : "edit") };
        if (title is not null) values.Add(new("t", title));
        if (addTag is not null) values.Add(new("a", "user/-/label/" + addTag));
        if (removeTag is not null) values.Add(new("r", "user/-/label/" + removeTag));
        using var response = await SendMutationAsync("subscription/edit", values, ct);
    }

    public async Task RenameTagAsync(string oldName, string newName, CancellationToken ct = default)
    {
        using var response = await SendMutationAsync("rename-tag",
            [new("s", "user/-/label/" + oldName), new("dest", "user/-/label/" + newName)], ct);
    }

    public async Task DeleteTagAsync(string id, CancellationToken ct = default)
    {
        using var response = await SendMutationAsync("disable-tag", [new("s", id)], ct);
    }

    private async Task<HttpResponseMessage> SendMutationAsync(string endpoint, IEnumerable<KeyValuePair<string, string>> values, CancellationToken ct)
    {
        var parameters = values.ToList();
        for (int attempt = 0; ; attempt++)
        {
            var token = await GetEditTokenAsync(ct);
            try
            {
                return await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Url("reader/api/0/" + endpoint))
                {
                    Content = new FormUrlEncodedContent(parameters.Append(new("T", token))),
                }, ct);
            }
            catch (HttpRequestException ex) when (attempt == 0 && ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                _editToken = null;
            }
        }
    }

    private async Task EditTagAsync(IEnumerable<string> itemIds, string? addTag, string? removeTag, CancellationToken ct)
    {
        foreach (var chunk in itemIds.Chunk(1000))
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var id in chunk) pairs.Add(new("i", ItemID(id, Variant)));
            if (addTag is not null) pairs.Add(new("a", addTag));
            if (removeTag is not null) pairs.Add(new("r", removeTag));
            using var resp = await SendMutationAsync("edit-tag", pairs, ct);
        }
    }

    private async Task<string> GetEditTokenAsync(CancellationToken ct)
    {
        await _writeTokenGate.WaitAsync(ct);
        try
        {
            if (_editToken is not null) return _editToken;
            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url("reader/api/0/token")), ct);
            var token = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            return _editToken = token.Length > 0 ? token : throw new InvalidDataException("The service did not return an edit token.");
        }
        finally { _writeTokenGate.Release(); }
    }

    internal static string ItemID(string articleID, ReaderAPIVariant variant)
    {
        const string prefix = "tag:google.com,2005:reader/item/";
        if (articleID.StartsWith(prefix, StringComparison.Ordinal))
        {
            var part = articleID[prefix.Length..];
            if (variant == ReaderAPIVariant.TheOldReader ||
                ulong.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                return articleID;
            throw new FormatException("The stored Reader API article ID is invalid.");
        }
        if (variant == ReaderAPIVariant.TheOldReader) return prefix + articleID;
        if (!long.TryParse(articleID, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new FormatException("The stored Reader API article ID is not a signed 64-bit value.");
        return prefix + unchecked((ulong)value).ToString("x16", CultureInfo.InvariantCulture);
    }

    internal static bool CanEncodeItemID(string id, ReaderAPIVariant variant)
    {
        if (variant == ReaderAPIVariant.TheOldReader) return id.Length > 0;
        const string prefix = "tag:google.com,2005:reader/item/";
        return id.StartsWith(prefix, StringComparison.Ordinal)
            ? ulong.TryParse(id[prefix.Length..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)
            : long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
