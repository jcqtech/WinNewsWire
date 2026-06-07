using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinNewsWire.Articles;
using WinNewsWire.ArticlesDatabase;
using WinNewsWire.Core;
using WinNewsWire.SyncDatabase;

namespace WinNewsWire.Account;

/// <summary>
/// Port of <c>Account</c>. Represents one account (local or remote) holding a collection of
/// feeds/folders plus an articles database. Only LocalAccount is implemented; other account
/// types can implement <see cref="IAccountDelegate"/>.
/// </summary>
public sealed class Account : IDisplayNameProvider, IDisposable
{
    public string AccountID { get; }
    public AccountType Type { get; }
    public string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public IAccountDelegate Delegate { get; }
    public ArticlesDatabaseStore Database { get; }
    public string AccountDirectory { get; }
    public List<Feed> TopLevelFeeds { get; } = new();
    public List<Folder> Folders { get; } = new();

    private SyncDatabaseStore? _syncDatabase;
    /// <summary>Pending sync actions for remote accounts. Port of <c>Account.database</c>
    /// (SyncDatabase). Lazy-initialized on first access. Never created for
    /// <see cref="AccountType.OnMyMac"/>.</summary>
    public SyncDatabaseStore SyncDatabase => _syncDatabase ??=
        new SyncDatabaseStore(Path.Combine(AccountDirectory, "Sync.sqlite3"));

    public event EventHandler? AccountStructureChanged;
    public event EventHandler? UnreadCountChanged;

    /// <summary>Raised after <see cref="LocalAccountRefresher"/> (or another delegate)
    /// inserts new articles for a feed. Mirrors NetNewsWire's
    /// <c>AccountDidDownloadArticles</c> notification, used by
    /// <see cref="WinNewsWire.Services.NewArticleNotifier"/> to fire toasts.</summary>
    public event EventHandler<NewArticlesEventArgs>? NewArticlesDownloaded;

    /// <summary>Raised after <see cref="MarkAsync"/> mutates a batch of statuses.
    /// Mirrors NetNewsWire's <c>StatusesDidChange</c> notification — used to dismiss
    /// pending toasts for articles that have just been marked read.</summary>
    public event EventHandler<StatusesChangedEventArgs>? StatusesChanged;

    public void RaiseNewArticlesDownloaded(IReadOnlyCollection<Articles.Article> newArticles)
    {
        if (newArticles.Count == 0) return;
        NewArticlesDownloaded?.Invoke(this, new NewArticlesEventArgs(newArticles));
    }

    public Account(string accountID, AccountType type, string name, IAccountDelegate @delegate)
    {
        AccountID = accountID;
        Type = type;
        Name = name;
        Delegate = @delegate;
        AccountDirectory = Path.Combine(AppConfig.AccountsDirectory, accountID);
        Directory.CreateDirectory(AccountDirectory);
        Database = new ArticlesDatabaseStore(Path.Combine(AccountDirectory, "DB.sqlite3"), accountID);
        LoadFromDisk();
    }

    public string NameForDisplay => Name;

    public IEnumerable<Feed> FlattenedFeeds()
    {
        foreach (var f in TopLevelFeeds) yield return f;
        foreach (var folder in Folders)
            foreach (var f in folder.Feeds) yield return f;
    }

