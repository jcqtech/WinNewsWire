using System.Net;
using System.Globalization;
using WinNewsWire.ActivityLog;
using WinNewsWire.Parsers;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.FeedFinder;

public sealed class FeedNotFoundException : Exception
{
    public FeedNotFoundException() : base("The feed couldn't be found and can't be added.") { }
}

/// <summary>Port of <c>FeedFinder</c>. Discovers feed URLs from a site URL.</summary>
public sealed class FeedFinder
{
    internal enum FindStrategy { SpecialCase, MicroblogJson, DirectFeed, HtmlHead, Candidates }

    public static async Task<HashSet<FeedSpecifier>> FindAsync(string url, CancellationToken ct = default)
    {
        var result = await ActivityLogger.Shared.LogActivityAsync(ActivityOwner.FeedFinder, ActivityKind.FindFeed(url),
            () => PerformFindAsync(url, ct), successMessage: result => ParentCompletionMessage(result.Feeds.Count, result.Strategy)).ConfigureAwait(false);
        return result.Feeds;
    }

    private static async Task<(HashSet<FeedSpecifier> Feeds, FindStrategy Strategy)> PerformFindAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Uri.TryCreate(url, UriKind.Absolute, out var knownUrl)
            && FeedSpecifier.KnownFeedSpecifier(knownUrl) is { } known)
            return (new() { known }, FindStrategy.SpecialCase);

        var result = await DownloadAndLogAsync(url, ct).ConfigureAwait(false);

        if ((int)result.StatusCode == 404)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("micro.blog", StringComparison.OrdinalIgnoreCase))
            {
                var b = new UriBuilder(u) { Path = u.AbsolutePath + ".json" };
                return (new() { new FeedSpecifier(null, b.Uri.AbsoluteUri, FeedSpecifierSource.HtmlLink, 1) }, FindStrategy.MicroblogJson);
            }
            throw new FeedNotFoundException();
        }

        if (!result.Success || result.Data is null || result.Data.Length == 0) throw new FeedNotFoundException();

        var urlToUse = result.Url;
        var parserData = new ParserData(urlToUse, result.Data);
        if (FeedParser.CanParse(parserData))
            return (new() { new FeedSpecifier(null, urlToUse, FeedSpecifierSource.UserEntered, 1) }, FindStrategy.DirectFeed);

        if (!LooksLikeHtml(result.Data)) throw new FeedNotFoundException();

        return await FindInHtmlAsync(result.Data, urlToUse, ct).ConfigureAwait(false);
    }

    public static async Task<DownloadResult> DownloadAndLogAsync(string url, CancellationToken ct = default)
    {
        var id = ActivityLogger.Shared.CreateActivity(ActivityOwner.FeedFinder, ActivityKind.FetchFeedCandidate(url));
        ActivityLogger.Shared.DidStart(id);
        try
        {
            var response = await Downloader.Shared.DownloadAsync(url, ct: ct).ConfigureAwait(false);
            var message = FormattedStatus((int)response.StatusCode);
            if (response.Success && response.Data is { Length: > 0 })
                message += " \u00b7 " + ActivityLogger.DataSizeMessage(response.Data);
            ActivityLogger.Shared.DidComplete(id, message, durationIsSignificant: !response.ReturnedFromCache, returnedFromCache: response.ReturnedFromCache);
            return response;
        }
        catch (Exception error)
        {
            ActivityLogger.Shared.DidFail(id, error);
            throw;
        }
    }

    internal static string ParentCompletionMessage(int count, FindStrategy strategy)
    {
        if (count == 0) return strategy == FindStrategy.Candidates ? "No feeds found in candidate URLs" : "No feeds found";
        var feeds = $"{count} {(count == 1 ? "feed" : "feeds")}";
        return strategy switch
        {
            FindStrategy.SpecialCase => feeds + " (special case match)",
            FindStrategy.MicroblogJson => feeds + " via Micro.blog .json fallback",
            FindStrategy.DirectFeed => "Direct feed",
            FindStrategy.HtmlHead => feeds + " via HTML <head>",
            _ => feeds + " via candidate URLs"
        };
    }

    private static string FormattedStatus(int status)
    {
        if (status == 0) return "No status";
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        return status == 200 ? "200 OK" : $"{status} {CultureInfo.CurrentCulture.TextInfo.ToTitleCase((response.ReasonPhrase ?? "Unknown").ToLowerInvariant())}";
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

    private static async Task<(HashSet<FeedSpecifier> Feeds, FindStrategy Strategy)> FindInHtmlAsync(byte[] htmlData, string urlString, CancellationToken ct)
    {
        var parserData = new ParserData(urlString, htmlData);
        var possible = HtmlFeedFinder.FindIn(parserData);

        if (possible.Count == 0 && Uri.TryCreate(urlString, UriKind.Absolute, out var u))
        {
            possible.Add(new FeedSpecifier(null, new UriBuilder(u) { Path = u.AbsolutePath.TrimEnd('/') + "/feed/" }.Uri.AbsoluteUri, FeedSpecifierSource.HtmlLink, 1));
            possible.Add(new FeedSpecifier(null, new UriBuilder(u) { Path = u.AbsolutePath.TrimEnd('/') + "/index.xml" }.Uri.AbsoluteUri, FeedSpecifierSource.HtmlLink, 1));
        }

        var inHead = new Dictionary<string, FeedSpecifier>();
        var toDownload = new HashSet<FeedSpecifier>();
        bool anyInHead = false;
        foreach (var s in possible)
        {
            if (s.Source == FeedSpecifierSource.HtmlHead) { inHead[s.UrlString] = inHead.TryGetValue(s.UrlString, out var ex) ? ex.MergeWith(s) : s; anyInHead = true; }
            else if (!inHead.ContainsKey(s.UrlString)) toDownload.Add(s);
        }

        if (anyInHead) return (inHead.Values.ToHashSet(), FindStrategy.HtmlHead);
        if (toDownload.Count == 0) throw new FeedNotFoundException();

        var confirmed = new Dictionary<string, FeedSpecifier>();
        var tasks = toDownload.Select(async s =>
        {
            try
            {
                var r = await DownloadAndLogAsync(s.UrlString, ct).ConfigureAwait(false);
                if (r.Success && r.Data is { Length: > 0 } && FeedParser.CanParse(new ParserData(s.UrlString, r.Data)))
                    return s;
            }
            catch (Exception error) when (error is HttpRequestException or IOException
                || error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning($"A feed-discovery candidate could not be downloaded ({error.GetType().Name}).");
            }
            return null;
        }).ToList();

        foreach (var t in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (t is null) continue;
            confirmed[t.UrlString] = confirmed.TryGetValue(t.UrlString, out var ex) ? ex.MergeWith(t) : t;
        }

        ct.ThrowIfCancellationRequested();
        return (confirmed.Values.ToHashSet(), FindStrategy.Candidates);
    }
}
