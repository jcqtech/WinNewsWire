using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using WinNewsWire.ActivityLog;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.SyncDatabase;

namespace WinNewsWire.Account.OneDrive;

/// <summary>Device-owned, mergeable snapshots transported by the Windows OneDrive sync client.</summary>
public sealed class OneDriveAccountDelegate : IAccountDelegate
{
    private readonly LocalAccountDelegate _local = new();
    private OneDriveReplica? _replica;
    private bool _applying;
    private bool _startupCleanupCompleted;
    private readonly string? _directory;
    public AccountType Type => AccountType.OneDrive;
    public bool SupportsRemoteSync => true;
    public bool RequiresContinuousStatusPolling => true;
    public bool UsesFeedBasedRetention => true;
    public bool SupportsOpmlImport => true;
    public string? Server => _directory is null ? null : new Uri(_directory + Path.DirectorySeparatorChar).AbsoluteUri;
    public string? LastSyncError { get; private set; }
    public DateTime? LastExchangeUtc { get; private set; }

    public OneDriveAccountDelegate(string? directory)
    {
        _directory = directory is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    internal void SetUnreadContentPreference(Account account, bool value)
    {
        var replica = Replica(account);
        account.Settings.OneDriveSyncUnreadContent = value;
        replica.RememberPreference(value, pending: true);
        replica.Set("preference/unreadContent", value ? "1" : "0");
        replica.Save();
    }

    internal OneDriveSyncStatistics Statistics(Account account)
    {
        var records = Replica(account).Data.Document.Registers;
        var path = Path.Combine(account.AccountDirectory, "OneDriveReplica.json");
        var subscribed = account.FlattenedFeeds().Select(f => f.Url).ToHashSet(StringComparer.Ordinal);
        var statusIDs = records.Keys.Where(key => key.StartsWith("status/", StringComparison.Ordinal))
            .Select(key => key.Split('/')[2]).Distinct(StringComparer.Ordinal)
            .Where(id => records.GetValueOrDefault("status/Deleted/" + id)?.Value != "1").ToArray();
        var content = records.Where(p => p.Key.StartsWith("content/", StringComparison.Ordinal) && p.Value.Value is not null)
            .Select(p => JsonSerializer.Deserialize<OneDriveArticle>(p.Value.Value!)!).ToArray();
        bool Starred(string id) => records.GetValueOrDefault("status/Starred/" + id)?.Value == "1";
        bool Unread(string id) => records.GetValueOrDefault("status/Read/" + id)?.Value == "0";
        int starredStatuses = statusIDs.Count(Starred);
        int unreadStatuses = statusIDs.Count(id => !Starred(id) && Unread(id));
        int starredContent = content.Count(a => Starred(Encode(a.ID)));
        int unreadContent = content.Count(a => !Starred(Encode(a.ID)) && Unread(Encode(a.ID)) && subscribed.Contains(a.FeedUrl));
        int readContent = content.Length - starredContent - unreadContent;
        return new(account.Folders.Count, account.FlattenedFeeds().Count(),
            statusIDs.Length, content.Length,
            records.Count(p => p.Value.Value is null || p.Key.StartsWith("status/Deleted/", StringComparison.Ordinal) && p.Value.Value == "1"),
            File.Exists(path) ? new FileInfo(path).Length : 0,
            LastExchangeUtc, LastSyncError, starredStatuses, unreadStatuses, statusIDs.Length - starredStatuses - unreadStatuses,
            starredContent, unreadContent, readContent, readContent + (account.Settings.OneDriveSyncUnreadContent ? 0 : unreadContent));
    }

    internal Task ScanAsync(Account account, IProgress<OneDriveSyncProgress>? progress, CancellationToken ct)
        => ExchangeAsync(account, ct, progress, captureContent: false);

    internal async Task<OneDriveCleanupResult> CleanUpContentAsync(Account account, IProgress<OneDriveSyncProgress>? progress, CancellationToken ct)
    {
        await ExchangeAsync(account, ct, progress, captureContent: false);
        var replica = Replica(account);
        var removed = await Task.Run(() => RetireUnneededContent(account, replica, int.MaxValue, ct, progress), ct);
        replica.Save();
        await ExchangeAsync(account, ct, progress, captureContent: false);
        return removed;
    }

    private static OneDriveCleanupResult RetireUnneededContent(Account account, OneDriveReplica replica, int limit,
        CancellationToken ct, IProgress<OneDriveSyncProgress>? progress = null)
    {
        var subscribed = account.FlattenedFeeds().Select(f => f.Url).ToHashSet(StringComparer.Ordinal);
        int readRemoved = 0, unreadRemoved = 0;
        foreach (var (key, register) in replica.Data.Document.Registers.ToArray())
        {
            if (!key.StartsWith("content/", StringComparison.Ordinal) || register.Value is null) continue;
            ct.ThrowIfCancellationRequested();
            var article = JsonSerializer.Deserialize<OneDriveArticle>(register.Value)
                ?? throw new InvalidDataException("Invalid OneDrive article content.");
            var id = Encode(article.ID);
            bool starred = replica.Data.Document.Registers.GetValueOrDefault("status/Starred/" + id)?.Value == "1";
            bool unread = replica.Data.Document.Registers.GetValueOrDefault("status/Read/" + id)?.Value == "0";
            if (starred || subscribed.Contains(article.FeedUrl) && unread && account.Settings.OneDriveSyncUnreadContent) continue;
            replica.Set(key, null);
            if (unread && subscribed.Contains(article.FeedUrl)) unreadRemoved++;
            else readRemoved++;
            var removed = readRemoved + unreadRemoved;
            if (removed % 400 == 0) progress?.Report(new($"Cleaning content: {readRemoved:N0} read/orphaned, {unreadRemoved:N0} unread removed", removed, 0));
            if (removed >= limit) break;
        }
        progress?.Report(new("Content cleanup prepared", readRemoved + unreadRemoved, readRemoved + unreadRemoved));
        return new(readRemoved, unreadRemoved);
    }

    private OneDriveReplica Replica(Account account) => _replica ??= new(Path.Combine(account.AccountDirectory, "OneDriveReplica.json"));
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Decode(string value) => OneDriveDocument.Decode(value);

    public void PrepareToSaveStructure(Account account)
    {
        foreach (var folder in account.Folders)
            if (folder.ExternalID is null) folder.ExternalID = Guid.NewGuid().ToString("N");
    }

    private static Dictionary<string, string?> Structure(Account account)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var folder in account.Folders)
        {
            if (!Guid.TryParseExact(folder.ExternalID, "N", out _))
                throw new InvalidDataException("A OneDrive folder is missing its synchronization identity.");
            result["folder/" + folder.ExternalID] = folder.NameForDisplay;
        }
        foreach (var feed in account.FlattenedFeeds())
        {
            var id = Encode(feed.Url);
            result["feed/" + id] = feed.EditedName ?? "";
            result["title/" + id] = feed.Name;
            result["homepage/" + id] = feed.HomePageUrl;
            if (account.TopLevelFeeds.Contains(feed)) result["member/" + id + "/root"] = "1";
            foreach (var folder in account.Folders.Where(folder => folder.Feeds.Contains(feed)))
                result["member/" + id + "/" + folder.ExternalID] = "1";
        }
        return result;
    }

    public void StructureSaved(Account account)
    {
        if (_applying) return;
        var replica = Replica(account);
        var before = replica.Data.Clock;
        replica.CaptureStructure(Structure(account));
        if (replica.Data.Clock == before) return;
        try
        {
            if (_directory is null || !Directory.Exists(_directory))
                throw new DirectoryNotFoundException("The OneDrive sync folder is unavailable. Local subscription changes are queued.");
            var snapshots = Path.Combine(_directory, "devices-v1");
            Directory.CreateDirectory(snapshots);
            var subscriptions = new OneDriveDocument
            {
                Registers = replica.Data.Document.Registers.Where(p => SnapshotPartition(p.Key) == "subscriptions")
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            };
            FileUtilities.WriteAllTextAtomically(Path.Combine(snapshots, $"{replica.Data.Device}.subscriptions.json"),
                JsonSerializer.Serialize(subscriptions));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LastSyncError = error.Message;
            System.Diagnostics.Trace.TraceError($"OneDrive subscription changes remain queued locally: {error}");
        }
    }

    public Task ImportOpmlAsync(Account account, Parsers.ParserData data, CancellationToken ct = default)
        => _local.ImportOpmlAsync(account, data, ct);
    public Task<Feed?> CreateFeedAsync(Account account, string urlOrSite, string? name, Folder? folder, CancellationToken ct)
        => _local.CreateFeedAsync(account, urlOrSite, name, folder, ct);
    public Task<Folder> CreateFolderAsync(Account account, string name, CancellationToken ct = default)
        => _local.CreateFolderAsync(account, name, ct);
    public Task RenameFolderAsync(Account account, Folder folder, string name, CancellationToken ct = default)
        => _local.RenameFolderAsync(account, folder, name, ct);
    public Task RemoveFolderAsync(Account account, Folder folder, CancellationToken ct = default)
        => _local.RemoveFolderAsync(account, folder, ct);
    public Task RenameFeedAsync(Account account, Feed feed, string name, CancellationToken ct = default)
        => _local.RenameFeedAsync(account, feed, name, ct);
    public Task AddFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        => _local.AddFeedAsync(account, feed, folder, ct);
    public Task RemoveFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        => _local.RemoveFeedAsync(account, feed, folder, ct);
    public Task MoveFeedAsync(Account account, Feed feed, Folder? source, Folder? destination, CancellationToken ct = default)
        => _local.MoveFeedAsync(account, feed, source, destination, ct);
    public Task RestoreFeedAsync(Account account, Feed feed, Folder? folder, CancellationToken ct = default)
        => _local.RestoreFeedAsync(account, feed, folder, ct);
    public Task RestoreFolderAsync(Account account, Folder folder, CancellationToken ct = default)
        => _local.RestoreFolderAsync(account, folder, ct);

    public async Task PublisherArticlesDeletedAsync(Account account, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await account.SyncDatabase.InsertStatusesAsync(ids.Select(id => new SyncStatus(id, SyncStatus.SyncKey.Deleted, true)));
        await account.Database.AcknowledgePublisherDeletionsAsync(ids);
        account.RaiseArticlesDeleted();
    }

    public Task SendArticleStatusAsync(Account account, CancellationToken ct = default) => ExchangeAsync(account, ct);
    public Task RefreshArticleStatusAsync(Account account, CancellationToken ct = default) => ExchangeAsync(account, ct);

    public Task<bool> SyncArticleStatusAsync(Account account, CancellationToken ct = default) => ExchangeAsync(account, ct);

    public Task RefreshAllAsync(Account account, IProgress<ProgressInfo>? progress, CancellationToken ct)
        => account.LogActivityAsync(ActivityKind.RefreshAll, async () =>
        {
            Exception? syncError = null;
            try { await ExchangeAsync(account, ct); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException)
            {
                syncError = error;
                System.Diagnostics.Trace.TraceError($"OneDrive exchange failed; refreshing feeds locally: {error}");
            }
            await _local.RefreshAllAsync(account, progress, ct);
            if (syncError is null) await ExchangeAsync(account, ct);
            else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(syncError).Throw();
        });

    private Task<bool> ExchangeAsync(Account account, CancellationToken ct, IProgress<OneDriveSyncProgress>? progress = null,
        bool captureContent = true)
        => account.LogActivityAsync(ActivityKind.RefreshArticleStatuses, async () =>
        {
            try
            {
                var changed = await ExchangeCoreAsync(account, ct, progress, captureContent);
                LastSyncError = null;
                LastExchangeUtc = DateTime.UtcNow;
                return changed;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or FormatException)
            {
                LastSyncError = error.Message;
                throw;
            }
        }, "OneDrive");

    private async Task<bool> ExchangeCoreAsync(Account account, CancellationToken ct, IProgress<OneDriveSyncProgress>? progress, bool captureContent)
    {
        ct.ThrowIfCancellationRequested();
        PrepareToSaveStructure(account);
        var replica = Replica(account);
        var clock = replica.Data.Clock;
        if (replica.Data.UnreadContentPreference is { } previousPreference
            ? previousPreference != account.Settings.OneDriveSyncUnreadContent
            : account.Settings.HasOneDriveUnreadContentPreference)
            SetUnreadContentPreference(account, account.Settings.OneDriveSyncUnreadContent);
        replica.CaptureStructure(Structure(account));
        var publisherDeletions = await account.Database.FetchPendingPublisherDeletionsAsync();
        if (publisherDeletions.Length > 0) await PublisherArticlesDeletedAsync(account, publisherDeletions, ct);
        var pending = await CaptureStatusesAsync(account, replica, ct);
        if (captureContent) await CaptureContentAsync(account, replica, ct);
        replica.Save();

        if (_directory is null || !Directory.Exists(_directory))
            throw new DirectoryNotFoundException("The OneDrive sync folder is unavailable. Reconnect OneDrive in Accounts settings; local changes are retained.");
        var snapshots = Path.Combine(_directory, "devices-v1");
        Directory.CreateDirectory(snapshots);
        // Validate every incoming document before applying any remote changes.
        var incoming = new List<OneDriveDocument>();
        var paths = Directory.GetFiles(snapshots, "*.json").Order(StringComparer.Ordinal).ToArray();
        progress?.Report(new("Reading OneDrive snapshots", 0, paths.Length));
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var document = JsonSerializer.Deserialize<OneDriveDocument>(await File.ReadAllTextAsync(path, ct))
                ?? throw new InvalidDataException("An empty OneDrive snapshot was received.");
            document.Validate();
            incoming.Add(document);
            progress?.Report(new("Reading OneDrive snapshots", incoming.Count, paths.Length));
        }
        progress?.Report(new("Merging subscriptions and article states", 0, 0));
        foreach (var document in incoming) replica.Merge(document);
        var pendingStars = pending.Where(s => s.Key == SyncStatus.SyncKey.Starred).Select(s => s.ArticleID).ToHashSet(StringComparer.Ordinal);
        foreach (var status in pending.Where(s => s.Key != SyncStatus.SyncKey.Deleted || !pendingStars.Contains(s.ArticleID)))
            replica.Set(StatusKey(status), status.Flag ? "1" : "0");
        foreach (var id in pendingStars) replica.Set("status/Deleted/" + Encode(id), "0");
        if (replica.Data.PendingPreference is { } localPreference)
            replica.Set("preference/unreadContent", localPreference ? "1" : "0");
        if (!replica.Data.Document.Registers.TryGetValue("preference/unreadContent", out var preference))
        {
            replica.Set("preference/unreadContent", account.Settings.OneDriveSyncUnreadContent ? "1" : "0");
            preference = replica.Data.Document.Registers["preference/unreadContent"];
        }
        var syncUnread = preference.Value == "1";
        if (account.Settings.OneDriveSyncUnreadContent != syncUnread) account.Settings.OneDriveSyncUnreadContent = syncUnread;
        if (replica.Data.UnreadContentPreference != syncUnread)
            replica.RememberPreference(syncUnread, pending: replica.Data.PendingPreference is not null);
        replica.Save();
        ApplyStructure(account, replica);
        if (!_startupCleanupCompleted)
        {
            await account.Database.CleanupDatabaseAtStartupAsync(account.FlattenedFeeds().Select(f => f.FeedID),
                ArticlesDatabase.ArticleRetentionStyle.FeedBased, ct: ct);
            _startupCleanupCompleted = true;
        }
        await ApplyArticlesAndStatusesAsync(account, replica, ct);
        progress?.Report(new("Publishing pending changes", 0, 0));
        var now = DateTime.UtcNow;
        if (captureContent && account.Settings.OneDriveLastContentCleanupUtc is null) account.Settings.OneDriveLastContentCleanupUtc = now;
        else if (captureContent && account.Settings.OneDriveLastContentCleanupUtc is { } lastCleanup && now - lastCleanup > TimeSpan.FromDays(6))
        {
            account.Settings.OneDriveLastContentCleanupUtc = now;
            RetireUnneededContent(account, replica, 400, ct);
        }
        replica.RememberStructure(Structure(account));
        replica.Save();
        bool publish = false;
        foreach (var group in replica.Data.Document.Registers.GroupBy(p => SnapshotPartition(p.Key)))
        {
            var destination = Path.Combine(snapshots, $"{replica.Data.Device}.{group.Key}.json");
            var document = new OneDriveDocument { Registers = group.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            if (File.Exists(destination) && (await File.ReadAllBytesAsync(destination, ct)).AsSpan().SequenceEqual(bytes)) continue;
            await FileUtilities.WriteAllBytesAtomicallyAsync(destination, bytes, ct);
            publish = true;
        }
        await account.StatusMutationGate.WaitAsync(ct);
        try
        {
            foreach (var group in pending.GroupBy(s => s.Key))
                await account.SyncDatabase.DeleteSelectedAsync(group.Select(s => s.ArticleID), group.Key);
        }
        finally { account.StatusMutationGate.Release(); }
        if (replica.Data.UnreadContentPreference != syncUnread || replica.Data.PendingPreference is not null)
        {
            replica.RememberPreference(syncUnread, pending: false);
            replica.Save();
        }
        progress?.Report(new("OneDrive exchange complete", paths.Length, paths.Length));
        return publish || replica.Data.Clock != clock;
    }

    private static string SnapshotPartition(string key) => key.StartsWith("content/", StringComparison.Ordinal)
        ? $"content-{SHA256.HashData(Encoding.UTF8.GetBytes(key))[0] % 16:x}"
        : key.StartsWith("status/", StringComparison.Ordinal) ? "statuses" : "subscriptions";

    private static string StatusKey(SyncStatus status) => $"status/{status.Key}/{Encode(status.ArticleID)}";

    private static async Task<HashSet<SyncStatus>> CaptureStatusesAsync(Account account, OneDriveReplica replica, CancellationToken ct)
    {
        await account.StatusMutationGate.WaitAsync(ct);
        try
        {
            var pending = await account.SyncDatabase.SelectForProcessingAsync();
            foreach (var status in pending)
                replica.Set(StatusKey(status), status.Flag ? "1" : "0");
            replica.Save();
            return pending;
        }
        finally { account.StatusMutationGate.Release(); }
    }

    private static async Task CaptureContentAsync(Account account, OneDriveReplica replica, CancellationToken ct)
    {
        var feeds = account.FlattenedFeeds().ToDictionary(f => f.FeedID, f => f.Url, StringComparer.Ordinal);
        var syncUnread = account.Settings.OneDriveSyncUnreadContent;
        var articles = await account.Database.FetchStarredAsync(feeds.Keys);
        if (syncUnread) articles.UnionWith(await account.Database.FetchUnreadArticlesAsync(feeds.Keys));
        articles.UnionWith(await account.Database.FetchArticlesByIDsAsync(replica.Data.Document.Registers
            .Where(p => p.Key.StartsWith("content/", StringComparison.Ordinal) && p.Value.Value is not null)
            .Select(p => Decode(p.Key["content/".Length..]))));
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var article in articles)
        {
            ct.ThrowIfCancellationRequested();
            var key = "content/" + Encode(article.ArticleID);
            if (!feeds.ContainsKey(article.FeedID))
            {
                if (article.Status.Starred) retained.Add(key);
                continue;
            }
            if (!article.Status.Starred && (!syncUnread || article.Status.Read)) continue;
            retained.Add(key);
            var json = JsonSerializer.Serialize(OneDriveArticle.From(article, feeds[article.FeedID]));
            if (!replica.Data.Document.Registers.TryGetValue(key, out var previous) || previous.Value != json)
                replica.Set(key, json);
            replica.RememberContent(key, replica.Data.Document.Registers[key]);
            SeedStatus(ArticleStatus.Key.Read, article.Status.Read);
            SeedStatus(ArticleStatus.Key.Starred, article.Status.Starred);
            void SeedStatus(ArticleStatus.Key status, bool flag)
            {
                var statusKey = $"status/{status}/{Encode(article.ArticleID)}";
                if (!replica.Data.Document.Registers.ContainsKey(statusKey)) replica.Set(statusKey, flag ? "1" : "0");
            }
        }
        // Only retire content for articles present locally; a feed can have fallen out of its RSS window.
        foreach (var article in articles)
        {
            var key = "content/" + Encode(article.ArticleID);
            if (!retained.Contains(key) && replica.Data.Document.Registers.TryGetValue(key, out var previous) && previous.Value is not null)
                replica.Set(key, null);
        }
    }

    private void ApplyStructure(Account account, OneDriveReplica replica)
    {
        var registers = replica.Data.Document.Registers;
        var effective = registers.Where(p => p.Key.StartsWith("folder/", StringComparison.Ordinal) && p.Value.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal);
        foreach (var (key, register) in registers.Where(p => p.Key.StartsWith("member/", StringComparison.Ordinal) && p.Value.Value == "1"))
        {
            var parts = key.Split('/');
            if (parts[2] != "root" && !effective.ContainsKey("folder/" + parts[2])) continue;
            effective[key] = "1";
            var feedKey = "feed/" + parts[1];
            effective[feedKey] = registers.GetValueOrDefault(feedKey)?.Value ?? "";
            effective["title/" + parts[1]] = registers.GetValueOrDefault("title/" + parts[1])?.Value;
            effective["homepage/" + parts[1]] = registers.GetValueOrDefault("homepage/" + parts[1])?.Value;
        }
        var current = Structure(account);
        if (current.Count == effective.Count && current.All(p => effective.TryGetValue(p.Key, out var value) && p.Value == value)) return;
        _applying = true;
        try
        {
            account.PerformBatchUpdate(() =>
            {
                var folders = account.Folders.Where(f => f.ExternalID is not null).ToDictionary(f => f.ExternalID!, StringComparer.Ordinal);
                var feeds = account.FlattenedFeeds().ToDictionary(f => f.Url, StringComparer.Ordinal);
                var nextID = account.Folders.Select(f => f.FolderID).DefaultIfEmpty().Max();
                account.Folders.Clear();
                foreach (var (key, register) in registers.Where(p => p.Key.StartsWith("folder/", StringComparison.Ordinal) && p.Value.Value is not null))
                {
                    var id = key["folder/".Length..];
                    if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid OneDrive folder identity.");
                    if (!folders.TryGetValue(id, out var folder)) folder = new Folder(account.AccountID, register.Value, ++nextID) { ExternalID = id };
                    folder.Name = register.Value;
                    folder.Feeds.Clear();
                    account.Folders.Add(folder);
                    folders[id] = folder;
                }
                account.TopLevelFeeds.Clear();
                foreach (var (key, register) in registers.Where(p => p.Key.StartsWith("member/", StringComparison.Ordinal) && p.Value.Value == "1"))
                {
                    var parts = key.Split('/');
                    if (parts.Length != 3) throw new InvalidDataException("Invalid OneDrive feed membership.");
                    var url = Decode(parts[1]);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        throw new InvalidDataException("Invalid OneDrive feed URL.");
                    Folder? folder = null;
                    if (parts[2] != "root" && (!folders.TryGetValue(parts[2], out folder) || !account.Folders.Contains(folder))) continue;
                    if (!feeds.TryGetValue(url, out var feed)) feeds[url] = feed = account.CreateFeedModel(url, null, url);
                    if (registers.TryGetValue("feed/" + parts[1], out var name)) feed.EditedName = string.IsNullOrEmpty(name.Value) ? null : name.Value;
                    if (registers.TryGetValue("title/" + parts[1], out var title)) feed.Name = title.Value;
                    if (registers.TryGetValue("homepage/" + parts[1], out var homepage)) feed.HomePageUrl = homepage.Value;
                    (folder?.Feeds ?? account.TopLevelFeeds).Add(feed);
                }
            });
        }
        finally { _applying = false; }
    }

    private static async Task ApplyArticlesAndStatusesAsync(Account account, OneDriveReplica replica, CancellationToken ct)
    {
        var feeds = account.FlattenedFeeds().ToDictionary(f => f.Url, StringComparer.Ordinal);
        var downloaded = new List<Article>();
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        bool removedArticles = false;
        await account.StatusMutationGate.WaitAsync(ct);
        try
        {
            var pendingStars = await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.SyncKey.Starred);
            foreach (var (key, register) in replica.Data.Document.Registers
                .Where(p => p.Key.StartsWith("status/Deleted/", StringComparison.Ordinal) && p.Value.Value == "1").ToArray())
            {
                var id = Decode(key["status/Deleted/".Length..]);
                if (pendingStars.Contains(id)) continue;
                deleted.Add(id);
                if (!replica.StatusWasApplied(key, register))
                {
                    removedArticles |= await account.Database.DeleteSynchronizedArticlesAsync([id], ct) > 0;
                    replica.RememberStatus(key, register);
                }
                var contentKey = "content/" + Encode(id);
                if (replica.Data.Document.Registers.GetValueOrDefault(contentKey)?.Value is not null)
                    replica.Set(contentKey, null);
            }
        }
        finally { account.StatusMutationGate.Release(); }
        var content = replica.Data.Document.Registers.Where(p => p.Key.StartsWith("content/", StringComparison.Ordinal)
            && p.Value.Value is not null && !deleted.Contains(Decode(p.Key["content/".Length..]))).ToArray();
        var existing = await account.Database.FetchExistingArticleIDsAsync(content.Select(p => Decode(p.Key["content/".Length..])));
        foreach (var batch in content.Where(p => !replica.ContentWasApplied(p.Key, p.Value)
            || !existing.Contains(Decode(p.Key["content/".Length..]))).Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            var materialized = batch.Select(pair => (Pair: pair, Article: JsonSerializer.Deserialize<OneDriveArticle>(pair.Value.Value!)
                ?? throw new InvalidDataException("Invalid OneDrive article content.")))
                .Where(item => feeds.ContainsKey(item.Article.FeedUrl)).ToArray();
            var articles = materialized.Select(item => item.Article.ToArticle(account, feeds[item.Article.FeedUrl])).ToArray();
            var inserted = await account.Database.UpdateArticlesAsync(articles);
            downloaded.AddRange(inserted);
            foreach (var item in materialized) replica.RememberContent(item.Pair.Key, item.Pair.Value);
        }
        await account.StatusMutationGate.WaitAsync(ct);
        try
        {
            foreach (var key in new[] { ArticleStatus.Key.Read, ArticleStatus.Key.Starred })
            {
                var pending = await account.SyncDatabase.SelectPendingArticleIDsForKeyAsync(SyncStatus.FromArticleStatusKey(key));
                var prefix = $"status/{key}/";
                foreach (var group in replica.Data.Document.Registers.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .GroupBy(p => p.Value.Value))
                {
                    if (group.Key is not ("0" or "1")) throw new InvalidDataException("Invalid OneDrive article status.");
                    var records = group.Where(p => !pending.Contains(Decode(p.Key[prefix.Length..]))
                        && !deleted.Contains(Decode(p.Key[prefix.Length..]))
                        && (!replica.StatusWasApplied(p.Key, p.Value) || downloaded.Any(a => a.ArticleID == Decode(p.Key[prefix.Length..])))).ToArray();
                    var ids = records.Select(p => Decode(p.Key[prefix.Length..])).ToArray();
                    await account.Database.CreateStatusesIfNeededAsync(ids, defaultRead: true);
                    await account.Database.MarkAsync(ids, key, group.Key == "1");
                    foreach (var record in records) replica.RememberStatus(record.Key, record.Value);
                }
            }
        }
        finally { account.StatusMutationGate.Release(); }
        await account.RecalculateUnreadCountsAsync();
        if (removedArticles) account.RaiseArticlesDeleted();
        if (downloaded.Count > 0) account.RaiseNewArticlesDownloaded(downloaded);
    }
}
