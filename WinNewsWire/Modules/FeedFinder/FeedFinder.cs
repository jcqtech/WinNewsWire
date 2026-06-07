using System.Net;
using WinNewsWire.Parsers;
using WinNewsWire.Web;

namespace WinNewsWire.FeedFinder;

public sealed class FeedNotFoundException : Exception
{
    public FeedNotFoundException() : base("The feed couldn't be found and can't be added.") { }
}

/// <summary>Port of <c>FeedFinder</c>. Discovers feed URLs from a site URL.</summary>
public static class FeedFinder
{
    public static async Task<HashSet<FeedSpecifier>> FindAsync(string url, CancellationToken ct = default)
    {
        if (TryKnown(url, out var known)) return new() { known! };

        var result = await Downloader.Shared.DownloadAsync(url, null, ct).ConfigureAwait(false);

        if ((int)result.StatusCode == 404)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("micro.blog", StringComparison.OrdinalIgnoreCase))
            {
                var b = new UriBuilder(u) { Path = u.AbsolutePath + ".json" };
                return new() { new FeedSpecifier(null, b.Uri.AbsoluteUri, FeedSpecifierSource.HtmlLink, 1) };
            }
            throw new FeedNotFoundException();
        }

        if (!result.Success || result.Data is null || result.Data.Length == 0) throw new FeedNotFoundException();

        var parserData = new ParserData(url, result.Data);
        if (FeedParser.CanParse(parserData))
            return new() { new FeedSpecifier(null, url, FeedSpecifierSource.UserEntered, 1) };

        if (!LooksLikeHtml(result.Data)) throw new FeedNotFoundException();

        return await FindInHtmlAsync(result.Data, url, ct).ConfigureAwait(false);
    }

    private static bool TryKnown(string url, out FeedSpecifier? spec)
    {
        spec = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) &&
            u.Host.Equals("rachelbythebay.com", StringComparison.OrdinalIgnoreCase))
        {
            spec = new FeedSpecifier("writing - rachelbythebay", "https://rachelbythebay.com/w/atom.xml", FeedSpecifierSource.UserEntered, 0);
            return true;
        }
        return false;
    }

    private static bool LooksLikeHtml(byte[] data)
    {
        int n = Math.Min(data.Length, 512);
        for (int i = 0; i < n - 4; i++)
        {
            if (data[i] == (byte)'<' &&
                ((data[i + 1] == (byte)'h' || data[i + 1] == (byte)'H') ||
                 (data[i + 1] == (byte)'!') ||
                 (data[i + 1] == (byte)'b' || data[i + 1] == (byte)'B')))
                return true;
        }
        return false;
    }

    private static async Task<HashSet<FeedSpecifier>> FindInHtmlAsync(byte[] htmlData, string urlString, CancellationToken ct)
    {
        var parserData = new ParserData(urlString, htmlData);
        var possible = HtmlFeedFinder.FindIn(parserData);

        if (possible.Count == 0 && Uri.TryCreate(urlString, UriKind.Absolute, out var u))
        {
            possible.Add(new FeedSpecifier(null, new Uri(u, "feed/").AbsoluteUri, FeedSpecifierSource.HtmlLink, 1));
            possible.Add(new FeedSpecifier(null, new Uri(u, "index.xml").AbsoluteUri, FeedSpecifierSource.HtmlLink, 1));
        }

        var inHead = new Dictionary<string, FeedSpecifier>();
        var toDownload = new HashSet<FeedSpecifier>();
        bool anyInHead = false;
        foreach (var s in possible)
        {
            if (s.Source == FeedSpecifierSource.HtmlHead) { inHead[s.UrlString] = inHead.TryGetValue(s.UrlString, out var ex) ? ex.MergeWith(s) : s; anyInHead = true; }
            else if (!inHead.ContainsKey(s.UrlString)) toDownload.Add(s);
        }

        if (anyInHead) return inHead.Values.ToHashSet();
        if (toDownload.Count == 0) throw new FeedNotFoundException();

        var confirmed = new Dictionary<string, FeedSpecifier>();
        var tasks = toDownload.Select(async s =>
        {
            try
            {
                var r = await Downloader.Shared.DownloadAsync(s.UrlString, null, ct).ConfigureAwait(false);
                if (r.Success && r.Data is { Length: > 0 } && FeedParser.CanParse(new ParserData(s.UrlString, r.Data)))
                    return s;
            }
            catch { }
            return null;
        }).ToList();

        foreach (var t in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (t is null) continue;
            confirmed[t.UrlString] = confirmed.TryGetValue(t.UrlString, out var ex) ? ex.MergeWith(t) : t;
        }

        if (confirmed.Count == 0) throw new FeedNotFoundException();
        return confirmed.Values.ToHashSet();
    }
}
