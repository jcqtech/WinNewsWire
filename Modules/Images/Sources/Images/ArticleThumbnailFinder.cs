using WinNewsWire.Articles;
using WinNewsWire.Parsers;

namespace WinNewsWire.Images;

public enum ThumbnailMode { Small, Large }

public sealed class ArticleThumbnailFinder(ImageMetadataDatabase database)
{
    private const int MaximumCachedArticleCount = 20_000;
    private readonly ImageMetadataDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _htmlUrls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _discovering = new(StringComparer.Ordinal);
    private readonly HashSet<string> _decodingFailures = new(StringComparer.Ordinal);

    /// <summary>Raised after discovery; UI subscribers must dispatch to their UI thread.</summary>
    public event EventHandler<IReadOnlySet<string>>? ThumbnailUrlsAvailable;

    public string? ThumbnailUrl(Article article, ThumbnailMode mode)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (mode is not (ThumbnailMode.Small or ThumbnailMode.Large)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (ProvidedThumbnailUrl(article, mode) is { } provided) return provided;
        lock (_gate)
            return _htmlUrls.TryGetValue(article.ArticleID, out var urls) ? urls.FirstOrDefault(IsUsable) : null;
    }

    public async Task DiscoverThumbnailUrlsAsync(IEnumerable<Article> articles, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ct.ThrowIfCancellationRequested();
        Article[] pending;
        lock (_gate)
        {
            if (_htmlUrls.Count > MaximumCachedArticleCount) _htmlUrls.Clear();
            pending = articles.DistinctBy(article => article.ArticleID)
                .Where(article => !string.IsNullOrEmpty(article.ContentHtml ?? article.Summary)
                    && !_htmlUrls.ContainsKey(article.ArticleID) && !_discovering.Contains(article.ArticleID)
                    && ProvidedThumbnailUrl(article, ThumbnailMode.Small) is null).ToArray();
            foreach (var article in pending) _discovering.Add(article.ArticleID);
        }
        if (pending.Length == 0) return;
        try
        {
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
                    Uri.TryCreate(article.RawLink ?? article.RawExternalLink, UriKind.Absolute, out var baseUrl);
                    records.Add(new(article.ArticleID, HtmlImageParser.SignificantImageUrlStrings(html, baseUrl, 3)));
                }
                return records;
            }, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (parsed.Count > 0) await _database.SaveArticleThumbnailsAsync(parsed).ConfigureAwait(false);
            var records = saved.Concat(parsed).ToArray();
            lock (_gate)
                foreach (var record in records) _htmlUrls[record.ArticleId] = record.ThumbnailUrls;
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
