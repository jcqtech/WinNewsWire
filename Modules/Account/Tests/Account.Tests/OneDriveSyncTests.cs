using System.Text.Json;
using System.Text;
using WinNewsWire.Account.OneDrive;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Account.Tests;

public sealed class OneDriveSyncTests : IDisposable
{
    private readonly TestAccountManager _storage = new();
    private readonly string _cloud = Path.Combine(Path.GetTempPath(), "WinNewsWire.OneDriveTests." + Guid.NewGuid().ToString("N"));

    public OneDriveSyncTests() => Directory.CreateDirectory(_cloud);
    private Account NewAccount()
    {
        var account = _storage.CreateAccount(accountDelegate: new OneDriveAccountDelegate(_cloud));
        account.Settings.OneDriveSyncUnreadContent = true;
        return account;
    }
    private static Task Sync(Account account) => account.SyncArticleStatusAsync();

    [Fact]
    public async Task SubscriptionsFoldersCopiesRenamesMovesDeletesAndUndoRoundTrip()
    {
        var a = NewAccount();
        var b = NewAccount();
        var folder = await a.CreateFolderAsync("Technology");
        var other = await a.CreateFolderAsync("Other");
        var feed = a.AddFeed("https://example.test/rss", "Publisher", folder);
        feed.HomePageUrl = "https://example.test/";
        await a.RenameFeedAsync(feed, "My Name");
        await a.AddFeedAsync(feed, null);
        await a.AddFeedAsync(feed, other);
        await Sync(a);
        await Sync(b);
        Assert.Equal(2, b.Folders.Count);
        Assert.Equal("My Name", Assert.Single(b.TopLevelFeeds).EditedName);
        Assert.Equal("Publisher", Assert.Single(b.TopLevelFeeds).Name);
        Assert.Equal("https://example.test/", Assert.Single(b.TopLevelFeeds).HomePageUrl);
        Assert.All(b.Folders, f => Assert.Single(f.Feeds));
        Assert.Single(b.FlattenedFeeds());
        var remoteFolder = b.ExistingFolderWithName("Technology")!;
        await b.RenameFolderAsync(remoteFolder, "News");
        await b.RemoveFeedAsync(Assert.Single(b.TopLevelFeeds), null);
        await Sync(b);
        await Sync(a);
        Assert.Empty(a.TopLevelFeeds);
        Assert.NotNull(a.ExistingFolderWithName("News"));
        await a.MoveFeedAsync(feed, other, null);
        await a.RemoveFolderAsync(a.ExistingFolderWithName("News")!);
        await Sync(a);
        await Sync(b);
        Assert.Single(b.TopLevelFeeds);
        Assert.Empty(Assert.Single(b.Folders).Feeds);
        await a.RestoreFolderAsync(folder);
        await Sync(a);
        await Sync(b);
        Assert.Single(b.ExistingFolderWithName("News")!.Feeds);
    }

    [Fact]
    public async Task TwoOfflineDevicesDoNotLoseIndependentEditsOrResurrectDeletedMemberships()
    {
        var a = NewAccount();
        var b = NewAccount();
        var c = NewAccount();
        var feed = a.AddFeed("https://example.test/a", "A");
        await Sync(a);
        await Sync(b);
        await Sync(c);
        await a.RemoveFeedAsync(feed, null);
        var folder = await b.CreateFolderAsync("Offline addition");
        b.AddFeed("https://example.test/b", "B", folder);
        await Sync(a);
        await Sync(b);
        await Sync(c);
        await Sync(a);
        await Sync(b);
        Assert.All(new[] { a, b, c }, account =>
        {
            Assert.Null(account.ExistingFeedWithUrl(feed.Url));
            Assert.Equal("https://example.test/b", Assert.Single(account.FlattenedFeeds()).Url);
        });
    }

