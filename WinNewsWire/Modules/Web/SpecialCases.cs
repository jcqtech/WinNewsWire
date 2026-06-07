namespace WinNewsWire.Web;

/// <summary>Port of <c>SpecialCases.swift</c>. Per-host special handling for rate-limited or UA-sensitive sites.</summary>
public static class SpecialCases
{
    public const string RachelByTheBayHostName = "rachelbythebay.com";
    public const string OpenRSSOrgHostName = "openrss.org";
    public const string YouTubeHostName = "youtube.com";

    public static bool ContainsSpecialCase(string urlString, ReadOnlySpan<string> hostNames)
    {
        var lower = urlString.ToLowerInvariant();
        foreach (var host in hostNames)
        {
            if (lower.Contains(host, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsOpenRSSOrgUrl(this Uri url)
        => url.Host.Contains(OpenRSSOrgHostName, StringComparison.OrdinalIgnoreCase);

    public static bool IsRachelByTheBayUrl(this Uri url)
        => url.Host.Contains(RachelByTheBayHostName, StringComparison.OrdinalIgnoreCase);

    public static bool IsYouTubeUrl(this Uri url)
        => url.Host.Contains(YouTubeHostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Captive-portal redirect detection. Hotels and similar often do permanent redirects.</summary>
    private static readonly string[] DisallowedRedirectSubstrings =
    [
        "solutionip", "lodgenet", "monzoon", "landingpage",
        "btopenzone", "register", "login", "authentic"
    ];

    public static bool IsDisallowedRedirect(string urlString)
    {
        var lower = urlString.ToLowerInvariant();
        foreach (var bad in DisallowedRedirectSubstrings)
        {
            if (lower.Contains(bad, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Adds an extended user agent for sites that need it (rachelbythebay.com, openrss.org).
    /// </summary>
    public static void AddSpecialCaseUserAgentIfNeeded(HttpRequestMessage request)
    {
        if (request.RequestUri is null) return;
        if (request.RequestUri.IsOpenRSSOrgUrl() || request.RequestUri.IsRachelByTheBayUrl())
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.ExtendedValue);
        }
    }

    /// <summary>Filter OpenRSS.org URLs: keep at most one random one per session.</summary>
    public static HashSet<Uri> FilterOpenRSSUrls(IReadOnlySet<Uri> urls)
    {
        var nonOpenRSS = new HashSet<Uri>();
        var openRSSUrls = new List<Uri>();

        foreach (var url in urls)
        {
            if (url.IsOpenRSSOrgUrl())
                openRSSUrls.Add(url);
            else
                nonOpenRSS.Add(url);
        }

        if (openRSSUrls.Count > 0)
        {
            var idx = Random.Shared.Next(openRSSUrls.Count);
            nonOpenRSS.Add(openRSSUrls[idx]);
        }

        return nonOpenRSS;
    }
}
