namespace WinNewsWire.AppShared.ArticleRendering;

public static class ArticleImageResource
{
    public const string ResourcePath = "/article-image";
    public const string BaseUrl = "https://winnewswire.local" + ResourcePath;

    public static string Url(string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var image) || image.Scheme is not ("http" or "https"))
            throw new ArgumentException("An absolute HTTP image URL is required.", nameof(imageUrl));
        return BaseUrl + "?u=" + Uri.EscapeDataString(imageUrl);
    }

    public static bool TryGetImageUrl(Uri requestUrl, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? imageUrl)
    {
        imageUrl = null;
        if (!requestUrl.IsAbsoluteUri || requestUrl.GetLeftPart(UriPartial.Path) != BaseUrl
            || requestUrl.Fragment.Length != 0 || !requestUrl.Query.StartsWith("?u=", StringComparison.Ordinal)
            || requestUrl.Query.Contains('&')) return false;
        string decoded = Uri.UnescapeDataString(requestUrl.Query[3..]);
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var original) || original.Scheme is not ("http" or "https")) return false;
        imageUrl = decoded;
        return true;
    }
}
