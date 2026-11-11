using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.AppShared.Extensions;

/// <summary>Port of <c>ArticleStringFormatter</c>.</summary>
public sealed class ArticleStringFormatter
{
    public static ArticleStringFormatter Shared { get; } = new();
    private const int MaxTitleLength = 1000;
    private const int MaxSummaryLength = 300;
    private readonly ConcurrentDictionary<(string Title, bool ForHtml), string> _titleCache = new();
    private readonly ConcurrentDictionary<string, AttributedText> _attributedTitleCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string AccountID, string ArticleID), (WeakReference<string> Body, string Text)> _summaryCache = new();

    public string TruncatedTitle(Article article, bool forHtml = false)
    {
        if (string.IsNullOrEmpty(article.Title)) return "";
        return _titleCache.GetOrAdd((article.Title, forHtml), key =>
        {
            var text = SanitizedTitle(key.Title, key.ForHtml)!.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            if (!key.ForHtml) text = text.DecodeHtmlEntities();
            return Truncate(text.CollapsingWhitespace(), MaxTitleLength);
        });
    }

    public AttributedText AttributedTruncatedTitle(Article article)
        => string.IsNullOrEmpty(article.Title) ? AttributedText.Empty
            : _attributedTitleCache.GetOrAdd(article.Title, _ => SimpleHtml.Parse(TruncatedTitle(article, forHtml: true)));

    public string TruncatedSummary(Article article)
    {
        var body = article.Body();
        if (body is null) return "";
        var key = (article.AccountID, article.ArticleID);
        if (_summaryCache.TryGetValue(key, out var entry) && entry.Body.TryGetTarget(out var cachedBody) && cachedBody == body) return entry.Text;
        var text = body.StripHtml(MaxSummaryLength).DecodeHtmlEntities();
        if (text == "Comments") text = "";
        _summaryCache[key] = (new(body), text);
        return text;
    }

    public string DateString(DateTime? date)
    {
        if (date is null) return "";
        var local = date.Value.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("t") : local.ToString("d");
    }

    public string TruncatedFeedName(string name) => Truncate(name, 100);

    public void EmptyCaches()
    {
        _titleCache.Clear();
        _attributedTitleCache.Clear();
        _summaryCache.Clear();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var characters = StringInfo.GetTextElementEnumerator(text);
        int count = 0;
        while (characters.MoveNext())
            if (count++ == maxLength) return text[..characters.ElementIndex];
        return text;
    }

    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "abbr", "b", "bdi", "bdo", "cite", "code", "del", "dfn", "em", "i", "ins",
        "kbd", "mark", "q", "s", "samp", "small", "strong", "sub", "sup", "time", "u", "var"
    };

    public static string? SanitizedTitle(string? title, bool forHtml)
    {
        if (title is null) return null;
        var output = new StringBuilder(title.Length);
        for (int i = 0; i < title.Length;)
        {
            if (title[i] != '<')
            {
                output.Append(title[i++]);
                continue;
            }
            int end = title.IndexOf('>', i + 1);
            bool closed = end >= 0;
            if (!closed) end = title.Length;
            if (end == i + 1)
            {
                i++;
                continue;
            }
            int nameStart = i + 1;
            if (title[nameStart] == '/') nameStart++;
            int nameEnd = nameStart;
            while (nameEnd < end && char.IsAsciiLetter(title[nameEnd])) nameEnd++;
            bool allowed = AllowedTags.Contains(title[nameStart..nameEnd]);
            if (forHtml || !allowed)
            {
                output.Append(forHtml && !allowed ? "&lt;" : "<");
                output.Append(title, i + 1, end - i - 1);
                if (closed) output.Append(forHtml && !allowed ? "&gt;" : ">");
            }
            i = closed ? end + 1 : end;
        }
        return output.ToString();
    }
}
