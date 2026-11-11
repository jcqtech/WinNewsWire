using System.Text.Json.Serialization;

namespace WinNewsWire.HTMLMetadata;

public sealed class HtmlMetadataRecord
{
    public string Url { get; }
    public IReadOnlyList<Favicon> Favicons { get; }
    public IReadOnlyList<AppleTouchIcon> AppleTouchIcons { get; }
    public IReadOnlyList<FeedLink> FeedLinks { get; }
    public IReadOnlyList<OpenGraphImage> OpenGraphImages { get; }
    public string? TwitterImageUrl { get; }

    public HtmlMetadataRecord(string url, IEnumerable<Favicon>? favicons = null, IEnumerable<AppleTouchIcon>? appleTouchIcons = null,
        IEnumerable<FeedLink>? feedLinks = null, IEnumerable<OpenGraphImage>? openGraphImages = null, string? twitterImageUrl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        Url = url;
        Favicons = Array.AsReadOnly((favicons ?? []).ToArray());
        AppleTouchIcons = Array.AsReadOnly((appleTouchIcons ?? []).ToArray());
        FeedLinks = Array.AsReadOnly((feedLinks ?? []).ToArray());
        OpenGraphImages = Array.AsReadOnly((openGraphImages ?? []).ToArray());
        TwitterImageUrl = twitterImageUrl;
    }

    public HtmlMetadataRecord(string url, Parsers.HtmlMetadata metadata)
        : this(url,
            metadata.Favicons.Select(icon => new Favicon(icon.Type, icon.Url)),
            metadata.AppleTouchIcons.Select(icon => new AppleTouchIcon(icon.Rel, icon.Sizes, icon.Size.Width, icon.Size.Height, icon.Url)),
            metadata.FeedLinks.Select(link => new FeedLink(link.Title, link.Type, link.Url)),
            metadata.OpenGraphProperties.Images.Select(image => new OpenGraphImage(
                image.Url, image.SecureUrl, image.MimeType, image.Width, image.Height, image.AltText)),
            metadata.TwitterImageUrl)
    {
    }

    public sealed record Favicon(string? Type, string? UrlString);
    public sealed record AppleTouchIcon(string? Rel, string? Sizes, double Width, double Height, string? UrlString);
    public sealed record FeedLink(string? Title, string? Type, string? UrlString);
    public sealed record OpenGraphImage(string? Url, [property: JsonPropertyName("secureURL")] string? SecureUrl,
        string? MimeType, double Width, double Height, string? AltText);

    public string? BestWebsiteIconUrl() => LargestAppleTouchIcon() ?? LargestOpenGraphImageUrl() ?? TwitterImageUrl;

    public string? LargestAppleTouchIcon()
    {
        AppleTouchIcon? best = null;
        foreach (var icon in AppleTouchIcons)
        {
            if (icon.Height > 0 && icon.Width / icon.Height > 2) continue;
            if (best is null || icon.Height > best.Height && icon.Width > best.Width) best = icon;
        }
        return best?.UrlString;
    }

    public string? LargestOpenGraphImageUrl()
    {
        OpenGraphImage? best = null;
        foreach (var image in OpenGraphImages)
        {
            if (image.Height > 0 && image.Width / image.Height > 2) continue;
            if (best is null || image.Height > best.Height && image.Width > best.Width) best = image;
        }
        var url = best?.SecureUrl ?? best?.Url;
        return url == "https://s0.wp.com/i/blank.jpg" ? null : url;
    }

    public IReadOnlyList<string> UsableFaviconUrls()
        => Favicons.Where(icon => Uri.TryCreate(icon.UrlString, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                && !string.Equals(icon.Type, "image/svg+xml", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetExtension(uri.AbsolutePath), ".svg", StringComparison.OrdinalIgnoreCase))
            .Select(icon => icon.UrlString!).ToArray();
}
