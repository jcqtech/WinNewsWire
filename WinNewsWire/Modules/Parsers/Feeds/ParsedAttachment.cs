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
    int? DurationInSeconds)
{
    public static ParsedAttachment? Create(string? url, string? mimeType, string? title, long? sizeInBytes, int? durationInSeconds)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return new ParsedAttachment(url, mimeType, title, sizeInBytes, durationInSeconds);
    }

    public override int GetHashCode() => Url.GetHashCode(StringComparison.Ordinal);
}
