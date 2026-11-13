namespace WinNewsWire.ActivityLog;

public sealed record ActivityKind
{
    private readonly string _kind;
    private readonly string? _url;
    public string Identifier => _kind;
    public string? Url => _url;
    public string? SimpleDisplayName { get; }

    private ActivityKind(string kind, string? displayName, string? url = null)
    {
        _kind = kind; SimpleDisplayName = displayName; _url = url;
    }

    public static ActivityKind SendArticleStatuses { get; } = new("sendArticleStatuses", "Sending statuses");
    public static ActivityKind RefreshArticleStatuses { get; } = new("refreshArticleStatuses", "Refreshing statuses");
    public static ActivityKind RefreshFeedList { get; } = new("refreshFeedList", "Refreshing feed list");
    public static ActivityKind FollowFeedRedirect { get; } = new("followFeedRedirect", "Feed redirect");
    public static ActivityKind RefreshArticles { get; } = new("refreshArticles", "Refreshing articles");
    public static ActivityKind FetchArticleIDs { get; } = new("fetchArticleIDs", "Fetching article IDs");
    public static ActivityKind RefreshMissingArticles { get; } = new("refreshMissingArticles", "Refreshing missing articles");
    public static ActivityKind ImportOpml { get; } = new("importOPML", "Importing OPML");
    public static ActivityKind SubscribeFeed { get; } = new("subscribeFeed", "Subscribing to feed");
    public static ActivityKind RenameFeed { get; } = new("renameFeed", "Renaming feed");
    public static ActivityKind RemoveFeed { get; } = new("removeFeed", "Removing feed");
    public static ActivityKind MoveFeed { get; } = new("moveFeed", "Moving feed");
    public static ActivityKind AddFeed { get; } = new("addFeed", "Adding feed");
    public static ActivityKind RestoreFeed { get; } = new("restoreFeed", "Restoring feed");
    public static ActivityKind CreateFolder { get; } = new("createFolder", "Creating folder");
    public static ActivityKind RenameFolder { get; } = new("renameFolder", "Renaming folder");
    public static ActivityKind RemoveFolder { get; } = new("removeFolder", "Removing folder");
    public static ActivityKind RestoreFolder { get; } = new("restoreFolder", "Restoring folder");
    public static ActivityKind CleanUpCloudKitRecords { get; } = new("cleanUpCloudKitRecords", "Cleaning up iCloud records");
    public static ActivityKind SubscribeToCloudKitZone { get; } = new("subscribeToCloudKitZone", "Subscribing to zone changes");
    public static ActivityKind FetchCloudKitStats { get; } = new("fetchCloudKitStats", "Fetching iCloud stats");
    public static ActivityKind ScanCloudKitStatusRecords { get; } = new("scanCloudKitStatusRecords", "Scanning iCloud status records");
    public static ActivityKind ScanCloudKitArticleRecords { get; } = new("scanCloudKitArticleRecords", "Scanning iCloud article records");
    public static ActivityKind ReceiveCloudKitNotification { get; } = new("receiveCloudKitNotification", "Receiving sync notification");
    public static ActivityKind VacuumDatabase { get; } = new("vacuumDatabase", "Vacuuming database");
    public static ActivityKind ValidateCredentials { get; } = new("validateCredentials", "Validating credentials");
    public static ActivityKind ExportOpml { get; } = new("exportOPML", "Exporting OPML");
    public static ActivityKind RefreshAll { get; } = new("refreshAll", "Refresh all");
    public static ActivityKind RefreshFeedContent(string feedUrl) => new("refreshFeedContent", null, feedUrl);
    public static ActivityKind FindFeed(string url) => new("findFeed", null, url);
    public static ActivityKind FetchFeedCandidate(string url) => new("fetchFeedCandidate", null, url);
    public static ActivityKind DownloadFeedImage(string url) => new("downloadFeedImage", null, url);
    public static ActivityKind DownloadFavicon(string url) => new("downloadFavicon", null, url);
    public static ActivityKind DownloadAvatar(string url) => new("downloadAvatar", null, url);
    public static ActivityKind DownloadHtmlMetadata(string url) => new("downloadHTMLMetadata", null, url);
    public static ActivityKind DownloadThumbnail(string url) => new("downloadThumbnail", null, url);

    public string DisplayName(string? detail = null) => SimpleDisplayName ?? _kind switch
    {
        "refreshFeedContent" => $"Refreshing feed: {detail ?? _url}",
        "findFeed" => $"Finding feed {_url}",
        "fetchFeedCandidate" => $"Fetching {_url}",
        "downloadFeedImage" => $"Downloading image {_url}",
        "downloadFavicon" => $"Downloading favicon {_url}",
        "downloadAvatar" => $"Downloading avatar {_url}",
        "downloadHTMLMetadata" => $"Downloading metadata {_url}",
        "downloadThumbnail" => $"Downloading thumbnail {_url}",
        _ => throw new InvalidOperationException("Unknown activity kind."),
    };
}
