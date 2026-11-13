using System.Globalization;
using System.Text.RegularExpressions;
using WinNewsWire.Articles;
using WinNewsWire.Parsers;

namespace WinNewsWire.AppShared.ArticleRendering;

public static partial class ArticleRenderer
{
    private static readonly Regex WordPressImageSize = new(@"-\d+x\d+(?=\.[A-Za-z0-9]+$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string EnclosuresHtml(IEnumerable<Attachment>? attachments, string body, IReadOnlySet<string> bodyImageIdentities,
        string? posterUrl, string baseUrl)
    {
        var enclosures = new List<string>();
        foreach (var attachment in attachments ?? [])
        {
            if (BodyContainsMedia(body, attachment.URL, bodyImageIdentities, baseUrl)
                || EnclosureHtml(attachment, posterUrl, baseUrl) is not { } html) continue;
            enclosures.Add(html);
        }
        return enclosures.Count == 0 ? "" : "<div class=\"nnw-enclosures\">\n" + string.Join("\n", enclosures) + "\n</div>";
    }

    private static string? EnclosureHtml(Attachment attachment, string? posterUrl, string baseUrl)
    {
        if (MediaUrl(attachment.URL, baseUrl) is not { } url) return null;
        var src = Html(url);
        var dimensions = DimensionAttributes(attachment);
        if (ArticleRenderingSpecialCases.YouTubeEmbedUrl(url) is { } youtube)
            return "<div class=\"nnw-enclosure nnw-enclosure-video\"><iframe src=\"" + Html(youtube) + "\"" + dimensions
                + " allowfullscreen></iframe></div>";
        var medium = attachment.DisplayMedium;
        string media;
        string? caption = attachment.Title;
        switch (medium)
        {
            case Attachment.AttachmentMedium.Audio:
                media = "<audio controls preload=\"none\" src=\"" + src + "\"></audio>";
                caption = AudioCaption(attachment, url);
                break;
            case Attachment.AttachmentMedium.Video:
                var poster = MediaUrl(posterUrl, baseUrl) is { } image ? " poster=\"" + Html(image) + "\"" : "";
                media = "<video controls preload=\"metadata\" playsinline src=\"" + src + "\"" + dimensions + poster + "></video>";
                break;
            case Attachment.AttachmentMedium.Image:
                media = "<img src=\"" + src + "\" alt=\"\"" + dimensions + ">";
                break;
            default:
                return null;
        }
        return "<div class=\"nnw-enclosure nnw-enclosure-" + medium.Value.ToString().ToLowerInvariant() + "\">" + media
            + (string.IsNullOrEmpty(caption) ? "" : "<div class=\"nnw-enclosure-caption\">" + Html(caption) + "</div>") + "</div>";
    }

    private static string? MediaUrl(string? value, string baseUrl)
    {
        if (string.IsNullOrEmpty(value)) return null;
        Uri? url;
        if (!(Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, value, out url))
            && !Uri.TryCreate(value, UriKind.Absolute, out url)) return null;
        return url.Scheme is "http" or "https" ? url.AbsoluteUri : null;
    }

    private static string DimensionAttributes(Attachment attachment)
        => attachment.Width is > 0 && attachment.Height is > 0
            ? $" width=\"{attachment.Width.Value.ToString(CultureInfo.InvariantCulture)}\" height=\"{attachment.Height.Value.ToString(CultureInfo.InvariantCulture)}\"" : "";

    private static HashSet<string> BodyImageIdentities(string body, string baseUrl)
        => HtmlImageParser.SignificantImageUrlStrings(body,
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) ? url : null, int.MaxValue)
            .Select(ImageIdentity).OfType<string>().ToHashSet(StringComparer.Ordinal);

    private static bool BodyContainsMedia(string body, string? url, IReadOnlySet<string> imageIdentities, string baseUrl)
    {
        var resolved = MediaUrl(url, baseUrl);
        return BodyContainsUrl(body, url) || BodyContainsUrl(body, resolved)
            || (ImageIdentity(resolved) is { } identity && imageIdentities.Contains(identity));
    }

    private static string? ImageIdentity(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) return null;
        var components = Uri.UnescapeDataString(url.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0) return null;
        components[^1] = WordPressImageSize.Replace(components[^1], "");
        return string.Join("/", components.TakeLast(2)).ToLowerInvariant();
    }

    private static bool BodyContainsUrl(string body, string? url)
        => !string.IsNullOrEmpty(url) && (body.Contains(url, StringComparison.Ordinal) || body.Contains(Html(url), StringComparison.Ordinal));

    private static string AudioCaption(Attachment attachment, string url)
    {
        var parts = new List<string>();
        var title = string.IsNullOrEmpty(attachment.Title) ? Uri.UnescapeDataString(new Uri(url).Segments.Last()) : attachment.Title;
        if (!string.IsNullOrEmpty(title) && title != "/") parts.Add(title);
        if (attachment.DurationInSeconds is >= 0 and { } duration)
            parts.Add(duration >= 3600 ? $"{duration / 3600}:{duration % 3600 / 60:00}:{duration % 60:00}" : $"{duration / 60}:{duration % 60:00}");
        if (attachment.SizeInBytes is >= 0 and { } bytes)
        {
            double size = bytes;
            string[] units = ["B", "kB", "MB", "GB", "TB", "PB", "EB"];
            int unit = 0;
            while (size >= 1000 && unit < units.Length - 1) { size /= 1000; unit++; }
            parts.Add(size.ToString("0.#", CultureInfo.CurrentCulture) + " " + units[unit]);
        }
        return string.Join(" \u00b7 ", parts);
    }
}
