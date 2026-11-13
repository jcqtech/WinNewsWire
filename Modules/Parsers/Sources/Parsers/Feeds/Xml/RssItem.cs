using System.Globalization;
using WinNewsWire.Parsers.Utilities;

namespace WinNewsWire.Parsers;

internal sealed class RssItem
{
    public string? Guid { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? Summary { get; set; }
    public string? Markdown { get; set; }
    public string? Link { get; set; }
    public string? Permalink { get; set; }
    public string? Language { get; set; }
    public DateTime? DatePublished { get; set; }
    public DateTime? DateModified { get; set; }
    public HashSet<ParsedAuthor> Authors { get; } = [];
    public List<ParsedAttachment> Attachments { get; } = [];
    public string? ImageUrl { get; set; }
    public long? ITunesDurationInSeconds { get; set; }

    public string UniqueId
    {
        get
        {
            if (!string.IsNullOrEmpty(Guid)) return Guid;
            var date = DatePublished is { } published
                ? (published.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)
                : null;
            var value = (Permalink.NilIfEmptyOrWhitespace() ?? Link.NilIfEmptyOrWhitespace() ?? Title.NilIfEmptyOrWhitespace());
            if (date is not null) value = (value ?? "") + date;
            else value ??= Body ?? "";
            return Md5Hash.Hex(value);
        }
    }

    public ParsedItem ToParsedItem(string feedUrl, string? legacyUniqueId = null)
    {
        var contentHtml = Body;
        var summary = Summary;
        if (string.IsNullOrEmpty(contentHtml) && !string.IsNullOrEmpty(summary))
        {
            contentHtml = summary;
            summary = null;
        }
        return new ParsedItem(
            syncServiceId: null, uniqueId: UniqueId, feedUrl: feedUrl,
            url: Permalink, externalUrl: Link, title: Title, language: Language,
            contentHtml: contentHtml, contentText: null, markdown: Markdown, summary: summary,
            imageUrl: ImageUrl, bannerImageUrl: null, datePublished: DatePublished, dateModified: DateModified,
            authors: Authors.Count == 0 ? null : Authors, tags: null,
            attachments: MergedAttachments())
        {
            LegacyUniqueId = legacyUniqueId == UniqueId ? null : legacyUniqueId
        };
    }

    private IReadOnlySet<ParsedAttachment>? MergedAttachments()
    {
        if (Attachments.Count == 0) return null;
        var byUrl = new Dictionary<string, ParsedAttachment>(StringComparer.Ordinal);
        foreach (var attachment in Attachments)
        {
            byUrl[attachment.Url] = byUrl.TryGetValue(attachment.Url, out var existing)
                ? existing with
                {
                    MimeType = existing.MimeType ?? attachment.MimeType,
                    Title = existing.Title ?? attachment.Title,
                    SizeInBytes = existing.SizeInBytes ?? attachment.SizeInBytes,
                    DurationInSeconds = existing.DurationInSeconds ?? attachment.DurationInSeconds,
                    Width = existing.Width ?? attachment.Width,
                    Height = existing.Height ?? attachment.Height,
                    Medium = existing.Medium ?? attachment.Medium
                } : attachment;
        }
        return byUrl.Values.Select(attachment => attachment.DurationInSeconds is null
            && ITunesDurationInSeconds is { } duration
            && (attachment.Medium is ParsedAttachment.AttachmentMedium.Audio or ParsedAttachment.AttachmentMedium.Video
                || attachment.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
                || attachment.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
                ? attachment with { DurationInSeconds = duration } : attachment).ToHashSet();
    }
}
