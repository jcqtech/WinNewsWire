using System.Security.Cryptography;
using WinNewsWire.Articles;
using WinNewsWire.Parsers;
using WinNewsWire.Web;

namespace WinNewsWire.Account;

/// <summary>
/// Port of <c>LocalAccountRefresher.swift</c>. Implements <see cref="IDownloadSessionDelegate"/>
/// to drive batch feed downloads via <see cref="DownloadSession"/>.
/// </summary>
internal sealed class LocalAccountRefresher : IDownloadSessionDelegate, IDisposable
{
    private readonly Account _account;
    private Dictionary<string, Feed> _urlToFeed = new(StringComparer.Ordinal);
    private TaskCompletionSource? _sessionTcs;
    private DownloadSession? _downloadSession;

    // ── Skip-rule constants (mirroring NNW) ──

    private static readonly TimeSpan MinimumTimeBetweenChecks = TimeSpan.FromMinutes(29);
    private static readonly TimeSpan CacheControlMaxMaxAge = TimeSpan.FromHours(5);
    private static readonly TimeSpan ConditionalGetResetAge = TimeSpan.FromDays(8);
    private static readonly TimeSpan SpecialCaseCutoff = TimeSpan.FromHours(25);

    /// <summary>Hosts that will never return a feed (legacy Twitter URLs).</summary>
    private static readonly string[] BadHosts =
        ["twitter.com", "www.twitter.com", "x.com", "www.x.com"];

    public LocalAccountRefresher(Account account) => _account = account;

    // ── Public entry point ──

    public async Task RefreshAllAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
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
                _urlToFeed[feed.Url] = feed;
                urls.Add(uri);
            }
        }

        _sessionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reg = ct.Register(() => _sessionTcs.TrySetCanceled(ct));
        _downloadSession = new DownloadSession(this);
        if (progress is not null)
            _downloadSession.ProgressChanged += (_, p) =>
                progress.Report(new ProgressInfo(p.NumberRemaining, p.NumberCompleted, p.NumberOfTasks));

        _downloadSession.Download(urls);
        await _sessionTcs.Task;

        try { _account.SaveChanges(); } catch { /* best effort */ }
        await _account.RecalculateUnreadCountsAsync();
    }

    public void Dispose() => _downloadSession?.Dispose();

    // ── IDownloadSessionDelegate ──

    public ConditionalGetInfo? ConditionalGetInfoFor(Uri url)
    {
        if (!_urlToFeed.TryGetValue(url.AbsoluteUri, out var feed))
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
        if (!_urlToFeed.TryGetValue(url.AbsoluteUri, out var feed))
            return;

        feed.LastCheckDate = DateTime.UtcNow;

        if (error is not null) return;

        bool statusIsOK = response.StatusIsOK;
        bool statusIsOKOrNotModified = statusIsOK || response.IsNotModified;
        if (!statusIsOKOrNotModified) return;

        // Update conditional GET info
        var conditionalGet = response.ToConditionalGetInfo();
        if (conditionalGet is not null)
        {
            if (conditionalGet.ETag != feed.ETag || conditionalGet.LastModified != feed.LastModified)
            {
                feed.ETag = conditionalGet.ETag;
                feed.LastModified = conditionalGet.LastModified;
                feed.ConditionalGetInfoDate = DateTime.UtcNow;
            }
        }

        if (!statusIsOK) return; // 304 — nothing more to do

        // Update Cache-Control info
        var cacheControl = response.ToCacheControlInfo();
        if (cacheControl is not null)
            feed.CacheControlInfo = cacheControl;

        if (data.Length == 0) return;

        // Content-hash dedup — skip parsing if data hasn't changed since last refresh.
        // The hash is salted with ParserVersion so that bumping it invalidates every
        // feed's stored hash on next refresh (forces a one-time re-parse after parser
        // fixes — e.g. new image/namespace extraction). Cheaper than unconditional
        // re-parse for feeds that don't honor conditional GET.
        var dataHash = ComputeMd5(data, ParserVersion);
        if (dataHash == feed.ContentHash) return;

        // Parse
        var parserData = new ParserData(feed.Url, data);
        if (!FeedParser.CanParse(parserData)) return;
        var parsed = FeedParser.Parse(parserData);
        if (parsed is null) return;

        // Update feed metadata
        if (feed.Name is null && !string.IsNullOrEmpty(parsed.Title)) feed.Name = parsed.Title;
        if (feed.HomePageUrl is null) feed.HomePageUrl = parsed.HomePageUrl;
        feed.ContentHash = dataHash;
        feed.OnDisplayNameChanged();

        // Create articles
        var arrived = DateTime.UtcNow;
        var articles = parsed.Items.Select(item =>
        {
            var authors = item.Authors?.Select(a =>
                    Articles.Author.Create(null, a.Name, a.Url, a.AvatarUrl, a.EmailAddress))
                .Where(a => a is not null).Cast<Articles.Author>().ToHashSet();
            var status = new ArticleStatus(
                Article.CalculatedArticleID(feed.FeedID, item.UniqueId),
                read: false, dateArrived: arrived);
            return new Article(
                _account.AccountID, articleID: null, feedID: feed.FeedID, uniqueID: item.UniqueId,
                title: item.Title, contentHtml: item.ContentHtml, contentText: item.ContentText, markdown: null,
                url: item.Url, externalURL: item.ExternalUrl, summary: item.Summary, imageURL: item.ImageUrl,
                datePublished: item.DatePublished, dateModified: item.DateModified,
                authors: (authors?.Count ?? 0) == 0 ? null : authors, attachments: null, status: status);
        }).ToList();

        var newArticles = await _account.Database.UpdateArticlesAsync(articles);
        // Raise on the account so the notifier (and any other UI listeners) can react.
        if (newArticles.Count > 0)
            _account.RaiseNewArticlesDownloaded(newArticles);
    }

    public Task HttpErrorAsync(int statusCode, Uri url)
    {
        // Log HTTP errors. DownloadSession already handles 429/4xx caching;
        // we just record the event for diagnostics.
        System.Diagnostics.Debug.WriteLine(
            $"LocalAccountRefresher: HTTP {statusCode} for {url.AbsoluteUri}");
        return Task.CompletedTask;
    }

    public bool ShouldContinueAfterReceivingData(ReadOnlyMemory<byte> dataSoFar, Uri url)
    {
        return !IsDefinitelyNotFeed(dataSoFar.Span);
    }

    public Task DownloadSessionDidCompleteAsync()
    {
        _sessionTcs?.TrySetResult();
        return Task.CompletedTask;
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
    private const string ParserVersion = "v2-media-image";

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
            var buf = new byte[saltBytes.Length + data.Length];
            Buffer.BlockCopy(saltBytes, 0, buf, 0, saltBytes.Length);
            Buffer.BlockCopy(data, 0, buf, saltBytes.Length, data.Length);
            hash = MD5.HashData(buf);
        }
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
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
