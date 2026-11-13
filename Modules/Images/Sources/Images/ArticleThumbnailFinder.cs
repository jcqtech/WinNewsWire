using WinNewsWire.Articles;
using WinNewsWire.Parsers;

namespace WinNewsWire.Images;

public enum ThumbnailMode { Small, Large }
public sealed record ThumbnailUrlFailure(IReadOnlySet<string> ArticleIds);

public sealed class ArticleThumbnailFinder(ImageDatabase database)
{
    private const int MaximumChosenUrlCount = 50_000;
    private readonly ImageDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _htmlUrls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _decodingFailures = new(StringComparer.Ordinal);
    private sealed record ArticleHtml(string ArticleId, string Html, Uri? BaseUrl);
    private sealed record ChosenThumbnail(string? RawThumbnailLink, string? RawImageLink,
        IReadOnlyList<Attachment>? Attachments, string? Url)
    {
        internal ChosenThumbnail(Article article, string? url)
            : this(article.RawThumbnailLink, article.RawImageLink, article.Attachments, url) { }

        internal bool HasSameImages(Article article)
            => RawThumbnailLink == article.RawThumbnailLink && RawImageLink == article.RawImageLink
                && (Attachments is null ? article.Attachments is null
                    : article.Attachments is not null && Attachments.SequenceEqual(article.Attachments));
    }
    private readonly Dictionary<ThumbnailMode, Dictionary<string, ChosenThumbnail>> _chosenUrls = [];

    /// <summary>Raised after chosen URLs are invalidated; UI subscribers must dispatch.</summary>
    public event EventHandler<ThumbnailUrlFailure>? ThumbnailDownloadFailed;

    public string? ThumbnailUrl(Article article, ThumbnailMode mode, Uri? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(article);
        if (mode is not (ThumbnailMode.Small or ThumbnailMode.Large)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_gate)
        {
            if (!_chosenUrls.TryGetValue(mode, out var chosen)) _chosenUrls[mode] = chosen = new(StringComparer.Ordinal);
            if (chosen.TryGetValue(article.ArticleID, out var cached) && cached.HasSameImages(article))
                return cached.Url;
            var url = ProvidedThumbnailUrl(article, mode)
                ?? CachedOrParsedHtmlUrls(article, baseUrl).FirstOrDefault(url => !HasFailed(url))
                ?? UsableProvidedUrl(NormalizedUrl(article.RawThumbnailLink));
            if (chosen.Count >= MaximumChosenUrlCount) chosen.Clear();
            chosen[article.ArticleID] = new(article, url);
            return url;
        }
    }

    public async Task FindThumbnailUrlsAsync(IEnumerable<Article> articles, CancellationToken ct = default,
        Func<Article, Uri?>? baseUrlResolver = null)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ct.ThrowIfCancellationRequested();
        var pending = new List<ArticleHtml>();
        lock (_gate)
        {
            foreach (var article in articles.DistinctBy(article => article.ArticleID))
            {
                if (_htmlUrls.ContainsKey(article.ArticleID) || ProvidedThumbnailUrl(article, ThumbnailMode.Large) is not null) continue;
                var html = article.ContentHtml ?? article.Summary;
                if (string.IsNullOrEmpty(html)) _htmlUrls[article.ArticleID] = [];
                else pending.Add(new(article.ArticleID, html, baseUrlResolver is null ? article.BaseUrl() : baseUrlResolver(article)));
            }
        }
        if (pending.Count == 0) return;
        var parsed = await Task.Run(() =>
        {
            var urls = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var article in pending)
            {
                ct.ThrowIfCancellationRequested();
                urls[article.ArticleId] = HtmlImageParser.SignificantImageUrlStrings(article.Html, article.BaseUrl, 3);
            }
            return urls;
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ForgetChosenUrls(parsed.Keys);
            foreach (var pair in parsed) _htmlUrls[pair.Key] = pair.Value;
        }
    }

    private IReadOnlyList<string> CachedOrParsedHtmlUrls(Article article, Uri? baseUrl)
    {
        if (_htmlUrls.TryGetValue(article.ArticleID, out var cached)) return cached;
        var html = article.ContentHtml ?? article.Summary;
        IReadOnlyList<string> urls = string.IsNullOrEmpty(html) ? []
            : HtmlImageParser.SignificantImageUrlStrings(html, baseUrl ?? article.BaseUrl(), 3);
        _htmlUrls[article.ArticleID] = urls;
        return urls;
    }

    private string? ProvidedThumbnailUrl(Article article, ThumbnailMode mode)
        => mode == ThumbnailMode.Small
            ? UsableProvidedUrl(NormalizedUrl(article.RawThumbnailLink))
                ?? UsableProvidedUrl(NormalizedUrl(article.RawImageLink))
                ?? UsableProvidedUrl(FirstSignificantImageAttachmentUrl(article))
            : UsableProvidedUrl(NormalizedUrl(article.RawImageLink))
                ?? UsableProvidedUrl(FirstSignificantImageAttachmentUrl(article));

    private string? UsableProvidedUrl(string? url)
        => url is not null && !HasFailed(url) && SignificantImageFilter.IsSignificant(url, null, null) ? url : null;

    private static string? FirstSignificantImageAttachmentUrl(Article article)
        => article.Attachments?.FirstOrDefault(attachment =>
            attachment.DisplayMedium == Attachment.AttachmentMedium.Image && attachment.URL is { } url
                && SignificantImageFilter.IsSignificant(url, attachment.Width, attachment.Height))?.URL;

    public void RecordDecodingFailure(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        lock (_gate) _decodingFailures.Add(url);
        ThumbnailUrlDidFail(url);
    }

    public void ResetDecodingFailures()
    {
        lock (_gate)
        {
            _decodingFailures.Clear();
            _chosenUrls.Clear();
        }
    }

    public void EmptyCache()
    {
        lock (_gate)
        {
            _htmlUrls.Clear();
            _chosenUrls.Clear();
        }
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

    private bool HasFailed(string url)
    {
        if (_database.RecentlyFailed(url) || _database.IsUnsuitableForThumbnail(url)) return true;
        lock (_gate) return _decodingFailures.Contains(url);
    }

    private static string? NormalizedUrl(string? value)
        => string.IsNullOrEmpty(value) ? null : Uri.TryCreate(value, UriKind.Absolute, out var url) ? url.AbsoluteUri : value;
}
