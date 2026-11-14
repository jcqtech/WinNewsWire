using System.Globalization;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

internal static class MediaRss
{
    public static ParsedAttachment? Attachment(string url, XmlAttributes attributes)
    {
        var medium = ParsedAttachment.ParseMedium(attributes.ValueForCaseInsensitiveName("medium"));
        return ParsedAttachment.Create(url, attributes.ValueForCaseInsensitiveName("type"), null,
            PositiveInteger(attributes.ValueForCaseInsensitiveName("fileSize") ?? attributes.ValueForCaseInsensitiveName("length")),
            ParsedAttachment.ParseDurationInSeconds(attributes.ValueForCaseInsensitiveName("duration")),
            PositiveInteger(attributes.ValueForCaseInsensitiveName("width")),
            PositiveInteger(attributes.ValueForCaseInsensitiveName("height")), medium);
    }

    private static long? PositiveInteger(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number : null;

    public static bool IsGravatar(Uri url)
        => (url.Host.Equals("gravatar.com", StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith(".gravatar.com", StringComparison.OrdinalIgnoreCase))
            && url.AbsolutePath.StartsWith("/avatar/", StringComparison.Ordinal);
}

internal sealed class MediaElementTracker
{
    public int ContainerDepth { get; private set; }
    private bool _inGroup;
    private ParsedAttachment? _first, _default;
    private string? _gravatarUrl;

    public void Reset()
    {
        ContainerDepth = 0;
        _inGroup = false;
        _first = _default = null;
        _gravatarUrl = null;
    }

    public bool Start(ReadOnlySpan<byte> name, XmlAttributes attributes, RssItem item, XmlSaxParser parser, Func<string, string?> resolveUrl)
    {
        if (name.SequenceEqual("group"u8))
        {
            _inGroup = true;
            _first = _default = null;
            ContainerDepth++;
            return false;
        }
        if (name.SequenceEqual("content"u8))
        {
            if (attributes.ValueForCaseInsensitiveName("url") is { } raw
                && resolveUrl(raw) is { } url && MediaRss.Attachment(url, attributes) is { } attachment)
            {
                if (_inGroup)
                {
                    _first ??= attachment;
                    if (string.Equals(attributes.ValueForCaseInsensitiveName("isDefault"), "true", StringComparison.OrdinalIgnoreCase))
                        _default ??= attachment;
                }
                else
                {
                    item.Attachments.Add(attachment);
                    if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && MediaRss.IsGravatar(parsed)) _gravatarUrl = url;
                }
            }
            ContainerDepth++;
            return false;
        }
        if (name.SequenceEqual("title"u8) && _gravatarUrl is not null)
        {
            parser.BeginStoringCharacters();
            ContainerDepth++;
            return false;
        }
        if (name.SequenceEqual("description"u8))
        {
            if (item.MediaDescription is null)
            {
                item.MediaDescriptionIsHtml = string.Equals(attributes.ValueForCaseInsensitiveName("type"), "html", StringComparison.OrdinalIgnoreCase);
                parser.BeginStoringCharacters();
            }
            ContainerDepth++;
            return false;
        }
        if (name.SequenceEqual("thumbnail"u8) && item.ThumbnailUrl is null
            && attributes.ValueForCaseInsensitiveName("url") is { Length: > 0 } thumbnail)
            item.ThumbnailUrl = resolveUrl(thumbnail);
        return true;
    }

    public void End(ReadOnlySpan<byte> name, RssItem item, XmlSaxParser parser)
    {
        if (name.SequenceEqual("group"u8))
        {
            if ((_default ?? _first) is { } attachment) item.Attachments.Add(attachment);
            _inGroup = false;
            _first = _default = null;
            ContainerDepth--;
        }
        else if (name.SequenceEqual("content"u8))
        {
            _gravatarUrl = null;
            ContainerDepth--;
        }
        else if (name.SequenceEqual("title"u8))
        {
            if (_gravatarUrl is { } url && parser.CurrentStringWithTrimmedWhitespace is { Length: > 0 } title)
                item.GravatarTitlesByUrl[url] = title;
            ContainerDepth--;
        }
        else if (name.SequenceEqual("description"u8))
        {
            item.MediaDescription ??= parser.CurrentStringWithTrimmedWhitespace;
            ContainerDepth--;
        }
    }
}
