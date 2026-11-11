using WinNewsWire.Core;

namespace WinNewsWire.Account;

/// <summary>Port of <c>Feed</c> with per-URL, SQLite-backed settings.</summary>
public sealed class Feed : IDisplayNameProvider, IOpmlRepresentable
{
    public string AccountID { get; }
    public string FeedID { get; }
    public SidebarItemIdentifier SidebarItemID => SidebarItemIdentifier.Feed(AccountID, FeedID);
    public ReadFilterType DefaultReadFilterType => ReadFilterType.None;
    public string Url { get; set; }
    public FeedSettings Settings { get; }
    public string? HomePageUrl { get => Settings.HomePageUrl; set => Settings.HomePageUrl = string.IsNullOrEmpty(value) ? null : value.NormalizedUrl(); }
    public string? IconUrl { get => Settings.IconUrl; set => Settings.IconUrl = value; }
    public string? FaviconUrl { get => Settings.FaviconUrl; set => Settings.FaviconUrl = value; }
    public string? Name { get; set; }
    public string? EditedName { get => Settings.EditedName; set => Settings.EditedName = value; }
    public string? ExternalID { get => Settings.ExternalID; set => Settings.ExternalID = value; }
    public Dictionary<string, string>? FolderRelationship { get => Settings.FolderRelationship; set => Settings.FolderRelationship = value; }
    public string? ETag { get => Settings.ConditionalGetInfo?.ETag; set => SetConditionalGet(LastModified, value); }
    public string? LastModified { get => Settings.ConditionalGetInfo?.LastModified; set => SetConditionalGet(value, ETag); }
    public DateTime? ConditionalGetInfoDate { get => Settings.ConditionalGetInfoDate; set => Settings.ConditionalGetInfoDate = value; }
    public string? ContentHash { get => Settings.ContentHash; set => Settings.ContentHash = value; }
    public DateTime? LastCheckDate { get => Settings.LastCheckDate; set => Settings.LastCheckDate = value; }
    public int? LastResponseCode { get => Settings.LastResponseCode; set => Settings.LastResponseCode = value; }
    public IReadOnlySet<Articles.Author>? Authors { get => Settings.Authors; set => Settings.Authors = value; }
    public Web.CacheControlInfo? CacheControlInfo { get => Settings.CacheControlInfo; set => Settings.CacheControlInfo = value; }
    public int UnreadCount { get; set; }

    /// <summary>When true, the article notifier raises a Windows toast for each
    /// new unread article downloaded for this feed. Mirrors
    /// <c>Feed.newArticleNotificationsEnabled</c> in NetNewsWire.</summary>
    public bool NewArticleNotificationsEnabled { get => Settings.NewArticleNotificationsEnabled; set => Settings.NewArticleNotificationsEnabled = value; }

    /// <summary>When true, selecting an article from this feed automatically
    /// enters reader view (article extractor). Mirrors
    /// <c>Feed.readerViewAlwaysEnabled</c> in NetNewsWire.</summary>
    public bool ReaderViewAlwaysEnabled { get => Settings.ReaderViewAlwaysEnabled; set => Settings.ReaderViewAlwaysEnabled = value; }

    /// <summary>Localized label for the per-feed notification toggle in the sidebar
    /// context menu. Matches NetNewsWire's <c>notificationDisplayName</c> heuristic
    /// (Reddit feeds say "posts", everything else says "articles").</summary>
    public string NotificationDisplayName =>
        !string.IsNullOrEmpty(Url) && Url.Contains("www.reddit.com", StringComparison.OrdinalIgnoreCase)
            ? "Show notifications for new posts"
            : "Show notifications for new articles";

    public Feed(string accountID, string feedID, string url, FeedSettings? settings = null)
    {
        AccountID = accountID; FeedID = feedID; Url = url;
        Settings = settings ?? new FeedSettings(url, feedID);
        Settings.Attach(this);
    }

    private void SetConditionalGet(string? modified, string? etag)
        => Settings.ConditionalGetInfo = modified is null && etag is null ? null : new(modified, etag);

    internal Feed WithFeedID(string id) => new(AccountID, id, Url, Settings)
    {
        Name = Name, UnreadCount = UnreadCount,
    };

    public string NameForDisplay =>
        !string.IsNullOrEmpty(EditedName) ? EditedName! :
        !string.IsNullOrEmpty(Name) ? Name! : "Untitled";

    public event EventHandler? UnreadCountChanged;
    public event EventHandler? DisplayNameChanged;
    public void OnUnreadCountChanged() => UnreadCountChanged?.Invoke(this, EventArgs.Empty);
    public void OnDisplayNameChanged() => DisplayNameChanged?.Invoke(this, EventArgs.Empty);

    public override int GetHashCode() => HashCode.Combine(AccountID, FeedID);
    public override bool Equals(object? obj) => obj is Feed f && f.AccountID == AccountID && f.FeedID == FeedID;

    public string OpmlString(int indentLevel, bool allowCustomAttributes = false)
    {
        var name = (EditedName ?? Name ?? "").EscapingSpecialXmlCharacters();
        var homePage = (HomePageUrl ?? "").EscapingSpecialXmlCharacters();
        var url = Url.EscapingSpecialXmlCharacters();
        return new string('\t', indentLevel) +
            $"<outline text=\"{name}\" title=\"{name}\" description=\"\" type=\"rss\" version=\"RSS\" htmlUrl=\"{homePage}\" xmlUrl=\"{url}\"/>\n";
    }
}
