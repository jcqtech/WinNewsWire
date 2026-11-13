using System.Globalization;

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

    public static long? ParseDurationInSeconds(string? value)
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

    public static AttachmentMedium? ParseMedium(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "image" => AttachmentMedium.Image,
        "audio" => AttachmentMedium.Audio,
        "video" => AttachmentMedium.Video,
        "document" => AttachmentMedium.Document,
        "executable" => AttachmentMedium.Executable,
        _ => null
    };

    public override int GetHashCode() => Url.GetHashCode(StringComparison.Ordinal);
}
