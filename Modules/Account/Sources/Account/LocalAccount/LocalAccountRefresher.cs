using System.Security.Cryptography;
using System.Collections.Concurrent;
using WinNewsWire.ActivityLog;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Parsers;
using WinNewsWire.Web;

namespace WinNewsWire.Account;

/// <summary>
/// Port of <c>LocalAccountRefresher.swift</c>. Implements <see cref="IDownloadSessionDelegate"/>
/// to drive batch feed downloads via <see cref="DownloadSession"/>.
/// </summary>
internal sealed class LocalAccountRefresher : IDownloadSessionDelegate, ILocalAccountRefresher
{
    internal event Action<Feed, ArticlesDatabase.ArticleChanges>? FeedUpdated;
    internal bool DeleteOlderArticles { get; set; }
    private readonly Account _account;
    private Dictionary<string, Feed> _urlToFeed = new(StringComparer.Ordinal);
    private readonly DownloadSession _downloadSession;
    private IProgress<ProgressInfo>? _progress;
    private CancellationToken _cancellation;
    // Keep parsed feeds waiting for database writes bounded without serializing network downloads.
    private readonly SemaphoreSlim _processingGate = new(2, 2);
    private int _disposed;
    private readonly ConcurrentDictionary<string, DownloadActivity> _activities = new(StringComparer.Ordinal);
    private readonly record struct DownloadActivity(int ID, bool Started = false);

    // ── Skip-rule constants (mirroring NNW) ──

    private static readonly TimeSpan MinimumTimeBetweenChecks = TimeSpan.FromMinutes(29);
    private static readonly TimeSpan CacheControlMaxMaxAge = TimeSpan.FromHours(5);
    private static readonly TimeSpan ConditionalGetResetAge = TimeSpan.FromDays(8);
    private static readonly TimeSpan SpecialCaseCutoff = TimeSpan.FromHours(25);

    /// <summary>Hosts that will never return a feed (legacy Twitter URLs).</summary>
    private static readonly string[] BadHosts =
        ["twitter.com", "www.twitter.com", "x.com", "www.x.com"];

    public LocalAccountRefresher(Account account)
    {
        _account = account;
        _downloadSession = new(this);
        _downloadSession.ProgressChanged += (_, progress) => _progress?.Report(progress);
    }

    // ── Public entry point ──

    public async Task RefreshAllAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var feeds = _account.FlattenedFeeds().ToList();
        var specialCaseCutoffDate = DateTime.UtcNow - SpecialCaseCutoff;
        var filtered = feeds.Where(f => !FeedShouldBeSkipped(f, specialCaseCutoffDate)).ToList();

        if (filtered.Count == 0)
        {
            await _account.RecalculateUnreadCountsAsync();
            return;
        }

