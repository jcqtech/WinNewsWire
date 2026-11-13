using System.Globalization;
using WinNewsWire.Parsers.Xml;

namespace WinNewsWire.Parsers;

internal static class MediaRss
{
    public static ParsedAttachment? Attachment(string url, XmlAttributes attributes)
    {
        var mediumText = attributes.ValueForNameCaseInsensitive("medium")?.Trim();
        var medium = mediumText?.ToLowerInvariant() switch
        {
            "image" => ParsedAttachment.AttachmentMedium.Image,
            "audio" => ParsedAttachment.AttachmentMedium.Audio,
            "video" => ParsedAttachment.AttachmentMedium.Video,
            "document" => ParsedAttachment.AttachmentMedium.Document,
            "executable" => ParsedAttachment.AttachmentMedium.Executable,
            _ => (ParsedAttachment.AttachmentMedium?)null
        };
        return ParsedAttachment.Create(url, attributes.ValueForNameCaseInsensitive("type"), null,
            PositiveInteger(attributes.ValueForNameCaseInsensitive("fileSize") ?? attributes.ValueForNameCaseInsensitive("length")),
            DurationInSeconds(attributes.ValueForNameCaseInsensitive("duration")),
            PositiveInteger(attributes.ValueForNameCaseInsensitive("width")),
            PositiveInteger(attributes.ValueForNameCaseInsensitive("height")), medium);
    }

    public static long? DurationInSeconds(string? value)
    {
        if (value is null) return null;
        var components = value.Trim().Split(':');
        if (components.Length > 3) return null;
        double seconds = 0;
        foreach (var component in components)
        {
            if (!double.TryParse(component, NumberStyles.Float, CultureInfo.InvariantCulture, out var part)
                || !double.IsFinite(part) || part < 0) return null;
            seconds = seconds * 60 + part;
        }
        // long.MaxValue rounds up as a double, so reject that exclusive upper bound.
        return double.IsFinite(seconds) && seconds >= 1 && seconds < 9223372036854775808d
            ? (long)Math.Floor(seconds) : null;
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
            if (attributes.ValueForNameCaseInsensitive("url") is { } raw
                && resolveUrl(raw) is { } url && MediaRss.Attachment(url, attributes) is { } attachment)
            {
                if (_inGroup)
                {
                    _first ??= attachment;
                    if (string.Equals(attributes.ValueForNameCaseInsensitive("isDefault"), "true", StringComparison.OrdinalIgnoreCase))
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
                item.MediaDescriptionIsHtml = string.Equals(attributes.ValueForNameCaseInsensitive("type"), "html", StringComparison.OrdinalIgnoreCase);
                parser.BeginStoringCharacters();
            }
            ContainerDepth++;
            return false;
        }
        if (name.SequenceEqual("thumbnail"u8) && item.ImageUrl is null
            && attributes.ValueForNameCaseInsensitive("url") is { Length: > 0 } thumbnail)
            item.ImageUrl = resolveUrl(thumbnail);
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
