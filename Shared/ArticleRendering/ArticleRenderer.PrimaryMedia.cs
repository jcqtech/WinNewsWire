using System.Globalization;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.ArticleRendering;

public static partial class ArticleRenderer
{
    private static Attachment? PrimaryImage(string? imageUrl, string? thumbnailUrl, IReadOnlyList<Attachment> attachments,
        string body, IReadOnlySet<string> bodyImageIdentities, string baseUrl)
    {
        var image = !string.IsNullOrEmpty(imageUrl)
            ? attachments.FirstOrDefault(attachment => attachment.URL == imageUrl) ?? ImageAttachment(imageUrl)
            : attachments.FirstOrDefault(attachment => attachment.DisplayMedium == Attachment.AttachmentMedium.Image)
                ?? ImageAttachment(thumbnailUrl);
        return image is not null && MediaUrl(image.URL, baseUrl) is not null
            && !BodyContainsMedia(body, image.URL, bodyImageIdentities, baseUrl) ? image : null;
    }

    private static Attachment? ImageAttachment(string? url)
        => Attachment.Create(null, url, null, null, null, null, medium: Attachment.AttachmentMedium.Image);

    private static string PrimaryImageHtml(Attachment image, string? articleTitle, string baseUrl)
    {
        var attributes = "src=\"" + Html(MediaUrl(image.URL, baseUrl)) + "\"";
        var caption = "";
        if (!string.IsNullOrEmpty(image.Title))
        {
            var title = Html(image.Title);
            attributes += " alt=\"" + title + "\" title=\"" + title + "\"";
            if (!CollapsingWhitespace(image.Title).Equals(CollapsingWhitespace(articleTitle), StringComparison.OrdinalIgnoreCase))
                caption = "<div class=\"nnw-primary-image-caption\">" + title + "</div>";
        }
        else attributes += " alt=\"\"";
        return "<div class=\"nnw-primary-image\"><img " + attributes + DimensionAttributes(image) + ">" + caption + "</div>";
    }

    private static string CollapsingWhitespace(string? value)
        => string.Join(" ", value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? []);

    private static bool IsShortUntitledPost(string? title, string body)
    {
        if (!string.IsNullOrEmpty(title)) return false;
        var characters = StringInfo.GetTextElementEnumerator(body);
        for (var count = 0; count < 2048; count++)
            if (!characters.MoveNext()) return true;
        return false;
    }
}