        _urlToFeed = new Dictionary<string, Feed>(filtered.Count, StringComparer.Ordinal);
        var urls = new HashSet<Uri>();
        foreach (var feed in filtered)
        {
            if (Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri))
            {
                _urlToFeed[uri.AbsoluteUri] = feed;
                if (urls.Add(uri))
                    _activities[uri.AbsoluteUri] = new(ActivityLogger.Shared.CreateActivity(_account.ActivityOwner,
                        ActivityKind.RefreshFeedContent(feed.Url), feed.NameForDisplay));
            }
        }

        _progress = progress;
        _cancellation = ct;
        try
        {
            _downloadSession.Download(urls);
            using var registration = ct.Register(_downloadSession.CancelAll);
            await _downloadSession.Completion.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            _account.SaveChanges();
            await _account.RecalculateUnreadCountsAsync();
        }
        catch (Exception error)
        {
            foreach (var url in _activities.Keys)
                if (_activities.TryRemove(url, out var activity))
                {
                    if (error is OperationCanceledException && ct.IsCancellationRequested)
                        ActivityLogger.Shared.DidComplete(activity.ID, "Cancelled", durationIsSignificant: false);
                    else ActivityLogger.Shared.DidFail(activity.ID, error);
                }
            throw;
        }
        finally { _progress = null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _downloadSession.Dispose();
        _ = _downloadSession.Completion.ContinueWith(_ => _processingGate.Dispose(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        CompleteRemainingActivities("Cancelled");
    }

    // ── IDownloadSessionDelegate ──

    public ConditionalGetInfo? ConditionalGetInfoFor(Uri url)
    {
        if (_activities.TryGetValue(url.AbsoluteUri, out var activity) && !activity.Started
            && _activities.TryUpdate(url.AbsoluteUri, activity with { Started = true }, activity))
            ActivityLogger.Shared.DidStart(activity.ID);
        if (!_urlToFeed.TryGetValue(url.AbsoluteUri, out var feed))
            return null;

        if (feed.ContentHash?.StartsWith(ParserVersion + ":", StringComparison.Ordinal) != true)
            return null;

        if (feed.ETag is null && feed.LastModified is null)
            return null;

        // Drop conditional GET info older than 8 days — some servers always 304
        // when any conditional GET headers are sent. Exception: OpenRSS / rachelbythebay.
        if (feed.ConditionalGetInfoDate is { } date)
        {
            if (date < DateTime.UtcNow - ConditionalGetResetAge)
            {
                if (!SpecialCases.ContainsSpecialCase(url.AbsoluteUri,
                        [SpecialCases.OpenRSSOrgHostName, SpecialCases.RachelByTheBayHostName]))
                {
                    feed.ETag = null;
                    feed.LastModified = null;
                    feed.ConditionalGetInfoDate = null;
                    return null;
                }
            }
        }

        return new ConditionalGetInfo(feed.LastModified, feed.ETag);
    }

    public async Task DownloadDidCompleteAsync(Uri url, DownloadSessionResponse response, byte[] data, Exception? error)
    {
        if (_cancellation.IsCancellationRequested) return;
        if (!_urlToFeed.TryGetValue(url.AbsoluteUri, out var feed))
            return;

        if (error is not null) { FailActivity(url, error); return; }
        try { await _processingGate.WaitAsync(_cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { return; }
        try
        {
            if (_cancellation.IsCancellationRequested) return;
            feed.LastCheckDate = DateTime.UtcNow;
            feed.LastResponseCode = response.StatusCode == 0 ? null : response.StatusCode;
            var message = await ProcessDownloadAsync(feed, response, data);
            if (_activities.TryRemove(url.AbsoluteUri, out var activity))
                ActivityLogger.Shared.DidComplete(activity.ID, message, returnedFromCache: response.ReturnedFromCache || response.IsNotModified);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or FeedParserException
            or System.Data.Common.DbException or System.Xml.XmlException or System.Text.Json.JsonException or InvalidOperationException or HttpRequestException)
        {
            FailActivity(url, failure);
        }
        finally { _processingGate.Release(); }
    }

    private async Task<string> ProcessDownloadAsync(Feed feed, DownloadSessionResponse response, byte[] data)
    {
        bool statusIsOK = response.StatusIsOK;
        bool statusIsOKOrNotModified = statusIsOK || response.IsNotModified;
        if (!statusIsOKOrNotModified) throw new HttpRequestException($"Feed download returned HTTP {response.StatusCode}.");

        if (!statusIsOK)
        {
            ApplyResponseHeaders(feed, response);
            return "Not modified";
        }

        if (data.Length == 0) return "No content";

        // Content-hash dedup — skip parsing if data hasn't changed since last refresh.
        // The hash is salted with ParserVersion so that bumping it invalidates every
        // feed's stored hash on next refresh (forces a one-time re-parse after parser
        // fixes — e.g. new image/namespace extraction). Cheaper than unconditional
        // re-parse for feeds that don't honor conditional GET.
        var dataHash = ContentHashFor(data);
        if (dataHash == feed.ContentHash)
        {
            ApplyResponseHeaders(feed, response);
            return "No changes";
        }

        // Parse
        var parserData = new ParserData(feed.Url, data);
        if (!FeedParser.CanParse(parserData)) throw new InvalidDataException("The downloaded content is not a supported feed.");
        var parsed = await FeedParser.ParseAsync(parserData, _cancellation) ?? throw new InvalidDataException("The downloaded feed could not be parsed.");

        var changes = await _account.Database.UpdateAsync(parsed.Items, feed.FeedID,
            deleteOlder: DeleteOlderArticles || _account.Delegate.UsesFeedBasedRetention);
        if (changes.Deleted is { Count: > 0 } deleted)
            await _account.Delegate.PublisherArticlesDeletedAsync(_account, deleted.Select(a => a.ArticleID).ToArray(), _cancellation);
        Account.UpdateFeedMetadata(feed, parsed);
        feed.ContentHash = dataHash;
        ApplyResponseHeaders(feed, response);
        FeedUpdated?.Invoke(feed, changes);
        if (changes.New is { Count: > 0 } newArticles)
            _account.RaiseNewArticlesDownloaded(newArticles);
        var count = changes.New?.Count ?? 0;
        return $"{count} new article{(count == 1 ? "" : "s")}, {ActivityLogger.DataSizeMessage(data)}";
    }

    private static void ApplyResponseHeaders(Feed feed, DownloadSessionResponse response)
    {
        if (response.ToConditionalGetInfo() is { } conditional &&
            (conditional.ETag != feed.ETag || conditional.LastModified != feed.LastModified))
        {
            feed.ETag = conditional.ETag;
            feed.LastModified = conditional.LastModified;
            feed.ConditionalGetInfoDate = DateTime.UtcNow;
        }
        if (response.StatusIsOK && response.ToCacheControlInfo() is { } cacheControl) feed.CacheControlInfo = cacheControl;
    }

    public Task HttpErrorAsync(int statusCode, Uri url)
    {
        if (_urlToFeed.TryGetValue(url.AbsoluteUri, out var feed))
        {
            feed.LastCheckDate = DateTime.UtcNow;
            feed.LastResponseCode = statusCode;
        }
        FailActivity(url, new HttpRequestException($"HTTP {statusCode} for {url.AbsoluteUri}"));
        return Task.CompletedTask;
    }

    public bool ShouldContinueAfterReceivingData(ReadOnlyMemory<byte> dataSoFar, Uri url)
    {
        return !IsDefinitelyNotFeed(dataSoFar.Span);
    }

    public Task DownloadSessionDidCompleteAsync()
    {
        CompleteRemainingActivities(_cancellation.IsCancellationRequested ? "Cancelled" : "Skipped");
        return Task.CompletedTask;
    }

    private void CompleteRemainingActivities(string message)
    {
        foreach (var url in _activities.Keys)
            if (_activities.TryRemove(url, out var activity))
                ActivityLogger.Shared.DidComplete(activity.ID, message, durationIsSignificant: false);
    }

    private void FailActivity(Uri url, Exception error)
    {
        if (_activities.TryRemove(url.AbsoluteUri, out var activity)) ActivityLogger.Shared.DidFail(activity.ID, error);
        System.Diagnostics.Trace.TraceError($"Feed refresh failed for {url}: {error}");
    }

    // ── Feed-skip rules (port of NNW private helpers) ──

    private static bool FeedShouldBeSkipped(Feed feed, DateTime specialCaseCutoffDate)
    {
        return FeedShouldBeSkippedForCacheControlReasons(feed)
            || FeedIsDisallowed(feed)
            || FeedShouldBeSkippedForTimingReasons(feed, specialCaseCutoffDate);
    }

    private static bool FeedIsDisallowed(Feed feed)
    {
        if (!Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri)) return true;

        var host = uri.Host.ToLowerInvariant();
        foreach (var bad in BadHosts)
        {
            if (host == bad) return true;
        }
        return false;
    }

    private static bool FeedShouldBeSkippedForTimingReasons(Feed feed, DateTime specialCaseCutoffDate)
    {
        if (feed.LastCheckDate is not { } lastCheck) return false;

        // Special-case hosts get a longer window (25 hours)
        if (SpecialCases.ContainsSpecialCase(feed.Url,
                [SpecialCases.RachelByTheBayHostName, SpecialCases.OpenRSSOrgHostName]))
        {
            if (lastCheck > specialCaseCutoffDate) return true;
        }

        // All feeds: minimum 29 minutes between checks
        if (DateTime.UtcNow - lastCheck < MinimumTimeBetweenChecks) return true;

        return false;
    }

    private static bool FeedShouldBeSkippedForCacheControlReasons(Feed feed)
    {
        if (feed.CacheControlInfo is not { } cc || cc.CanResume) return false;

        // OpenRSS gets unclamped Cache-Control — they configure it correctly
        if (SpecialCases.ContainsSpecialCase(feed.Url, [SpecialCases.OpenRSSOrgHostName]))
            return true;

        // All other feeds: honor Cache-Control with a max cap
        if (!cc.CanResume_Clamped(CacheControlMaxMaxAge)) return true;

        return false;
    }

    // ── Helpers ──

    /// <summary>Bump this whenever feed parser extraction logic changes in a way
    /// that should invalidate previously-stored ContentHash values (forces a
    /// one-time re-parse of every feed on next refresh).</summary>
    private const string ParserVersion = "v4-native-parsers";

    internal static string ContentHashFor(byte[] data) => ParserVersion + ":" + ComputeMd5(data, ParserVersion);

    private static string ComputeMd5(byte[] data, string? salt = null)
    {
        byte[] hash;
        if (string.IsNullOrEmpty(salt))
        {
            hash = MD5.HashData(data);
        }
        else
        {
            var saltBytes = System.Text.Encoding.UTF8.GetBytes(salt + ":");
            using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            incremental.AppendData(saltBytes);
            incremental.AppendData(data);
            hash = incremental.GetHashAndReset();
        }
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Returns true if the data prefix is definitely not a feed (e.g. image magic bytes).
    /// Port of <c>Data.isDefinitelyNotFeed()</c> / <c>Data.isImage</c> in NNW.
    /// </summary>
    private static bool IsDefinitelyNotFeed(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;

        // PNG: 89 50 4E 47
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return true;

        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return true;

        // GIF: "GIF8"
        if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8')
            return true;

        // BMP: "BM"
        if (data[0] == (byte)'B' && data[1] == (byte)'M')
            return true;

        // WebP: "RIFF" ... "WEBP"
        if (data.Length >= 12 &&
            data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
            return true;

        return false;
    }
}