    [Fact]
    public async Task ContentAndBothStatusDirectionsSurviveFeedWindowAndRestart()
    {
        var a = NewAccount();
        var b = NewAccount();
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        var article = MakeArticle(a, feed);
        await a.Database.UpdateArticlesAsync([article]);
        await a.MarkAsync([article.ArticleID], ArticleStatus.Key.Starred, true);
        await Sync(a);
        await Sync(b);
        var received = Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID));
        Assert.Equal("<p>Archived body</p>", received.ContentHtml);
        Assert.False(received.Status.Read);
        Assert.True(Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID)).Status.Starred);
        await b.MarkAsync([article.ArticleID], ArticleStatus.Key.Read, true);
        await b.MarkAsync([article.ArticleID], ArticleStatus.Key.Starred, false);
        b = _storage.Reopen(b, new OneDriveAccountDelegate(_cloud));
        await Sync(b);
        await Sync(a);
        var updated = Assert.Single(await a.Database.FetchArticlesAsync(feed.FeedID));
        Assert.True(updated.Status.Read);
        Assert.False(updated.Status.Starred);
        await a.MarkAsync([article.ArticleID], ArticleStatus.Key.Read, false);
        await a.MarkAsync([article.ArticleID], ArticleStatus.Key.Starred, true);
        await Sync(a);
        await Sync(b);
        Assert.False(Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID)).Status.Read);
        Assert.True(Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID)).Status.Starred);
    }

    [Fact]
    public async Task MissingOneDriveDirectoryRetainsOfflineChangesAcrossRestart()
    {
        var a = NewAccount();
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        var article = MakeArticle(a, feed);
        await a.Database.UpdateArticlesAsync([article]);
        await a.MarkAsync([article.ArticleID], ArticleStatus.Key.Starred, true);
        Directory.Delete(_cloud, recursive: true);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Sync(a));
        a = _storage.Reopen(a, new OneDriveAccountDelegate(_cloud));
        Directory.CreateDirectory(_cloud);
        await Sync(a);
        var b = NewAccount();
        await Sync(b);
        Assert.True(Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID)).Status.Starred);
    }

    [Fact]
    public async Task FailedDurableOutboxWriteDoesNotAcknowledgeQueuedStatuses()
    {
        var a = NewAccount();
        await a.Database.CreateStatusesIfNeededAsync(["queued"], defaultRead: false);
        await Sync(a);
        await a.MarkAsync(["queued"], ArticleStatus.Key.Read, true);
        var path = Path.Combine(a.AccountDirectory, "OneDriveReplica.json");
        var backup = path + ".backup";
        File.Move(path, backup);
        Directory.CreateDirectory(path);
        try
        {
            var failure = await Record.ExceptionAsync(() => Sync(a));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        finally
        {
            Directory.Delete(path);
            File.Move(backup, path);
        }
        Assert.Equal(1, await a.SyncDatabase.SelectPendingCountAsync());
        a = _storage.Reopen(a, new OneDriveAccountDelegate(_cloud));
        await Sync(a);
        var b = NewAccount();
        await Sync(b);
        Assert.Contains("queued", await b.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Read, true));
    }

    [Fact]
    public async Task UnavailableOneDriveStillRefreshesPublisherFeedsAndReportsTheSyncError()
    {
        var a = NewAccount();
        var previous = Account.LocalRefresherFactory;
        var refresher = new RecordingRefresher();
        Account.LocalRefresherFactory = _ => refresher;
        try
        {
            Directory.Delete(_cloud);
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => a.RefreshAllAsync());
            Assert.True(refresher.Refreshed);
            Assert.NotNull(((OneDriveAccountDelegate)a.Delegate).LastSyncError);
        }
        finally { Account.LocalRefresherFactory = previous; }
    }

    [Fact]
    public async Task UnknownStatusesArriveBeforeArticlesAndAreAppliedToLaterDownloads()
    {
        var a = NewAccount();
        var b = NewAccount();
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        await a.Database.CreateStatusesIfNeededAsync(["not-downloaded"], defaultRead: false);
        await a.MarkAsync(["not-downloaded"], ArticleStatus.Key.Read, true);
        await a.MarkAsync(["not-downloaded"], ArticleStatus.Key.Starred, true);
        await Sync(a);
        await Sync(b);
        Assert.Contains("not-downloaded", await b.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Starred, true));
        await b.Database.UpdateArticlesAsync([MakeArticle(b, b.ExistingFeedWithUrl(feed.Url)!, "not-downloaded")]);
        var received = Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID));
        Assert.True(received.Status.Read);
        Assert.True(received.Status.Starred);
    }

    [Fact]
    public async Task UnreadContentPreferenceDoesNotDisableStarredContent()
    {
        var a = NewAccount();
        a.Settings.OneDriveSyncUnreadContent = false;
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        await a.Database.UpdateArticlesAsync([MakeArticle(a, feed, "unread"), MakeArticle(a, feed, "star")]);
        await a.MarkAsync(["star"], ArticleStatus.Key.Starred, true);
        await Sync(a);
        var b = NewAccount();
        await Sync(b);
        Assert.Equal("star", Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID)).ArticleID);
    }

    [Fact]
    public async Task MalformedOrFutureSnapshotsAreReportedWithoutErasingSubscriptions()
    {
        var a = NewAccount();
        a.AddFeed("https://example.test/rss", "Feed");
        await Sync(a);
        var path = Path.Combine(_cloud, "devices-v1", "future.json");
        await File.WriteAllTextAsync(path, """{"Version":2,"Registers":{}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => Sync(a));
        Assert.Single(a.FlattenedFeeds());
        Assert.Contains("Unsupported", ((OneDriveAccountDelegate)a.Delegate).LastSyncError);
        await File.WriteAllTextAsync(path, "{");
        await Assert.ThrowsAsync<JsonException>(() => Sync(a));
        Assert.Single(a.FlattenedFeeds());
    }

    [Fact]
    public async Task InvalidMembershipIsRejectedBeforeAnyRemoteStructureIsApplied()
    {
        var a = NewAccount();
        a.AddFeed("https://example.test/rss", "Feed");
        await Sync(a);
        var remote = new OneDriveDocument();
        remote.Registers["member/%/root"] = new(100, Guid.NewGuid().ToString("N"), "1");
        await File.WriteAllTextAsync(Path.Combine(_cloud, "devices-v1", "invalid.json"), JsonSerializer.Serialize(remote));
        await Assert.ThrowsAsync<FormatException>(() => Sync(a));
        Assert.Single(a.FlattenedFeeds());
    }

    [Fact]
    public async Task QuietPollDoesNotRewriteCloudSnapshotsOrRaiseStructureNotifications()
    {
        var a = NewAccount();
        a.AddFeed("https://example.test/rss", "Feed");
        await Sync(a);
        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(_cloud, "devices-v1"), "*.json"));
        var written = File.GetLastWriteTimeUtc(snapshot);
        var b = NewAccount();
        await Sync(b);
        await Sync(a);
        written = File.GetLastWriteTimeUtc(snapshot);
        int notifications = 0;
        a.AccountStructureChanged += (_, _) => notifications++;
        Assert.False(await a.SyncArticleStatusAsync());
        Assert.Equal(written, File.GetLastWriteTimeUtc(snapshot));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task DifferentLocalArrivalDatesDoNotCauseArticleContentToPingPong()
    {
        var a = NewAccount();
        var b = NewAccount();
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        var otherFeed = b.AddFeed(feed.Url, "Feed");
        Article ArticleFor(Account account, Feed target, DateTime arrived) => new(account.AccountID, "same-article",
            target.FeedID, "same-article", "Title", "<p>Body</p>", null, null, null, null, null, null,
            DateTime.UtcNow.Date, null, null, null, new ArticleStatus("same-article", false, arrived));
        await a.Database.UpdateArticlesAsync([ArticleFor(a, feed, DateTime.UtcNow.AddDays(-1))]);
        await b.Database.UpdateArticlesAsync([ArticleFor(b, otherFeed, DateTime.UtcNow)]);
        await Sync(a);
        await Sync(b);
        await Sync(a);
        Assert.False(await b.SyncArticleStatusAsync());
        Assert.False(await a.SyncArticleStatusAsync());
    }

    [Fact]
    public async Task CleanupKeepsStarredBodiesAndDeletionMarkersAndDoesNotDeleteLocalHistory()
    {
        var a = NewAccount();
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        await a.Database.UpdateArticlesAsync([MakeArticle(a, feed, "star"), MakeArticle(a, feed, "read")]);
        await a.MarkAsync(["star"], ArticleStatus.Key.Starred, true);
        await Sync(a);
        await a.MarkAsync(["read"], ArticleStatus.Key.Read, true);
        await a.CleanUpOneDriveContentAsync();
        var statistics = await a.FetchOneDriveSyncStatisticsAsync();
        Assert.Equal(1, statistics.ContentRecords);
        Assert.True(statistics.Tombstones > 0);
        Assert.Equal(2, (await a.Database.FetchArticlesAsync(feed.FeedID)).Count);
        var b = NewAccount();
        await Sync(b);
        Assert.Equal("star", Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID)).ArticleID);
    }

    [Fact]
    public async Task DelayedIndependentOneDriveDirectoriesConvergeWithConflictCopies()
    {
        var left = Path.Combine(_cloud, "left");
        var right = Path.Combine(_cloud, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        var a = _storage.CreateAccount(accountDelegate: new OneDriveAccountDelegate(left));
        var b = _storage.CreateAccount(accountDelegate: new OneDriveAccountDelegate(right));
        a.AddFeed("https://example.test/a", "A");
        b.AddFeed("https://example.test/b", "B");
        await Sync(a);
        await Sync(b);
        var aPath = Assert.Single(Directory.GetFiles(Path.Combine(left, "devices-v1")));
        var bPath = Assert.Single(Directory.GetFiles(Path.Combine(right, "devices-v1")));
        File.Copy(aPath, Path.Combine(right, "devices-v1", Path.GetFileName(aPath)));
        File.Copy(bPath, Path.Combine(left, "devices-v1", Path.GetFileName(bPath)));
        await Sync(a);
        await Sync(b);
        await a.RenameFeedAsync(a.ExistingFeedWithUrl("https://example.test/a")!, "Changed offline");
        await b.RenameFeedAsync(b.ExistingFeedWithUrl("https://example.test/b")!, "Other offline change");
        await Sync(a);
        await Sync(b);
        File.Copy(aPath, Path.Combine(right, "devices-v1", "conflict-copy.json"));
        File.Copy(bPath, Path.Combine(left, "devices-v1", "conflict-copy.json"));
        await Sync(a);
        await Sync(b);
        foreach (var account in new[] { a, b })
        {
            Assert.Equal(2, account.FlattenedFeeds().Count());
            Assert.Equal("Changed offline", account.ExistingFeedWithUrl("https://example.test/a")!.EditedName);
            Assert.Equal("Other offline change", account.ExistingFeedWithUrl("https://example.test/b")!.EditedName);
        }
    }

    [Fact]
    public async Task ConcurrentRenamesConvergeDeterministicallyInsteadOfOscillating()
    {
        var a = NewAccount();
        var b = NewAccount();
        var folder = await a.CreateFolderAsync("Original");
        await Sync(a);
        await Sync(b);
        await a.RenameFolderAsync(folder, "Left");
        await b.RenameFolderAsync(Assert.Single(b.Folders), "Right");
        await Sync(a);
        await Sync(b);
        await Sync(a);
        Assert.Equal(Assert.Single(a.Folders).Name, Assert.Single(b.Folders).Name);
        Assert.Contains(Assert.Single(a.Folders).Name, new[] { "Left", "Right" });
        Assert.False(await a.SyncArticleStatusAsync());
        Assert.False(await b.SyncArticleStatusAsync());
    }

    [Fact]
    public async Task ProviderFactoryRestoresTheOneDriveFolderWithoutServiceCredentials()
    {
        var a = NewAccount();
        a.AddFeed("https://example.test/rss", "Feed");
        await Sync(a);
        var provider = AccountDelegateFactory.Resolve(AccountType.OneDrive, a.AccountID, server: a.Server);
        var reopened = _storage.Reopen(a, provider);
        await Sync(reopened);
        Assert.Equal(AccountType.OneDrive, reopened.Type);
        Assert.Equal(new Uri(_cloud + Path.DirectorySeparatorChar).AbsoluteUri, reopened.Server);
        Assert.Single(reopened.FlattenedFeeds());
    }

    [Fact]
    public async Task NewAccountsDefaultUnreadContentOffAndAdoptTheSharedPreference()
    {
        var a = _storage.CreateAccount(accountDelegate: new OneDriveAccountDelegate(_cloud));
        Assert.False(a.Settings.OneDriveSyncUnreadContent);
        await Sync(a);
        await a.ConfigureOneDriveAsync(_cloud, true);
        await Sync(a);
        var b = _storage.CreateAccount(accountDelegate: new OneDriveAccountDelegate(_cloud));
        Assert.False(b.Settings.OneDriveSyncUnreadContent);
        await Sync(b);
        Assert.True(b.Settings.OneDriveSyncUnreadContent);
        await b.ConfigureOneDriveAsync(_cloud, false);
        await Sync(b);
        await Sync(a);
        Assert.False(a.Settings.OneDriveSyncUnreadContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingStarOrUnstarProtectsAnArticleFromAnIncomingDeletion(bool star)
    {
        var a = NewAccount();
        var b = NewAccount();
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        await a.Database.UpdateArticlesAsync([MakeArticle(a, feed)]);
        await a.MarkAsync(["article"], ArticleStatus.Key.Starred, !star);
        await Sync(a);
        await Sync(b);
        await a.Delegate.PublisherArticlesDeletedAsync(a, ["article"], CancellationToken.None);
        await b.MarkAsync(["article"], ArticleStatus.Key.Starred, star);
        await Sync(a);
        await Sync(b);
        Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID));
        Assert.Equal(star, Assert.Single(await b.Database.FetchArticlesAsync(feed.FeedID)).Status.Starred);
    }

    [Fact]
    public async Task PendingReadAloneDoesNotProtectFromAnIncomingDeletion()
    {
        var a = NewAccount();
        var b = NewAccount();
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        await a.Database.UpdateArticlesAsync([MakeArticle(a, feed)]);
        await Sync(a);
        await Sync(b);
        await a.Delegate.PublisherArticlesDeletedAsync(a, ["article"], CancellationToken.None);
        await Sync(a);
        await b.MarkAsync(["article"], ArticleStatus.Key.Read, true);
        int deletionNotifications = 0;
        b.ArticlesDeleted += (_, _) => deletionNotifications++;
        await Sync(b);
        Assert.Empty(await b.Database.FetchArticlesAsync(feed.FeedID));
        Assert.Contains("article", await b.Database.FetchStatusArticleIDsAsync(ArticleStatus.Key.Read, true));
        Assert.Equal(1, deletionNotifications);
        await Sync(b);
        Assert.Equal(1, deletionNotifications);
    }

    [Fact]
    public async Task PublisherDeletionJournalRecoversWhenTheWorkerNotificationWasLost()
    {
        var a = NewAccount();
        var b = NewAccount();
        var feed = a.AddFeed("https://example.test/rss", "Feed");
        var old = new Article(a.AccountID, "expired", feed.FeedID, "expired", "Old", "Body", null, null,
            null, null, null, null, DateTime.UtcNow.AddDays(-40), null, null, null,
            new ArticleStatus("expired", false, DateTime.UtcNow.AddDays(-31)));
        await a.Database.UpdateArticlesAsync([old]);
        await Sync(a);
        await Sync(b);
        var parsed = await Parsers.FeedParser.ParseAsync(new(feed.Url, Encoding.UTF8.GetBytes("""
            <rss version="2.0"><channel><title>Feed</title><link>https://example.test/</link><description>Feed</description>
            <item><guid>new</guid><title>New</title></item></channel></rss>
            """)));
        await a.Database.UpdateAsync(parsed!.Items, feed.FeedID, deleteOlder: true);
        // Simulate exit after the worker's DB commit but before the IPC notification.
        a = _storage.Reopen(a, new OneDriveAccountDelegate(_cloud));
        await Sync(a);
        await Sync(b);
        Assert.Empty(await b.Database.FetchArticlesByIDsAsync(["expired"]));
        Assert.Empty(await a.Database.FetchPendingPublisherDeletionsAsync());
    }

    [Fact]
    public async Task WeeklyCleanupIsBoundedAndManualCleanupKeepsEvenOrphanedStarredBodies()
    {
        var a = _storage.CreateAccount(accountDelegate: new OneDriveAccountDelegate(_cloud));
        var source = _storage.CreateAccount();
        var feed = source.AddFeed("https://example.test/orphan", "Orphan");
        var document = new OneDriveDocument();
        var device = Guid.NewGuid().ToString("N");
        long clock = 0;
        static string ID(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        foreach (var index in Enumerable.Range(0, 451))
        {
            var article = MakeArticle(source, feed, "orphan-" + index);
            var id = ID(article.ArticleID);
            document.Registers["content/" + id] = new(++clock, device, JsonSerializer.Serialize(OneDriveArticle.From(article, feed.Url)));
            document.Registers["status/Read/" + id] = new(++clock, device, "1");
            document.Registers["status/Starred/" + id] = new(++clock, device, index == 0 ? "1" : "0");
        }
        var snapshots = Path.Combine(_cloud, "devices-v1");
        Directory.CreateDirectory(snapshots);
        await File.WriteAllTextAsync(Path.Combine(snapshots, "old-device.json"), JsonSerializer.Serialize(document));
        await Sync(a);
        Assert.Equal(451, (await a.FetchOneDriveSyncStatisticsAsync()).ContentRecords);
        a.Settings.OneDriveLastContentCleanupUtc = DateTime.UtcNow.AddDays(-7);
        await Sync(a);
        Assert.Equal(51, (await a.FetchOneDriveSyncStatisticsAsync()).ContentRecords);
        await Sync(a);
        Assert.Equal(51, (await a.FetchOneDriveSyncStatisticsAsync()).ContentRecords);
        var cleanup = await a.CleanUpOneDriveContentAsync();
        Assert.Equal(50, cleanup.ReadDeleted);
        Assert.Equal(0, cleanup.UnreadDeleted);
        var remaining = await a.FetchOneDriveSyncStatisticsAsync();
        Assert.Equal(1, remaining.ContentRecords);
        Assert.Equal(1, remaining.StarredContent);
    }

    [Fact]
    public void MergeIsOrderIndependentAndOldSnapshotsCannotUndoTombstones()
    {
        var first = new OneDriveReplica(Path.Combine(_cloud, "first.local"));
        var second = new OneDriveReplica(Path.Combine(_cloud, "second.local"));
        var membership = "member/aHR0cHM6Ly9leGFtcGxlLnRlc3QvcnNz/root";
        first.Set(membership, "1");
        second.Set("folder/" + Guid.NewGuid().ToString("N"), "Independent");
        second.Merge(first.Data.Document);
        second.Set(membership, null);
        var deletion = JsonSerializer.Deserialize<OneDriveDocument>(JsonSerializer.Serialize(second.Data.Document))!;
        first.Merge(deletion);
        Assert.Null(first.Data.Document.Registers[membership].Value);
        var stale = new OneDriveDocument { Registers = new() { [membership] = new(1, first.Data.Device, "1") } };
        first.Merge(stale);
        second.Merge(first.Data.Document);
        Assert.Equal(JsonSerializer.Serialize(first.Data.Document.Registers.OrderBy(p => p.Key)),
            JsonSerializer.Serialize(second.Data.Document.Registers.OrderBy(p => p.Key)));
    }

    [Theory]
    [InlineData(@"C:\OneDriveOther\WinNewsWire")]
    [InlineData(@"C:\OneDrive")]
    [InlineData(@"C:\OneDrive\..\Outside")]
    public void SyncFolderMustBeInsideTheActualOneDriveRoot(string path)
        => Assert.Throws<ArgumentException>(() => OneDriveLocations.ValidateSyncDirectory(path, [@"C:\OneDrive"]));

    private static Article MakeArticle(Account account, Feed feed, string id = "article") => new(account.AccountID,
        id, feed.FeedID, id, "Archived title", "<p>Archived body</p>", null, null,
        "https://example.test/article", null, "Summary", null, DateTime.UtcNow.Date, null,
        null, null, new ArticleStatus(id, read: false, starred: false, DateTime.UtcNow.Date));

    private sealed class RecordingRefresher : ILocalAccountRefresher
    {
        internal bool Refreshed { get; private set; }
        public Task RefreshAllAsync(IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Refreshed = true;
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    public void Dispose()
    {
        _storage.Dispose();
        if (Directory.Exists(_cloud)) Directory.Delete(_cloud, recursive: true);
    }
}
