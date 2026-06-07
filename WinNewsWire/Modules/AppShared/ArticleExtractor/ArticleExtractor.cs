using System.Net.Http;
using WinNewsWire.AppShared.ArticleRendering;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.ArticleExtractor;

/// <summary>Port of <c>ArticleExtractor</c> (Mercury-style reader mode).</summary>
public interface IArticleExtractor
{
    Task<ExtractedArticle?> ExtractAsync(Article article, CancellationToken ct = default);
}

public sealed class NullArticleExtractor : IArticleExtractor
{
    public static NullArticleExtractor Shared { get; } = new();
    public Task<ExtractedArticle?> ExtractAsync(Article article, CancellationToken ct = default)
        => Task.FromResult<ExtractedArticle?>(null);
}

/// <summary>
/// Reader-mode extractor backed by SmartReader (a .NET port of Mozilla Readability).
/// Replaces the Mercury Parser web service used on Mac with an in-process implementation.
/// Fetches the article's canonical URL and returns a simplified HTML body, title, author,
/// and dominant image suitable for inline rendering in <see cref="ArticleRenderer"/>.
/// </summary>
public sealed class SmartReaderArticleExtractor : IArticleExtractor
{
    public static SmartReaderArticleExtractor Shared { get; } = new();

    public async Task<ExtractedArticle?> ExtractAsync(Article article, CancellationToken ct = default)
    {
        var url = !string.IsNullOrEmpty(article.RawLink) ? article.RawLink
                : !string.IsNullOrEmpty(article.RawExternalLink) ? article.RawExternalLink
                : null;
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        try
        {
            var reader = new SmartReader.Reader(uri.ToString());
            reader.Debug = false;
            var parsed = await reader.GetArticleAsync().ConfigureAwait(false);
            if (parsed is null || !parsed.IsReadable) return null;
            return new ExtractedArticle(
                Title: parsed.Title ?? article.Title,
                Author: parsed.Author,
                DatePublished: parsed.PublicationDate?.ToString("o"),
                Content: parsed.Content ?? parsed.TextContent ?? string.Empty,
                Url: uri.ToString());
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch { return null; }
    }
}
