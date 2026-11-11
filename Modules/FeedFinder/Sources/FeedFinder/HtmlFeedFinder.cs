using WinNewsWire.Parsers;
using WinNewsWire.Core;
using WinNewsWire.Web;

namespace WinNewsWire.FeedFinder;

internal static class HtmlFeedFinder
{
    private static readonly string[] WordsToMatch = ["feed", "xml", "rss", "atom", "json"];
    private static readonly string[] NonFeedDomains = ["x.com", "twitter.com", "facebook.com", "instagram.com"];
    private static readonly HashSet<string> NonFeedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".tiff", ".heic", ".svg",
        ".webp", ".bmp", ".ico", ".zip", ".tar", ".tgz", ".gz", ".dmg", ".mp3", ".mp4",
        ".mov", ".mpeg", ".mpg", ".m4a", ".aac", ".wav", ".aiff", ".doc", ".docx",
        ".xls", ".xlsx", ".ppt", ".pptx"
    };

    public static HashSet<FeedSpecifier> FindIn(ParserData parserData)
    {
        var dict = new Dictionary<string, FeedSpecifier>();
        int orderFound = 0;

        var metadata = HtmlMetadataParser.Parse(parserData);
        foreach (var fl in metadata.FeedLinks)
        {
            var u = fl.Url?.NormalizedUrl();
            if (string.IsNullOrEmpty(u)) continue;
            orderFound++;
            Add(dict, new FeedSpecifier(fl.Title, u, FeedSpecifierSource.HtmlHead, orderFound));
        }

        var links = HtmlLinkParser.ParseLinks(parserData);
        foreach (var link in links)
        {
            var u = link.Href?.NormalizedUrl();
            if (string.IsNullOrEmpty(u)) continue;
            if (!MightBeFeed(u)) continue;
            orderFound++;
            Add(dict, new FeedSpecifier(link.Text, u, FeedSpecifierSource.HtmlLink, orderFound));
        }

        return dict.Values.ToHashSet();
    }

    private static void Add(Dictionary<string, FeedSpecifier> d, FeedSpecifier fs)
    {
        if (d.TryGetValue(fs.UrlString, out var existing)) d[fs.UrlString] = existing.MergeWith(fs);
        else d[fs.UrlString] = fs;
    }

    private static bool MightBeFeed(string url)
    {
        if (SpecialCases.UrlStringMatchesDomain(url, NonFeedDomains)) return false;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.Path, UriFormat.Unescaped) : url;
        if (NonFeedExtensions.Contains(Path.GetExtension(path))) return false;
        var massaged = url.Replace("buzzfeed", "_", StringComparison.OrdinalIgnoreCase);
        foreach (var w in WordsToMatch)
            if (massaged.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
