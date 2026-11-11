using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using WinNewsWire.Account;
using WinNewsWire.Secrets;
using WinNewsWire.Web;

namespace WinNewsWire.ReaderAPI;

public sealed partial class ReaderAPICaller
{
    private readonly SemaphoreSlim _authenticationGate = new(1, 1);
    private readonly SemaphoreSlim _writeTokenGate = new(1, 1);
    public AccountSettings? AccountSettings { get; set; }
    public SyncRateLimiter RateLimiter { get; } = new("Reader API", treatsForbiddenAsRateLimited: false);
    public ReaderAPIUsageLimits? UsageLimits { get; private set; }
    public string? ApplicationID { get; set; }
    public string? ApplicationKey { get; set; }
    private readonly Dictionary<string, ConditionalGetInfo?> _pendingConditionalGets = new(StringComparer.Ordinal);

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);
        for (int attempt = 0; ; attempt++)
        {
            RateLimiter.ThrowIfLimited();
            var token = AuthToken;
            using var request = Auth(build());
            var response = await _http.SendAsync(request, ct);
            try
            {
                RateLimiter.CheckResponse(response);
                NoteUsageLimits(response);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0 && Credentials is { } credentials)
                {
                    response.Dispose();
                    await _authenticationGate.WaitAsync(ct);
                    try
                    {
                        if (AuthToken == token)
                        {
                            AuthToken = null;
                            if (await LoginAsync(credentials.Username, credentials.Secret, ct) is null)
                                throw new CredentialsException("The account session expired. Sign in again.");
                        }
                    }
                    finally { _authenticationGate.Release(); }
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.NotModified) response.EnsureSuccessStatusCode();
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private async Task<T?> GetConditionalAsync<T>(string path, string key, bool forceRefresh, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
            if (!forceRefresh) AccountSettings?.ConditionalGetInfo(key)?.ApplyTo(request);
            return request;
        }, ct);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidDataException("Reader API returned an empty response.");
        if (result is ReaderAPITagsResponse { Tags: null } or ReaderAPISubscriptionsResponse { Subscriptions: null })
            throw new InvalidDataException("Reader API returned incomplete subscription data.");
        if (!forceRefresh) _pendingConditionalGets[key] = ConditionalGetInfo.FromResponse(response.Headers, response.Content.Headers);
        return result;
    }

    internal void CommitConditionalGetInfo()
    {
        foreach (var (key, info) in _pendingConditionalGets) AccountSettings?.SetConditionalGetInfo(key, info);
        _pendingConditionalGets.Clear();
    }

    public async Task<ReaderAPIItemIDs> RetrieveItemIDsAsync(string stream, string? excludedTag = null, DateTime? since = null, CancellationToken ct = default)
    {
        var query = "reader/api/0/stream/items/ids?n=1000&output=json&s=" + Uri.EscapeDataString(stream);
        if (excludedTag is not null) query += "&xt=" + Uri.EscapeDataString(excludedTag);
        if (since is { } date) query += "&ot=" + new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? continuation = null;
        DateTime? serverDate = null;
        do
        {
            var path = query + (continuation is null ? "" : "&c=" + Uri.EscapeDataString(continuation));
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), ct);
            serverDate ??= response.Headers.Date?.UtcDateTime;
            var page = await response.Content.ReadFromJsonAsync<ReaderAPIReferenceWrapper>(cancellationToken: ct)
                ?? throw new InvalidDataException("Reader API returned no article ID response.");
            if (page.ItemRefs is null) throw new InvalidDataException("Reader API returned no article ID collection.");
            ids.UnionWith(page.ItemRefs.Select(item => item.ID).OfType<string>());
            continuation = string.IsNullOrEmpty(page.Continuation) ? null : page.Continuation;
            if (continuation is not null && !visited.Add(continuation))
                throw new InvalidDataException("Reader API returned a repeated continuation.");
        } while (continuation is not null);
        return new(ids, serverDate);
    }

    public async Task<IReadOnlyList<ReaderAPIEntry>> RetrieveEntriesAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var entries = new List<ReaderAPIEntry>();
        foreach (var batch in ids.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            var parameters = batch.Select(id => new KeyValuePair<string, string>("i", ItemID(id, Variant)))
                .Append(new("output", "json"));
            using var response = await SendMutationAsync("stream/items/contents", parameters, ct);
            var result = await response.Content.ReadFromJsonAsync<ReaderAPIStreamContentsResponse>(cancellationToken: ct);
            entries.AddRange(result?.Items ?? throw new InvalidDataException("Reader API returned no article collection."));
        }
        return entries;
    }

    private void NoteUsageLimits(HttpResponseMessage response)
    {
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        if (!int.TryParse(Header("X-Reader-Zone1-Usage"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var usage)
            || !int.TryParse(Header("X-Reader-Zone1-Limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit <= 0) return;
        var reset = double.TryParse(Header("X-Reader-Limits-Reset-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) ? seconds : TimeSpan.FromDays(1).TotalSeconds;
        UsageLimits = new(usage, limit, DateTime.UtcNow.AddSeconds(reset));
    }
}
