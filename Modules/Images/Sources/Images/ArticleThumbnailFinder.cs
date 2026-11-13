using WinNewsWire.Articles;
using WinNewsWire.Parsers;

namespace WinNewsWire.Images;

public enum ThumbnailMode { Small, Large }
public sealed record ThumbnailUrlFailure(IReadOnlySet<string> ArticleIds);

public sealed class ArticleThumbnailFinder(ImageDatabase database)
{
    private const int MaximumCachedArticleCount = 50_000;
    private readonly ImageDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _htmlUrls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _discovering = new(StringComparer.Ordinal);
    private readonly HashSet<string> _decodingFailures = new(StringComparer.Ordinal);
    private sealed record ChosenThumbnail(Article Article, string? Url);
    private readonly Dictionary<ThumbnailMode, Dictionary<string, ChosenThumbnail>> _chosenUrls = [];

    /// <summary>Raised after discovery; UI subscribers must dispatch to their UI thread.</summary>
    public event EventHandler<IReadOnlySet<string>>? ThumbnailUrlsAvailable;
    /// <summary>Raised after chosen URLs are invalidated; UI subscribers must dispatch.</summary>
    public event EventHandler<ThumbnailUrlFailure>? ThumbnailDownloadFailed;

    public string? ThumbnailUrl(Article article, ThumbnailMode mode)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (mode is not (ThumbnailMode.Small or ThumbnailMode.Large)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_gate)
        {
            if (!_chosenUrls.TryGetValue(mode, out var chosen)) _chosenUrls[mode] = chosen = new(StringComparer.Ordinal);
            if (chosen.TryGetValue(article.ArticleID, out var cached) && ReferenceEquals(cached.Article, article))
                return cached.Url;
            var url = ProvidedThumbnailUrl(article, mode)
                ?? (_htmlUrls.TryGetValue(article.ArticleID, out var urls) ? urls.FirstOrDefault(IsUsable) : null);
            if (chosen.Count >= MaximumCachedArticleCount) chosen.Clear();
            chosen[article.ArticleID] = new(article, url);
            return url;
        }
    }

    public async Task DiscoverThumbnailUrlsAsync(IEnumerable<Article> articles, CancellationToken ct = default,
        Func<Article, Uri?>? baseUrlResolver = null)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ct.ThrowIfCancellationRequested();
        Article[] pending;
        lock (_gate)
        {
            if (_htmlUrls.Count > MaximumCachedArticleCount)
            {
                _htmlUrls.Clear();
                _chosenUrls.Clear();
            }
            pending = articles.DistinctBy(article => article.ArticleID)
                .Where(article => !string.IsNullOrEmpty(article.ContentHtml ?? article.Summary)
                    && !_htmlUrls.ContainsKey(article.ArticleID) && !_discovering.Contains(article.ArticleID)
                    && ThumbnailUrl(article, ThumbnailMode.Small) is null).ToArray();
            foreach (var article in pending) _discovering.Add(article.ArticleID);
        }
        if (pending.Length == 0) return;
        try
        {
            var baseUrls = pending.ToDictionary(article => article.ArticleID,
                article => baseUrlResolver is null ? article.BaseUrl() : baseUrlResolver(article), StringComparer.Ordinal);
            var saved = await _database.FetchArticleThumbnailsAsync(pending.Select(article => article.ArticleID)).ConfigureAwait(false);
            var savedIds = saved.Select(record => record.ArticleId).ToHashSet(StringComparer.Ordinal);
            var parsed = await Task.Run(() =>
            {
                var records = new List<ArticleThumbnailRecord>();
                foreach (var article in pending.Where(article => !savedIds.Contains(article.ArticleID)))
                {
                    ct.ThrowIfCancellationRequested();
                    var html = article.ContentHtml ?? article.Summary;
                    if (string.IsNullOrEmpty(html)) continue;
                    records.Add(new(article.ArticleID, HtmlImageParser.SignificantImageUrlStrings(html, baseUrls[article.ArticleID], 3)));
                }
                return records;
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (parsed.Count > 0) await _database.SaveArticleThumbnailsAsync(parsed).ConfigureAwait(false);
            var records = saved.Concat(parsed).ToArray();
            lock (_gate)
            {
                ForgetChosenUrls(records.Select(record => record.ArticleId));
                foreach (var record in records) _htmlUrls[record.ArticleId] = record.ThumbnailUrls;
            }
            var found = records.Where(record => record.ThumbnailUrls.Count > 0)
                .Select(record => record.ArticleId).ToHashSet(StringComparer.Ordinal);
            if (found.Count > 0) ThumbnailUrlsAvailable?.Invoke(this, found);
        }
        finally
        {
            lock (_gate)
                foreach (var article in pending) _discovering.Remove(article.ArticleID);
        }
    }

    private string? ProvidedThumbnailUrl(Article article, ThumbnailMode mode)
    {
        var thumbnail = NormalizedUrl(article.RawThumbnailLink);
        var image = NormalizedUrl(article.RawImageLink);
        var attachment = article.Attachments?.FirstOrDefault(attachment =>
            attachment.DisplayMedium == Attachment.AttachmentMedium.Image && attachment.URL is { } url
                && SignificantImageFilter.IsSignificant(url, attachment.Width, attachment.Height))?.URL;
        var candidates = mode == ThumbnailMode.Small ? new[] { thumbnail, image, attachment } : [image, attachment, thumbnail];
        return candidates.FirstOrDefault(url => url is not null && IsUsable(url));
    }

    public void RecordDecodingFailure(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        lock (_gate) _decodingFailures.Add(url);
        ThumbnailUrlDidFail(url);
    }

    public void ThumbnailUrlDidFail(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        HashSet<string> ids;
        lock (_gate)
        {
            ids = _chosenUrls.Values.SelectMany(chosen => chosen.Where(pair => pair.Value.Url == url).Select(pair => pair.Key))
                .ToHashSet(StringComparer.Ordinal);
            ForgetChosenUrls(ids);
        }
        if (ids.Count > 0) ThumbnailDownloadFailed?.Invoke(this, new(ids));
    }

    private void ForgetChosenUrls(IEnumerable<string> ids)
    {
        foreach (var chosen in _chosenUrls.Values)
            foreach (var id in ids) chosen.Remove(id);
    }

    private bool IsUsable(string url)
    {
        if (!SignificantImageFilter.IsSignificant(url, null, null)
            || _database.RecentlyFailed(url) || _database.IsUnsuitableForThumbnail(url)) return false;
        lock (_gate) return !_decodingFailures.Contains(url);
    }

    private static string? NormalizedUrl(string? value)
        => string.IsNullOrEmpty(value) ? null : Uri.TryCreate(value, UriKind.Absolute, out var url) ? url.AbsoluteUri : value;
}