    public Feed? ExistingFeedWithUrl(string url)
        => FlattenedFeeds().FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));

    public Feed AddFeed(string url, string? name = null, Folder? folder = null)
    {
        if (ExistingFeedWithUrl(url) is { } existing) return existing;
        var feedID = url;
        var feed = new Feed(AccountID, feedID, url) { Name = name };
        if (folder is null) TopLevelFeeds.Add(feed);
        else folder.Feeds.Add(feed);
        SaveToDisk();
        AccountStructureChanged?.Invoke(this, EventArgs.Empty);
        return feed;
    }

    public Folder AddFolder(string name)
    {
        var folder = new Folder(AccountID, name, Folders.Count + 1);
        Folders.Add(folder);
        SaveToDisk();
        AccountStructureChanged?.Invoke(this, EventArgs.Empty);
        return folder;
    }

    public bool RemoveFeed(Feed feed)
    {
        bool removed = TopLevelFeeds.Remove(feed);
        foreach (var folder in Folders) removed |= folder.Feeds.Remove(feed);
        if (removed) { SaveToDisk(); AccountStructureChanged?.Invoke(this, EventArgs.Empty); }
        return removed;
    }

    public void RestoreFeed(Feed feed, Folder? folder = null)
    {
        if (folder is null) TopLevelFeeds.Add(feed); else folder.Feeds.Add(feed);
        SaveToDisk();
        AccountStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreFolder(Folder folder)
    {
        Folders.Add(folder);
        SaveToDisk();
        AccountStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveFolder(Folder folder)
    {
        var removed = Folders.Remove(folder);
        if (removed) { SaveToDisk(); AccountStructureChanged?.Invoke(this, EventArgs.Empty); }
        return removed;
    }

    /// <summary>Move a feed from its current container (top-level or any folder) into the
    /// specified destination (null = top level). Port of Mac <c>Account.addFeed(_:to:)</c>
    /// when invoked with an already-owned feed (drag-drop reparent).</summary>
    public bool MoveFeed(Feed feed, Folder? destination)
    {
        bool removed = TopLevelFeeds.Remove(feed);
        foreach (var folder in Folders) removed |= folder.Feeds.Remove(feed);
        if (!removed) return false;
        if (destination is null) TopLevelFeeds.Add(feed);
        else destination.Feeds.Add(feed);
        SaveToDisk();
        AccountStructureChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<Feed?> CreateFeedAsync(string urlOrSite, string? name = null, Folder? folder = null, CancellationToken ct = default)
    {
        var feed = await Delegate.CreateFeedAsync(this, urlOrSite, name, folder, ct);
        if (feed is not null) { SaveToDisk(); AccountStructureChanged?.Invoke(this, EventArgs.Empty); }
        return feed;
    }

    public Task RefreshAllAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => Delegate.RefreshAllAsync(this, progress, ct);

    public Task<HashSet<Articles.Article>> FetchUnreadAsync(int? limit = null)
        => Database.FetchUnreadArticlesAsync(FlattenedFeeds().Select(f => f.FeedID), limit);

    public Task MarkAsync(IEnumerable<string> articleIDs, ArticleStatus.Key key, bool value)
    {
        var ids = articleIDs as IReadOnlyCollection<string> ?? articleIDs.ToList();
        var localTask = Database.MarkAsync(ids, key, value);
        if (ids.Count > 0)
            StatusesChanged?.Invoke(this, new StatusesChangedEventArgs(ids, key, value));
        if (Delegate.SupportsRemoteSync && ids.Count > 0)
        {
            var syncKey = SyncStatus.FromArticleStatusKey(key);
            var statuses = ids.Select(id => new SyncStatus(id, syncKey, value));
            return Task.WhenAll(localTask, SyncDatabase.InsertStatusesAsync(statuses));
        }
        return localTask;
    }

    public async Task RecalculateUnreadCountsAsync()
    {
        var counts = await Database.UnreadCountsByFeedAsync(FlattenedFeeds().Select(f => f.FeedID));
        foreach (var f in FlattenedFeeds())
        {
            var c = counts.GetValueOrDefault(f.FeedID);
            if (f.UnreadCount != c) { f.UnreadCount = c; f.OnUnreadCountChanged(); }
        }
        UnreadCountChanged?.Invoke(this, EventArgs.Empty);
    }

    private string SettingsPath => Path.Combine(AccountDirectory, "Settings.json");

    private sealed record FeedDto(
        string FeedID, string Url, string? Name, string? EditedName, string? HomePageUrl, string? IconUrl,
        string? FaviconUrl, string? ExternalID, string? ETag, string? LastModified, DateTime? LastCheckDate,
        string? ContentHash, DateTime? ConditionalGetInfoDate = null,
        bool NewArticleNotificationsEnabled = false, bool ReaderViewAlwaysEnabled = false);
    private sealed record FolderDto(int FolderID, string? Name, List<FeedDto> Feeds, string? ExternalID = null);
    private sealed record AccountDto(string Name, List<FeedDto> TopLevelFeeds, List<FolderDto> Folders);

    private void SaveToDisk()
    {
        FeedDto F(Feed f) => new(f.FeedID, f.Url, f.Name, f.EditedName, f.HomePageUrl, f.IconUrl, f.FaviconUrl, f.ExternalID, f.ETag, f.LastModified, f.LastCheckDate, f.ContentHash, f.ConditionalGetInfoDate, f.NewArticleNotificationsEnabled, f.ReaderViewAlwaysEnabled);
        var dto = new AccountDto(Name,
            TopLevelFeeds.Select(F).ToList(),
            Folders.Select(fl => new FolderDto(fl.FolderID, fl.Name, fl.Feeds.Select(F).ToList(), fl.ExternalID)).ToList());
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Public hook for UI code (e.g. the Inspector) to persist mutations made directly
    /// on <see cref="Feed"/> or <see cref="Folder"/> references (name edits, home-page edits, etc.)
    /// that bypass the add/remove helpers.</summary>
    public void SaveChanges() => SaveToDisk();

    private void LoadFromDisk()
    {
        if (!File.Exists(SettingsPath)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<AccountDto>(File.ReadAllText(SettingsPath));
            if (dto is null) return;
            Name = dto.Name;
            Feed FromDto(FeedDto d) => new(AccountID, d.FeedID, d.Url)
            {
                Name = d.Name, EditedName = d.EditedName, HomePageUrl = d.HomePageUrl, IconUrl = d.IconUrl,
                FaviconUrl = d.FaviconUrl, ExternalID = d.ExternalID, ETag = d.ETag, LastModified = d.LastModified,
                LastCheckDate = d.LastCheckDate, ContentHash = d.ContentHash,
                ConditionalGetInfoDate = d.ConditionalGetInfoDate,
                NewArticleNotificationsEnabled = d.NewArticleNotificationsEnabled,
                ReaderViewAlwaysEnabled = d.ReaderViewAlwaysEnabled,
            };
            TopLevelFeeds.Clear();
            TopLevelFeeds.AddRange(dto.TopLevelFeeds.Select(FromDto));
            Folders.Clear();
            foreach (var fl in dto.Folders)
            {
                var folder = new Folder(AccountID, fl.Name, fl.FolderID) { ExternalID = fl.ExternalID };
                folder.Feeds.AddRange(fl.Feeds.Select(FromDto));
                Folders.Add(folder);
            }
        }
        catch { }
    }

    public void Dispose() { Database.Dispose(); _syncDatabase?.Dispose(); }

    // OPML import/export (simplified)
    public string ExportOpml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("""<opml version="2.0">""");
        sb.AppendLine("  <head><title>" + Esc(Name) + "</title></head>");
        sb.AppendLine("  <body>");
        foreach (var f in TopLevelFeeds) sb.AppendLine(FeedOutline(f, 2));
        foreach (var folder in Folders)
        {
            sb.AppendLine($"    <outline text=\"{Esc(folder.Name)}\" title=\"{Esc(folder.Name)}\">");
            foreach (var f in folder.Feeds) sb.AppendLine(FeedOutline(f, 3));
            sb.AppendLine("    </outline>");
        }
        sb.AppendLine("  </body>");
        sb.AppendLine("</opml>");
        return sb.ToString();
    }

    private static string FeedOutline(Feed f, int indent)
        => new string(' ', indent * 2) + $"<outline text=\"{Esc(f.NameForDisplay)}\" title=\"{Esc(f.NameForDisplay)}\" type=\"rss\" xmlUrl=\"{Esc(f.Url)}\" htmlUrl=\"{Esc(f.HomePageUrl ?? "")}\"/>";
    private static string Esc(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";
}

public sealed record ProgressInfo(int NumberRemaining, int NumberCompleted, int NumberOfTasks);
