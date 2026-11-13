namespace WinNewsWire.Parsers;

/// <summary>
/// Port of <c>ParsedItem</c>. Equality mirrors Swift <c>hash(into:)</c>:
/// by <see cref="SyncServiceId"/> when present, else by <c>(UniqueId, FeedUrl)</c>.
/// </summary>
public sealed class ParsedItem : IEquatable<ParsedItem>
{
    public string? SyncServiceId { get; }
    public string UniqueId { get; }
    public string FeedUrl { get; }
    public string? Url { get; }
    public string? ExternalUrl { get; }
    public string? Title { get; }
    public string? Language { get; }
    public string? ContentHtml { get; }
    public string? ContentText { get; }
    public string? Markdown { get; }
    public string? Summary { get; }
    public string? ImageUrl { get; }
    public string? BannerImageUrl { get; }
    public DateTime? DatePublished { get; }
    public DateTime? DateModified { get; }
    public IReadOnlySet<ParsedAuthor>? Authors { get; }
    public IReadOnlySet<string>? Tags { get; }
    public IReadOnlyList<ParsedAttachment>? Attachments { get; }
    internal string? LegacyUniqueId { get; init; }

    public ParsedItem(
        string? syncServiceId,
        string uniqueId,
        string feedUrl,
        string? url,
        string? externalUrl,
        string? title,
        string? language,
        string? contentHtml,
        string? contentText,
        string? markdown,
        string? summary,
        string? imageUrl,
        string? bannerImageUrl,
        DateTime? datePublished,
        DateTime? dateModified,
        IReadOnlySet<ParsedAuthor>? authors,
        IReadOnlySet<string>? tags,
        IEnumerable<ParsedAttachment>? attachments)
    {
        SyncServiceId = syncServiceId;
        UniqueId = uniqueId;
        FeedUrl = feedUrl;
        Url = url;
        ExternalUrl = externalUrl;
        Title = title;
        Language = language;
        var renderedMarkdown = markdown is null ? null : WinNewsWire.Markdown.Markdown.ToHtml(markdown);
        ContentHtml = string.IsNullOrEmpty(renderedMarkdown) ? contentHtml : renderedMarkdown;
        ContentText = contentText;
        Markdown = markdown;
        Summary = summary;
        ImageUrl = imageUrl;
        BannerImageUrl = bannerImageUrl;
        DatePublished = datePublished;
        DateModified = dateModified;
        Authors = authors;
        Tags = tags;
        Attachments = attachments?.ToArray();
    }

    public bool Equals(ParsedItem? other)
    {
        if (other is null) return false;
        if (SyncServiceId is not null || other.SyncServiceId is not null)
            return SyncServiceId == other.SyncServiceId;
        return UniqueId == other.UniqueId && FeedUrl == other.FeedUrl;
    }

    public override bool Equals(object? obj) => obj is ParsedItem p && Equals(p);

    public override int GetHashCode()
    {
        if (SyncServiceId is not null)
            return SyncServiceId.GetHashCode(StringComparison.Ordinal);
        return HashCode.Combine(UniqueId, FeedUrl);
    }
}
