namespace WinNewsWire.ActivityLog;

public sealed class ActivityOwner : IEquatable<ActivityOwner>
{
    private readonly string _kind;
    public string? AccountID { get; }
    public string DisplayName { get; }

    private ActivityOwner(string kind, string displayName, string? accountID = null)
    {
        _kind = kind; DisplayName = displayName; AccountID = accountID;
    }

    public static ActivityOwner App { get; } = new("app", "WinNewsWire");
    public static ActivityOwner FeedFinder { get; } = new("feedFinder", "Feed Finder");
    public static ActivityOwner FeedImageDownloader { get; } = new("feedImageDownloader", "Feed Images");
    public static ActivityOwner FaviconDownloader { get; } = new("faviconDownloader", "Favicons");
    public static ActivityOwner AvatarDownloader { get; } = new("avatarDownloader", "Avatars");
    public static ActivityOwner HtmlMetadataDownloader { get; } = new("htmlMetadataDownloader", "HTML Metadata");
    public static ActivityOwner ThumbnailDownloader { get; } = new("thumbnailDownloader", "Thumbnails");
    public static ActivityOwner Account(string accountID, string displayName) => new("account", displayName, accountID);
    public bool Equals(ActivityOwner? other) => other is not null && _kind == other._kind && AccountID == other.AccountID;
    public override bool Equals(object? obj) => obj is ActivityOwner other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_kind, AccountID);
}
