namespace WinNewsWire.Parsers;

/// <summary>
/// Port of <c>ParsedAttachment</c>. Use <see cref="Create"/> to enforce the Swift
/// <c>init?</c> rule that an empty URL yields null.
/// </summary>
public sealed record ParsedAttachment(
    string Url,
    string? MimeType,
    string? Title,
    long? SizeInBytes,
    long? DurationInSeconds,
    long? Width = null,
    long? Height = null,
    ParsedAttachment.AttachmentMedium? Medium = null)
{
    public static ParsedAttachment? Create(string? url, string? mimeType, string? title, long? sizeInBytes, long? durationInSeconds,
        long? width = null, long? height = null, AttachmentMedium? medium = null)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return new ParsedAttachment(url, mimeType, title, sizeInBytes, durationInSeconds, width, height, medium);
    }

    public enum AttachmentMedium
    {
        Image,
        Audio,
        Video,
        Document,
        Executable
    }

    public override int GetHashCode() => Url.GetHashCode(StringComparison.Ordinal);
}
