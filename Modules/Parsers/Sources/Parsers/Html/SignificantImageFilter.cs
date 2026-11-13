namespace WinNewsWire.Parsers;

public static class SignificantImageFilter
{
    public const int MinimumDimension = 64;
    public const double MaximumAspectRatio = 4;

    private static readonly (string Host, string Path)[] BlockedImagePrefixes =
    [
        ("addtoany.com", "/"), ("feedburner.com", "/"), ("feeds.wordpress.com", "/"),
        ("pixel.wp.com", "/"), ("s.w.org", "/images/core/emoji/"),
        ("s0.wp.com", "/i/blank.jpg"), ("stats.wordpress.com", "/")
    ];

    private static readonly (string Host, string Path)[] SharingLinkPrefixes =
    [
        ("addtoany.com", "/"), ("bsky.app", "/intent/"), ("digg.com", "/submit"),
        ("facebook.com", "/dialog/"), ("facebook.com", "/share"), ("flipboard.com", "/bookmarklet"),
        ("getpocket.com", "/save"), ("linkedin.com", "/cws/share"), ("linkedin.com", "/sharearticle"),
        ("linkedin.com", "/sharing/"), ("news.ycombinator.com", "/submitlink"), ("pinterest.com", "/pin/create"),
        ("reddit.com", "/submit"), ("shareaholic.com", "/"), ("sharethis.com", "/"), ("t.me", "/share"),
        ("threads.net", "/intent/"), ("tumblr.com", "/share"), ("tumblr.com", "/widgets/share"),
        ("twitter.com", "/intent/"), ("twitter.com", "/share"), ("whatsapp.com", "/send"),
        ("x.com", "/intent/"), ("x.com", "/share")
    ];

    public static bool IsSignificant(string url, int? width, int? height,
        string? htmlClass = null, string? enclosingLinkUrl = null)
        => HasSignificantDimensions(width, height) && !IsBlockedUrl(url)
            && !HasDecorativeClass(htmlClass) && !IsSharingLink(enclosingLinkUrl);

    public static bool HasSignificantDimensions(int? width, int? height)
    {
        if (width < MinimumDimension || height < MinimumDimension) return false;
        return width is not { } w || height is not { } h || (double)Math.Max(w, h) / Math.Min(w, h) <= MaximumAspectRatio;
    }

    private static bool IsBlockedUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(url.Host)) return true;
        return Path.GetExtension(url.AbsolutePath).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || Matches(url, BlockedImagePrefixes) || MediaRss.IsGravatar(url);
    }

    private static bool IsSharingLink(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var url)
            && (url.Scheme == "mailto" || Matches(url, SharingLinkPrefixes));

    private static bool Matches(Uri url, (string Host, string Path)[] prefixes)
        => prefixes.Any(prefix =>
            (url.Host.Equals(prefix.Host, StringComparison.OrdinalIgnoreCase)
                || url.Host.EndsWith("." + prefix.Host, StringComparison.OrdinalIgnoreCase))
            && url.AbsolutePath.StartsWith(prefix.Path, StringComparison.OrdinalIgnoreCase));

    private static bool HasDecorativeClass(string? value)
        => value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(name => name.Equals("emoji", StringComparison.OrdinalIgnoreCase)
                || name.Equals("wp-smiley", StringComparison.OrdinalIgnoreCase)) == true;
}
