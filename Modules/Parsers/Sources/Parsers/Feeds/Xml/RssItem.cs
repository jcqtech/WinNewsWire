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
    public string? ITunesImageUrl { get; set; }
    public long? ITunesDurationInSeconds { get; set; }
    public string? MediaDescription { get; set; }
    public bool MediaDescriptionIsHtml { get; set; }
    public Dictionary<string, string> GravatarTitlesByUrl { get; } = new(StringComparer.Ordinal);

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
        string? contentText = null;
        if (string.IsNullOrEmpty(contentHtml) && MediaDescription is { } description)
        {
            if (MediaDescriptionIsHtml) contentHtml = description;
            else contentText = description;
        }
        var avatarUrls = new HashSet<string>(StringComparer.Ordinal);
        var authors = Authors.Select(author =>
        {
            var avatar = author.AvatarUrl is null && author.Name is { } name
                ? GravatarTitlesByUrl.FirstOrDefault(pair => pair.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Key : null;
            if (avatar is null) return author;
            avatarUrls.Add(avatar);
            return author with { AvatarUrl = avatar };
        }).ToHashSet();
        return new ParsedItem(
            syncServiceId: null, uniqueId: UniqueId, feedUrl: feedUrl,
            url: Permalink, externalUrl: Link, title: Title, language: Language,
            contentHtml: contentHtml, contentText: contentText, markdown: Markdown, summary: summary,
            imageUrl: ImageUrl ?? ITunesImageUrl, bannerImageUrl: null, datePublished: DatePublished, dateModified: DateModified,
            authors: authors.Count == 0 ? null : authors, tags: null,
            attachments: MergedAttachments(avatarUrls))
        {
            LegacyUniqueId = legacyUniqueId == UniqueId ? null : legacyUniqueId
        };
    }

    private IReadOnlyList<ParsedAttachment>? MergedAttachments(IReadOnlySet<string> avatarUrls)
    {
        if (Attachments.Count == 0) return null;
        var merged = new List<ParsedAttachment>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var attachment in Attachments)
        {
            if (avatarUrls.Contains(attachment.Url)) continue;
            if (indexes.TryGetValue(attachment.Url, out var index))
            {
                var existing = merged[index];
                merged[index] = existing with
                {
                    MimeType = existing.MimeType ?? attachment.MimeType,
                    Title = existing.Title ?? attachment.Title,
                    SizeInBytes = existing.SizeInBytes ?? attachment.SizeInBytes,
                    DurationInSeconds = existing.DurationInSeconds ?? attachment.DurationInSeconds,
                    Width = existing.Width ?? attachment.Width,
                    Height = existing.Height ?? attachment.Height,
                    Medium = existing.Medium ?? attachment.Medium
                };
            }
            else
            {
                indexes[attachment.Url] = merged.Count;
                merged.Add(attachment);
            }
        }
        return merged.Count == 0 ? null : merged.Select(attachment => attachment.DurationInSeconds is null
            && ITunesDurationInSeconds is { } duration
            && (attachment.Medium is ParsedAttachment.AttachmentMedium.Audio or ParsedAttachment.AttachmentMedium.Video
                || attachment.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true
                || attachment.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
                ? attachment with { DurationInSeconds = duration } : attachment).ToArray();
    }
}
