using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinNewsWire.Articles;
using WinNewsWire.ArticlesDatabase;
using WinNewsWire.Core;
using WinNewsWire.Parsers;
using WinNewsWire.SyncDatabase;
using WinNewsWire.ActivityLog;

namespace WinNewsWire.Account;

/// <summary>
/// Port of <c>Account</c>. Represents one account (local or remote) holding a collection of
/// feeds/folders plus an articles database. Service behavior is implemented by
/// <see cref="IAccountDelegate"/>.
/// </summary>
public sealed partial class Account : IDisplayNameProvider, IOpmlRepresentable, IDisposable, IAsyncDisposable
{
    public string AccountID { get; }
    public AccountType Type { get; }
    public AccountSettings Settings { get; }
    public FeedSettingsDatabase FeedSettingsDatabase { get; }
    private readonly Dictionary<string, FeedSettings> _feedSettingsByUrl = new(StringComparer.Ordinal);
    public string Name
    {
        get => Settings.Name ?? "";
        set
        {
            if (Name == value) return;
            Settings.Name = value;
            DisplayNameChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool IsActive
    {
        get => Settings.IsActive;
        set
        {
            if (Settings.IsActive == value) return;
            Settings.IsActive = value;
            AccountStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public IAccountDelegate Delegate { get; private set; }
    public string? CredentialUsername { get => Settings.Username; internal set => Settings.Username = value; }
    public string? Server
    {
        get => Settings.EndpointUrl?.ToString();
        internal set
        {
            if (value is not null) Settings.EndpointUrl = new Uri(value, UriKind.RelativeOrAbsolute);
        }
    }
    public ArticlesDatabaseStore Database { get; }
    public string AccountDirectory { get; }
    public List<Feed> TopLevelFeeds { get; } = new();
    public List<Folder> Folders { get; } = new();
    private int _nextFolderID;
    private ILocalAccountRefresher? _localRefresher;
    public static Func<Account, ILocalAccountRefresher>? LocalRefresherFactory { get; set; }
    internal ILocalAccountRefresher LocalRefresher => _localRefresher ??= LocalRefresherFactory?.Invoke(this) ?? new LocalAccountRefresher(this);
    public int? RefreshProcessId => _localRefresher?.ProcessId;
    public LocalRefreshStatistics? LastRefreshStatistics => _localRefresher?.LastStatistics;

    private SyncDatabaseStore? _syncDatabase;
    internal SemaphoreSlim StatusMutationGate { get; } = new(1, 1);
    /// <summary>Pending sync actions for remote accounts. Port of <c>Account.database</c>
    /// (SyncDatabase). Lazy-initialized on first access. Never created for
    /// <see cref="AccountType.OnMyMac"/>.</summary>
    public SyncDatabaseStore SyncDatabase
    {
        get
        {
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _syncDatabase ??= new SyncDatabaseStore(Path.Combine(AccountDirectory, "Sync.sqlite3"));
            }
        }
    }

    public event EventHandler? AccountStructureChanged;
    public event EventHandler? AccountStateChanged;
    public event EventHandler? DisplayNameChanged;
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
    public event EventHandler? ArticleStatusesQueued;

    public void RaiseNewArticlesDownloaded(IReadOnlyCollection<Articles.Article> newArticles)
    {
        if (newArticles.Count == 0) return;
        NewArticlesDownloaded?.Invoke(this, new NewArticlesEventArgs(newArticles));
    }

    /// <summary>
    /// For local accounts, the OPML mirror file at
    /// <c>%LocalAppData%/WinNewsWire/Accounts/&lt;id&gt;/Subscriptions.opml</c>.
    /// Re-exported whenever the account's feed/folder structure changes so
    /// users can hand the file to other readers or import it elsewhere.
    /// Null for remote accounts (their state lives on the server).
    /// </summary>
    public OpmlFile? OpmlFile { get; }
    private int _isOpmlImportInProgress;
    public bool IsOpmlImportInProgress => Volatile.Read(ref _isOpmlImportInProgress) != 0;
    public bool SupportsOpmlImport => Delegate.SupportsOpmlImport && !Type.Has(AccountBehavior.DisallowOpmlImports);

    public Account(string accountID, AccountType type, string name, IAccountDelegate @delegate, AccountSettings? settings = null)
        : this(accountID, type, name, @delegate, settings, null)
    {
    }

    internal Account(string accountID, AccountType type, string name, IAccountDelegate @delegate,
        AccountSettings? settings, string? articlesDatabasePath)
    {
        AccountID = accountID;
        Type = type;
        Delegate = @delegate;
        AccountDirectory = Path.Combine(AppConfig.AccountsDirectory, accountID);
        Directory.CreateDirectory(AccountDirectory);
        Settings = settings ?? new AccountSettings(accountID, AccountDirectory);
        Settings.ImportWindowsMetadata(null, true, @delegate.CredentialUsername, @delegate.Server);
        FeedSettingsDatabase = new FeedSettingsDatabase(Path.Combine(AccountDirectory, "FeedSettings.db"));
        try
        {
            FeedSettingsImporter.ImportIfNeeded(AccountDirectory, FeedSettingsDatabase);
            foreach (var (url, row) in FeedSettingsDatabase.AllRows())
                _feedSettingsByUrl[url] = new FeedSettings(url, row, FeedSettingsDatabase);
            Database = new ArticlesDatabaseStore(articlesDatabasePath ?? Path.Combine(AccountDirectory, "DB.sqlite3"), accountID);
            bool loadedSettings = LoadFromDisk();
            Settings.Name ??= name;
            Database.RemapFeedIDs(_feedIDRedirects);

            if (type == AccountType.OnMyMac)
            {
                OpmlFile = new OpmlFile(Path.Combine(AccountDirectory, "Subscriptions.opml"), this);
                // A failed OPML write must not roll back a newer, durable settings snapshot.
                if (loadedSettings && File.Exists(OpmlFile.FilePath)
                    && File.GetLastWriteTimeUtc(SettingsPath) > File.GetLastWriteTimeUtc(OpmlFile.FilePath))
                {
                    try { OpmlFile.Save(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        System.Diagnostics.Trace.TraceError($"Loaded newer account settings but could not repair OPML: {ex}");
                    }
                }
                else
                {
                    OpmlFile.Load();
                }
            }
        }
        catch
        {
            OpmlFile?.Dispose();
            Database?.Dispose();
            FeedSettingsDatabase.Dispose();
            throw;
        }
    }

    public string NameForDisplay => Name;

    public async Task ImportOpmlAsync(ParserData data, CancellationToken ct = default)
    {
        if (!IsActive) throw new InvalidOperationException("Activate this account before importing subscriptions.");
        if (!SupportsOpmlImport) throw new NotSupportedException($"{NameForDisplay} does not support OPML imports.");
        ct.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _isOpmlImportInProgress, 1, 0) != 0)
            throw new InvalidOperationException("An OPML import is already in progress for this account.");
        try
        {
            _ = OpmlParser.Parse(data);
            await PerformMutationAsync(ActivityKind.ImportOpml, token => Delegate.ImportOpmlAsync(this, data, token), ct);
            Settings.LastArticleFetchStartTime = null;
            await RefreshAllAsync(ct: ct);
        }
        finally
        {
            Interlocked.Exchange(ref _isOpmlImportInProgress, 0);
        }
    }

    public IEnumerable<Feed> FlattenedFeeds()
        => TopLevelFeeds.Concat(Folders.SelectMany(folder => folder.Feeds)).Distinct();

    public Feed? ExistingFeedWithUrl(string url)
        => FlattenedFeeds().FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.Ordinal));

