using WinNewsWire.Core;

namespace WinNewsWire.Account;

public enum AccountType
{
    OnMyMac = 1,
    CloudKit = 2,
    Feedly = 16,
    Feedbin = 17,
    NewsBlur = 19,
    FreshRSS = 20,
    Inoreader = 21,
    BazQux = 22,
    TheOldReader = 23,
}

/// <summary>Port of <c>Feed</c> (settings-bearing fields flattened).</summary>
public sealed class Feed : IDisplayNameProvider
{
    public string AccountID { get; }
    public string FeedID { get; }
    public string Url { get; set; }
    public string? HomePageUrl { get; set; }
    public string? IconUrl { get; set; }
    public string? FaviconUrl { get; set; }
    public string? Name { get; set; }
    public string? EditedName { get; set; }
    public string? ExternalID { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public DateTime? ConditionalGetInfoDate { get; set; }
    public string? ContentHash { get; set; }
    public DateTime? LastCheckDate { get; set; }
    /// <summary>Cached Cache-Control info from the last HTTP response. Not persisted to disk.</summary>
    public Web.CacheControlInfo? CacheControlInfo { get; set; }
    public int UnreadCount { get; set; }

    /// <summary>When true, the article notifier raises a Windows toast for each
    /// new unread article downloaded for this feed. Mirrors
    /// <c>Feed.newArticleNotificationsEnabled</c> in NetNewsWire.</summary>
    public bool NewArticleNotificationsEnabled { get; set; }

    /// <summary>When true, selecting an article from this feed automatically
    /// enters reader view (article extractor). Mirrors
    /// <c>Feed.readerViewAlwaysEnabled</c> in NetNewsWire.</summary>
    public bool ReaderViewAlwaysEnabled { get; set; }

    /// <summary>Localized label for the per-feed notification toggle in the sidebar
    /// context menu. Matches NetNewsWire's <c>notificationDisplayName</c> heuristic
    /// (Reddit feeds say "posts", everything else says "articles").</summary>
    public string NotificationDisplayName =>
        !string.IsNullOrEmpty(Url) && Url.Contains("www.reddit.com", StringComparison.OrdinalIgnoreCase)
            ? "Show notifications for new posts"
            : "Show notifications for new articles";

    public Feed(string accountID, string feedID, string url)
    {
        AccountID = accountID; FeedID = feedID; Url = url;
    }

    public string NameForDisplay =>
        !string.IsNullOrEmpty(EditedName) ? EditedName! :
        !string.IsNullOrEmpty(Name) ? Name! : "Untitled";

    public event EventHandler? UnreadCountChanged;
    public event EventHandler? DisplayNameChanged;
    public void OnUnreadCountChanged() => UnreadCountChanged?.Invoke(this, EventArgs.Empty);
    public void OnDisplayNameChanged() => DisplayNameChanged?.Invoke(this, EventArgs.Empty);

    public override int GetHashCode() => HashCode.Combine(AccountID, FeedID);
    public override bool Equals(object? obj) => obj is Feed f && f.AccountID == AccountID && f.FeedID == FeedID;
}

/// <summary>Port of <c>Folder</c>.</summary>
public sealed class Folder : IDisplayNameProvider
{
    public string AccountID { get; }
    public string? Name { get; set; }
    public int FolderID { get; }
    /// <summary>Remote-account identifier for this folder (e.g. a Feedly collection id).
    /// Mirrors <c>Folder.externalID</c> in the Swift source.</summary>
    public string? ExternalID { get; set; }
    public List<Feed> Feeds { get; } = new();
    public string NameForDisplay => Name ?? "Untitled Folder";

    public Folder(string accountID, string? name, int folderID)
    {
        AccountID = accountID; Name = name; FolderID = folderID;
    }
}
