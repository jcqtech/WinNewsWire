using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinNewsWire.Articles;

/// <summary>Port of <c>Attachment</c> — podcast/media attachment for an article.</summary>
public sealed class Attachment : IEquatable<Attachment>
{
    public string AttachmentID { get; }
    public string? URL { get; }
    public string? MimeType { get; }
    public string? Title { get; }
    public long? SizeInBytes { get; }
    public long? DurationInSeconds { get; }
    public long? Width { get; }
    public long? Height { get; }
    public AttachmentMedium? Medium { get; }
    public AttachmentMedium? DisplayMedium
    {
        get
        {
            if (MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true) return AttachmentMedium.Audio;
            if (MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) return AttachmentMedium.Video;
            if (MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return AttachmentMedium.Image;
            if (Medium is AttachmentMedium.Audio or AttachmentMedium.Video or AttachmentMedium.Image) return Medium;
            if (!Uri.TryCreate(URL, UriKind.Absolute, out var url)) return null;
            return Path.GetExtension(url.AbsolutePath).ToLowerInvariant() switch
            {
                ".mp3" or ".m4a" or ".aac" or ".wav" or ".ogg" or ".oga" or ".opus" or ".flac" => AttachmentMedium.Audio,
                ".mp4" or ".m4v" or ".mov" or ".webm" => AttachmentMedium.Video,
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".avif" => AttachmentMedium.Image,
                _ => null
            };
        }
    }

    private Attachment(string id, string? url, string? mimeType, string? title, long? sizeInBytes, long? durationInSeconds,
        long? width, long? height, AttachmentMedium? medium)
    {
        AttachmentID = id;
        URL = url;
        MimeType = mimeType;
        Title = title;
        SizeInBytes = sizeInBytes;
        DurationInSeconds = durationInSeconds;
        Width = width;
        Height = height;
        Medium = medium;
    }

    public static Attachment? Create(string? attachmentID, string? url, string? mimeType, string? title, long? sizeInBytes, long? durationInSeconds,
        long? width = null, long? height = null, AttachmentMedium? medium = null)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var id = attachmentID ?? DatabaseID.For((url ?? "") + (mimeType ?? ""));
        return new Attachment(id, url, mimeType, title, sizeInBytes, durationInSeconds, width, height, medium);
    }

    public override int GetHashCode() => AttachmentID.GetHashCode();
    public override bool Equals(object? obj) => obj is Attachment a && Equals(a);
    public bool Equals(Attachment? other) => other is not null
        && (AttachmentID, URL, MimeType, Title, SizeInBytes, DurationInSeconds, Width, Height, Medium)
            == (other.AttachmentID, other.URL, other.MimeType, other.Title, other.SizeInBytes, other.DurationInSeconds,
                other.Width, other.Height, other.Medium);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<AttachmentMedium>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private sealed record StoredAttachment(string? AttachmentID, string? Url, string? MimeType, string? Title,
        long? SizeInBytes, long? DurationInSeconds, long? Width, long? Height, AttachmentMedium? Medium);

    public static string? ToJson(IEnumerable<Attachment>? attachments)
        => attachments is null ? null : JsonSerializer.Serialize(attachments.Select(a => new StoredAttachment(
            a.AttachmentID, a.URL, a.MimeType, a.Title, a.SizeInBytes, a.DurationInSeconds, a.Width, a.Height, a.Medium)), JsonOptions);

    public static IReadOnlySet<Attachment>? FromJson(string? json)
    {
        if (json is null) return null;
        var stored = JsonSerializer.Deserialize<StoredAttachment[]>(json, JsonOptions)
            ?? throw new JsonException("Stored article attachments must be an array.");
        return stored.Select(a => a is null ? throw new JsonException("Stored attachment must be an object.")
            : Create(a.AttachmentID, a.Url, a.MimeType, a.Title, a.SizeInBytes, a.DurationInSeconds, a.Width, a.Height, a.Medium)
                ?? throw new JsonException("Stored attachment URL must not be empty.")).ToHashSet();
    }

    public enum AttachmentMedium
    {
        Image,
        Audio,
        Video,
        Document,
        Executable
    }
}