    public Folder? ExistingFolderWithName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return Folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Materializes a parsed OPML outline tree into this account's
    /// feeds + folders. Existing feeds (matched by URL) and folders (matched
    /// by name) are preserved; new items are added; nothing is deleted.
    /// Runs inside <see cref="PerformBatchUpdate"/> so consumers see a single
    /// structure-change notification.
    /// </summary>
    /// <remarks>
    /// Items are run through <c>OPMLNormalizer</c> first so duplicate URLs
    /// are dropped and unnamed folders are flattened — matches the
    /// <c>BatchUpdate.shared.perform { account.loadOPMLItems(...) }</c> flow
    /// in NetNewsWire's macOS app.
    /// </remarks>
    public void LoadOpmlItems(IReadOnlyList<OpmlItem> items, bool isManualImport = true)
        => ApplyOpmlItems(items, isManualImport, replaceStructure: false);

    internal void RestoreOpmlItems(IReadOnlyList<OpmlItem> items)
        => ApplyOpmlItems(items, isManualImport: false, replaceStructure: true);

    private void ApplyOpmlItems(IReadOnlyList<OpmlItem> items, bool isManualImport, bool replaceStructure)
    {
        var normalized = OPMLNormalizer.Normalize(items);
        // Create complete new settings in one transaction, not one synchronous write per property.
        var newSettings = normalized.SelectMany(item => item.FeedSpecifier is null ? item.Children : [item])
            .Select(item => item.FeedSpecifier).OfType<OpmlFeedSpecifier>()
            .DistinctBy(specifier => specifier.FeedUrl, StringComparer.Ordinal)
            .Where(specifier => !_feedSettingsByUrl.ContainsKey(specifier.FeedUrl))
            .Select(specifier => (Url: specifier.FeedUrl, Settings: new FeedSettingsDatabase.Row(specifier.FeedUrl,
                HomePageUrl: string.IsNullOrEmpty(specifier.HomePageUrl) ? null : specifier.HomePageUrl.NormalizedUrl(),
                EditedName: isManualImport ? specifier.Title : null))).ToArray();
        if (newSettings.Length > 0)
        {
            FeedSettingsDatabase.InsertRows(newSettings);
            foreach (var (url, row) in newSettings) _feedSettingsByUrl[url] = new FeedSettings(url, row, FeedSettingsDatabase);
        }
        var knownFeeds = FlattenedFeeds().GroupBy(f => f.Url, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var knownFolders = Folders.GroupBy(f => f.NameForDisplay, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _nextFolderID = Math.Max(_nextFolderID, Folders.Select(f => f.FolderID).DefaultIfEmpty(0).Max());

        void AddFeedSpecifier(OpmlFeedSpecifier specifier, List<Feed> destination)
        {
            knownFeeds.TryGetValue(specifier.FeedUrl, out var existing);
            var feed = NewFeed(specifier, isManualImport, existing);
            knownFeeds[specifier.FeedUrl] = feed;
            if (!destination.Contains(feed)) destination.Add(feed);
        }

        PerformBatchUpdate(() =>
        {
            if (replaceStructure)
            {
                TopLevelFeeds.Clear();
                Folders.Clear();
            }

            foreach (var item in normalized)
            {
                if (item.FeedSpecifier is { } specifier)
                {
                    AddFeedSpecifier(specifier, TopLevelFeeds);
                    continue;
                }

                var name = item.Title!;
                var folder = ExistingFolderWithName(name);
                if (folder is null)
                {
                    folder = knownFolders.GetValueOrDefault(name) ?? new Folder(AccountID, name, ++_nextFolderID);
                    if (replaceStructure) folder.Feeds.Clear();
                    Folders.Add(folder);
                }
                if (item.Attributes.TryGetValue("nnw_externalID", out var externalID))
                    folder.ExternalID = externalID;
                foreach (var child in item.Children)
                    if (child.FeedSpecifier is { } childSpecifier)
                        AddFeedSpecifier(childSpecifier, folder.Feeds);
            }
        });
    }

    internal Feed NewFeed(OpmlFeedSpecifier specifier, bool isManualImport)
        => NewFeed(specifier, isManualImport, ExistingFeedWithUrl(specifier.FeedUrl));

    private Feed NewFeed(OpmlFeedSpecifier specifier, bool isManualImport, Feed? existing)
    {
        var feed = existing ?? CreateFeedModel(specifier.FeedUrl, null, specifier.FeedUrl);
        if (specifier.Title is { } title)
        {
            feed.Name = title;
            if (isManualImport && feed.EditedName is null) feed.EditedName = title;
        }
        if (specifier.HomePageUrl is { } homePageUrl) feed.HomePageUrl = homePageUrl;
        return feed;
    }

    public Feed AddFeed(string url, string? name = null, Folder? folder = null, string? feedID = null)
    {
        if (ExistingFeedWithUrl(url) is { } existing)
        {
            var destination = folder?.Feeds ?? TopLevelFeeds;
            if (!destination.Contains(existing))
            {
                destination.Add(existing);
                SaveAndNotifyStructure();
            }
            return existing;
        }
        var feed = CreateFeedModel(url, name, feedID ?? url);
        if (folder is null) TopLevelFeeds.Add(feed);
        else folder.Feeds.Add(feed);
        SaveAndNotifyStructure();
        return feed;
    }

    internal Feed CreateFeedModel(string url, string? name, string feedID)
    {
        if (!_feedSettingsByUrl.TryGetValue(url, out var settings))
            _feedSettingsByUrl[url] = settings = new FeedSettings(url, feedID, FeedSettingsDatabase);
        if (_feedIDRedirects.TryGetValue(settings.FeedID, out var canonicalID)) settings.FeedID = canonicalID;
        return new Feed(AccountID, settings.FeedID, url, settings) { Name = name };
    }

    public Folder AddFolder(string name)
    {
        if (ExistingFolderWithName(name) is { } existing) return existing;
        _nextFolderID = Math.Max(_nextFolderID, Folders.Select(f => f.FolderID).DefaultIfEmpty(0).Max()) + 1;
        var folder = new Folder(AccountID, name, _nextFolderID);
        Folders.Add(folder);
        SaveAndNotifyStructure();
        return folder;
    }

    public bool RemoveFeed(Feed feed)
    {
        bool removed = TopLevelFeeds.Remove(feed);
        foreach (var folder in Folders) removed |= folder.Feeds.Remove(feed);
        if (removed) SaveAndNotifyStructure();
        return removed;
    }

    public void RestoreFeed(Feed feed, Folder? folder = null)
    {
        AddFeedToContainer(feed, folder);
    }

    public void RestoreFolder(Folder folder)
    {
        if (!Folders.Contains(folder))
        {
            Folders.Add(folder);
            SaveAndNotifyStructure();
        }
    }

    public bool RemoveFolder(Folder folder)
    {
        var removed = Folders.Remove(folder);
        if (removed) SaveAndNotifyStructure();
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
        SaveAndNotifyStructure();
        return true;
    }

    public async Task<Feed?> CreateFeedAsync(string urlOrSite, string? name = null, Folder? folder = null, CancellationToken ct = default)
    {
        return await PerformMutationAsync(ActivityKind.SubscribeFeed, token =>
        {
            ValidateDestination(folder);
            ArgumentException.ThrowIfNullOrWhiteSpace(urlOrSite);
            return Delegate.CreateFeedAsync(this, urlOrSite, name, folder, token);
        }, ct);
    }

    public Task RefreshAllAsync(IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
        => RunOperationAsync(async token =>
        {
            using var combinedProgress = CombinedRefreshProgress.Shared.BeginRefresh(AccountID, progress);
            await Database.RemapFeedIDsAsync(_feedIDRedirects);
            await Delegate.RefreshAllAsync(this, combinedProgress, token);
        }, ct);

    /// <summary>
    /// Runs <paramref name="action"/> with disk-save and structure-change
    /// notifications suppressed; flushes a single save + event at the end.
    /// Mirrors NetNewsWire's <c>BatchUpdate</c> + <c>account.loadOPMLItems(...)</c>
    /// pattern used during OPML import or any bulk add/remove session.
    /// </summary>
    public void PerformBatchUpdate(Action action)
    {
        bool wasInBatch = _batchDepth > 0;
        _batchDepth++;
        try { action(); }
        finally
        {
            _batchDepth--;
            if (_batchDepth == 0 && !wasInBatch)
            {
                SaveToDisk();
                AccountStructureChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private int _batchDepth;
    internal bool IsPerformingBatchUpdate => _batchDepth > 0;

    private void SaveAndNotifyStructure()
    {
        if (_batchDepth > 0) return;
        SaveToDisk();
        AccountStructureChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<HashSet<Articles.Article>> FetchUnreadAsync(int? limit = null)
        => Database.FetchUnreadArticlesAsync(FlattenedFeeds().Select(f => f.FeedID), limit);

    public async Task MarkAsync(IEnumerable<string> articleIDs, ArticleStatus.Key key, bool value, CancellationToken ct = default)
    {
        using var cancellation = OperationCancellation(ct);
        var ids = articleIDs as IReadOnlyCollection<string> ?? articleIDs.ToList();
        await StatusMutationGate.WaitAsync(cancellation.Token);
        try
        {
            var changed = await Database.MarkAsync(ids, key, value);
            if (Delegate.SupportsRemoteSync && changed.Count > 0)
            {
                var syncKey = SyncStatus.FromArticleStatusKey(key);
                var statuses = changed.Select(id => new SyncStatus(id, syncKey, value));
                try { await SyncDatabase.InsertStatusesAsync(statuses); }
                catch (Exception error)
                {
                    try { await Database.MarkAsync(changed, key, !value); }
                    catch (Exception rollback)
                    {
                        throw new AggregateException("The status change could not be queued or rolled back.", error, rollback);
                    }
                    throw;
                }
                Delegate.ArticleStatusesQueued();
                ArticleStatusesQueued?.Invoke(this, EventArgs.Empty);
                if (await SyncDatabase.SelectPendingCountAsync() > 100) ScheduleStatusSend();
            }
            if (changed.Count > 0)
                StatusesChanged?.Invoke(this, new StatusesChangedEventArgs(changed, key, value));
        }
        finally { StatusMutationGate.Release(); }
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
        bool NewArticleNotificationsEnabled = false, bool ReaderViewAlwaysEnabled = false,
        Dictionary<string, string>? FolderRelationship = null);
    private sealed record FolderDto(int FolderID, string? Name, List<FeedDto> Feeds, string? ExternalID = null);
    private sealed record AccountDto(string Name, List<FeedDto> TopLevelFeeds, List<FolderDto> Folders,
        Dictionary<string, string>? FeedIDRedirects = null);

    private void SaveToDisk()
    {
        FeedDto F(Feed f) => new(f.FeedID, f.Url, f.Name, f.EditedName, f.HomePageUrl, f.IconUrl, f.FaviconUrl, f.ExternalID, f.ETag, f.LastModified, f.LastCheckDate, f.ContentHash, f.ConditionalGetInfoDate, f.NewArticleNotificationsEnabled, f.ReaderViewAlwaysEnabled, f.FolderRelationship);
        var dto = new AccountDto(Name,
            TopLevelFeeds.Select(F).ToList(),
            Folders.Select(fl => new FolderDto(fl.FolderID, fl.Name, fl.Feeds.Select(F).ToList(), fl.ExternalID)).ToList(),
            _feedIDRedirects);
        FileUtilities.WriteAllTextAtomically(SettingsPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var feed in FlattenedFeeds())
        {
            feed.Settings.BindDatabase(FeedSettingsDatabase);
            feed.Settings.FeedID = feed.FeedID;
            feed.Settings.Flush();
            _feedSettingsByUrl[feed.Url] = feed.Settings;
        }
    }

    /// <summary>Public hook for UI code (e.g. the Inspector) to persist mutations made directly
    /// on <see cref="Feed"/> or <see cref="Folder"/> references (name edits, home-page edits, etc.)
    /// that bypass the add/remove helpers.</summary>
    public void SaveChanges() => SaveAndNotifyStructure();

    private bool LoadFromDisk()
    {
        if (!File.Exists(SettingsPath)) return false;
        try
        {
            var dto = JsonSerializer.Deserialize<AccountDto>(File.ReadAllText(SettingsPath));
            if (dto?.TopLevelFeeds is null || dto.Folders is null)
                throw new JsonException("Account settings do not contain a subscription structure.");
            Settings.Name ??= dto.Name;
            _feedIDRedirects.Clear();
            if (dto.FeedIDRedirects is { } redirects)
                foreach (var pair in redirects) _feedIDRedirects.Add(pair.Key, pair.Value);
            var importedRows = dto.TopLevelFeeds.Concat(dto.Folders.SelectMany(folder => folder.Feeds))
                .DistinctBy(feed => feed.Url).Where(feed => !_feedSettingsByUrl.ContainsKey(feed.Url))
                .Select(feed => (feed.Url, Settings: new FeedSettingsDatabase.Row(
                    feed.FeedID, feed.HomePageUrl, feed.IconUrl, feed.FaviconUrl, feed.EditedName, feed.ContentHash,
                    feed.NewArticleNotificationsEnabled, feed.ReaderViewAlwaysEnabled,
                    ConditionalGetInfo: feed.ETag is null && feed.LastModified is null ? null : new(feed.LastModified, feed.ETag),
                    ConditionalGetInfoDate: feed.ConditionalGetInfoDate, ExternalID: feed.ExternalID,
                    FolderRelationship: feed.FolderRelationship, LastCheckDate: feed.LastCheckDate))).ToList();
            FeedSettingsDatabase.InsertRows(importedRows);
            foreach (var (url, row) in importedRows) _feedSettingsByUrl[url] = new FeedSettings(url, row, FeedSettingsDatabase);
            var feedsByID = new Dictionary<string, Feed>(StringComparer.Ordinal);
            Feed FromDto(FeedDto d)
            {
                if (feedsByID.TryGetValue(d.FeedID, out var existing)) return existing;
                var feed = CreateFeedModel(d.Url, d.Name, d.FeedID);
                feedsByID.Add(d.FeedID, feed);
                return feed;
            }
            TopLevelFeeds.Clear();
            TopLevelFeeds.AddRange(dto.TopLevelFeeds.Select(FromDto));
            Folders.Clear();
            foreach (var fl in dto.Folders)
            {
                var folder = new Folder(AccountID, fl.Name, fl.FolderID) { ExternalID = fl.ExternalID };
                folder.Feeds.AddRange(fl.Feeds.Select(FromDto));
                Folders.Add(folder);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.TraceError($"Could not load account settings {SettingsPath}: {ex}");
            return false;
        }
    }

    public string ExportOpml(string? title = null, bool allowCustomAttributes = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        sb.AppendLine("<!-- OPML generated by WinNewsWire -->");
        sb.AppendLine("""<opml version="1.1">""");
        sb.AppendLine("  <head><title>" + (title ?? NameForDisplay).EscapingSpecialXmlCharacters() + "</title></head>");
        sb.AppendLine("  <body>");
        sb.Append(OpmlString(0, allowCustomAttributes));
        sb.AppendLine("  </body>");
        sb.AppendLine("</opml>");
        return sb.ToString();
    }

    public string OpmlString(int indentLevel, bool allowCustomAttributes = false)
    {
        var sb = new StringBuilder();
        foreach (var feed in TopLevelFeeds.OrderBy(f => f.NameForDisplay, LocalizedStandardComparer.Shared)
                     .ThenBy(f => f.Url, StringComparer.Ordinal))
            sb.Append(feed.OpmlString(indentLevel + 1, allowCustomAttributes));
        foreach (var folder in Folders.OrderBy(f => f.NameForDisplay, LocalizedStandardComparer.Shared))
            sb.Append(folder.OpmlString(indentLevel + 1, allowCustomAttributes));
        return sb.ToString();
    }
}
