using System.Globalization;

namespace WinNewsWire.Parsers;

public sealed record HtmlMetadataFeedLink(string? Title, string? Type, string? Url);
public sealed record HtmlMetadataFavicon(string? Type, string? Url);
public readonly record struct HtmlImageSize(double Width, double Height);
public sealed record HtmlMetadataAppleTouchIcon(string? Rel, string? Sizes, string? Url)
{
    public HtmlImageSize Size
    {
        get
        {
            var parts = Sizes?.Split('x');
            return parts?.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
                ? new(width, height) : default;
        }
    }
}

public sealed class HtmlMetadata
{
    public string BaseUrl { get; }
    public IReadOnlyList<HtmlTag> Tags { get; }
    public IReadOnlyList<HtmlMetadataFavicon> Favicons { get; }
    public IReadOnlyList<HtmlMetadataAppleTouchIcon> AppleTouchIcons { get; }
    public IReadOnlyList<HtmlMetadataFeedLink> FeedLinks { get; }
    public HtmlOpenGraphProperties OpenGraphProperties { get; }
    public HtmlTwitterProperties TwitterProperties { get; }
    public string? FaviconLink => Favicons.FirstOrDefault()?.Url;
    public string? OpenGraphImageUrl => OpenGraphProperties.Images.FirstOrDefault()?.Url;
    public string? TwitterImageUrl => TwitterProperties.ImageUrl;

    public HtmlMetadata(string url, IReadOnlyList<HtmlTag> tags)
    {
        BaseUrl = url;
        Tags = tags;
        Favicons = ResolveFavicons(tags, url);
        AppleTouchIcons = ResolveAppleTouchIcons(tags, url);
        FeedLinks = ResolveFeedLinks(tags, url);
        OpenGraphProperties = new(tags);
        TwitterProperties = new(tags);
    }

    private static List<HtmlMetadataFavicon> ResolveFavicons(IReadOnlyList<HtmlTag> tags, string baseUrl)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<HtmlMetadataFavicon>();
        foreach (var tag in tags)
        {
            if (tag.Type != HtmlTagType.Link || string.IsNullOrEmpty(UrlFrom(tag.Attributes))) continue;
            var rel = Value(tag.Attributes, "rel")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
            if (!rel.Contains("icon", StringComparer.OrdinalIgnoreCase)) continue;
            var absolute = AbsoluteUrl(tag.Attributes, baseUrl);
            if (absolute is not null && seen.Add(absolute)) result.Add(new(Value(tag.Attributes, "type"), absolute));
        }
        return result;
    }

    private static List<HtmlMetadataAppleTouchIcon> ResolveAppleTouchIcons(IReadOnlyList<HtmlTag> tags, string baseUrl)
    {
        var result = new List<HtmlMetadataAppleTouchIcon>();
        foreach (var tag in tags)
        {
            if (tag.Type != HtmlTagType.Link) continue;
            var rel = Value(tag.Attributes, "rel");
            if (rel?.ToLowerInvariant() is not ("apple-touch-icon" or "apple-touch-icon-precomposed")) continue;
            result.Add(new(rel, Value(tag.Attributes, "sizes"), AbsoluteUrl(tag.Attributes, baseUrl)));
        }
        return result;
    }

    private static List<HtmlMetadataFeedLink> ResolveFeedLinks(IReadOnlyList<HtmlTag> tags, string baseUrl)
    {
        var result = new List<HtmlMetadataFeedLink>();
        foreach (var tag in tags)
        {
            if (tag.Type != HtmlTagType.Link || !string.Equals(Value(tag.Attributes, "rel"), "alternate", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(Value(tag.Attributes, "media")) || !string.IsNullOrEmpty(Value(tag.Attributes, "hreflang"))) continue;
            var type = Value(tag.Attributes, "type");
            if (!string.IsNullOrEmpty(type) && !IsFeedType(type)) continue;
            var url = UrlFrom(tag.Attributes);
            if (string.IsNullOrEmpty(url)) continue;
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith('/') || url.StartsWith('.') || !url.Contains(':'))) continue;
            result.Add(new(Value(tag.Attributes, "title"), type, AbsoluteUrl(tag.Attributes, baseUrl)));
        }
        return result;
    }

    private static string? Value(IReadOnlyDictionary<string, string> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var exact)) return exact;
        foreach (var (name, value) in attributes)
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }
    private static string? UrlFrom(IReadOnlyDictionary<string, string> attributes) => Value(attributes, "href") ?? Value(attributes, "src");
    private static string? AbsoluteUrl(IReadOnlyDictionary<string, string> attributes, string baseUrl)
    {
        var relative = UrlFrom(attributes);
        return !string.IsNullOrEmpty(relative) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var page)
            && Uri.TryCreate(page, relative, out var absolute) ? absolute.AbsoluteUri : null;
    }
    private static bool IsFeedType(string type)
        => type.EndsWith("/rss+xml", StringComparison.OrdinalIgnoreCase) || type.EndsWith("/atom+xml", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("/json", StringComparison.OrdinalIgnoreCase);
}

public sealed record HtmlOpenGraphImage(string? Url = null, string? SecureUrl = null, string? MimeType = null,
    double Width = 0, double Height = 0, string? AltText = null);

public sealed class HtmlOpenGraphProperties
{
    public IReadOnlyList<HtmlOpenGraphImage> Images { get; }

    public HtmlOpenGraphProperties(IReadOnlyList<HtmlTag> tags)
    {
        var images = new List<HtmlOpenGraphImage>();
        foreach (var tag in tags)
        {
            if (tag.Type != HtmlTagType.Meta || !tag.Attributes.TryGetValue("property", out var name) ||
                !name.StartsWith("og:", StringComparison.Ordinal) || !tag.Attributes.TryGetValue("content", out var content)) continue;
            if (name is not ("og:image" or "og:image:url" or "og:image:secure_url" or "og:image:type" or "og:image:width" or "og:image:height" or "og:image:alt")) continue;
            if (images.Count == 0 || name == "og:image" && images[^1].Url is not null) images.Add(new());
            images[^1] = name switch
            {
                "og:image" or "og:image:url" => images[^1] with { Url = content },
                "og:image:secure_url" => images[^1] with { SecureUrl = content },
                "og:image:type" => images[^1] with { MimeType = content },
                "og:image:width" => images[^1] with { Width = Number(content) },
                "og:image:height" => images[^1] with { Height = Number(content) },
                "og:image:alt" => images[^1] with { AltText = content },
                _ => images[^1]
            };
        }
        Images = images;
    }
    private static double Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
}

public sealed class HtmlTwitterProperties
{
    public string? ImageUrl { get; }
    public HtmlTwitterProperties(IReadOnlyList<HtmlTag> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Type != HtmlTagType.Meta || !tag.Attributes.TryGetValue("name", out var name) || name != "twitter:image:src"
                || !tag.Attributes.TryGetValue("content", out var content) || content.Length == 0) continue;
            ImageUrl = content;
            break;
        }
    }
}
