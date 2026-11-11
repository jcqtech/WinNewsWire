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
    public HashSet<ParsedAttachment> Attachments { get; } = [];

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
            imageUrl: null, bannerImageUrl: null, datePublished: DatePublished, dateModified: DateModified,
            authors: Authors.Count == 0 ? null : Authors, tags: null,
            attachments: Attachments.Count == 0 ? null : Attachments)
        {
            LegacyUniqueId = legacyUniqueId == UniqueId ? null : legacyUniqueId
        };
    }
}
