namespace WinNewsWire.Articles;

/// <summary>Port of <c>Attachment</c> — podcast/media attachment for an article.</summary>
public sealed class Attachment : IEquatable<Attachment>
{
    public string AttachmentID { get; }
    public string? URL { get; }
    public string? MimeType { get; }
    public string? Title { get; }
    public long? SizeInBytes { get; }
    public int? DurationInSeconds { get; }

    private Attachment(string id, string? url, string? mimeType, string? title, long? sizeInBytes, int? durationInSeconds)
    {
        AttachmentID = id;
        URL = url;
        MimeType = mimeType;
        Title = title;
        SizeInBytes = sizeInBytes;
        DurationInSeconds = durationInSeconds;
    }

    public static Attachment? Create(string? attachmentID, string? url, string? mimeType, string? title, long? sizeInBytes, int? durationInSeconds)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var id = attachmentID ?? DatabaseID.For((url ?? "") + (mimeType ?? ""));
        return new Attachment(id, url, mimeType, title, sizeInBytes, durationInSeconds);
    }

    public override int GetHashCode() => AttachmentID.GetHashCode();
    public override bool Equals(object? obj) => obj is Attachment a && Equals(a);
    public bool Equals(Attachment? other) => other is not null && other.AttachmentID == AttachmentID;
}
