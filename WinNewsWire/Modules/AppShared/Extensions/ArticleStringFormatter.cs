using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.Extensions;

/// <summary>Port of <c>ArticleStringFormatter</c>.</summary>
public static class ArticleStringFormatter
{
    private const int MaxTitleLength = 1000;
    private const int MaxSummaryLength = 300;
    private static readonly ConcurrentDictionary<string, string> _titleCache = new();
    private static readonly ConcurrentDictionary<string, string> _summaryCache = new();

    public static string TruncatedTitle(Article article)
    {
        if (string.IsNullOrEmpty(article.Title)) return "";
        var key = article.ArticleID + ":t";
        return _titleCache.GetOrAdd(key, _ =>
        {
            var s = StripHtml(article.Title!).Trim();
            s = Regex.Replace(s, "\\s+", " ");
            if (s.Length > MaxTitleLength) s = s.Substring(0, MaxTitleLength);
            return WebUtility.HtmlDecode(s);
        });
    }

    public static string TruncatedSummary(Article article)
    {
        var src = article.Summary ?? article.ContentText ?? article.ContentHtml;
        if (string.IsNullOrEmpty(src)) return "";
        var key = article.ArticleID + ":s";
        return _summaryCache.GetOrAdd(key, _ =>
        {
            var s = StripHtml(src!).Trim();
            s = Regex.Replace(s, "\\s+", " ");
            if (s.Length > MaxSummaryLength) s = s.Substring(0, MaxSummaryLength) + "…";
            return WebUtility.HtmlDecode(s);
        });
    }

    public static string DateString(DateTime? date)
        => date is null ? "" : date.Value.ToLocalTime().ToString("g");

    private static string StripHtml(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (var c in s)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>Port of <c>CacheCleaner</c>. Deletes old files under the caches directory.</summary>
public static class CacheCleaner
{
    public static void PurgeOlderThan(TimeSpan age)
    {
        var root = Core.AppConfig.CachesDirectory;
        var cutoff = DateTime.UtcNow - age;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
