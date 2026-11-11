using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WinNewsWire.Articles;
using Xunit;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.Account.Tests.LocalAccount;

public sealed class LocalRefreshProcessTests : IAsyncLifetime
{
    private readonly TestAccountManager _manager = new();
    private readonly Func<Account, ILocalAccountRefresher>? _previous = Account.LocalRefresherFactory;
    private readonly Account _account;
    private static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "RefreshWorker", "WinNewsWire.RefreshWorker.exe");

    public LocalRefreshProcessTests()
    {
        Account.LocalRefresherFactory = account => new LocalAccountRefreshProcess(account, WorkerPath);
        _account = _manager.CreateAccount();
    }

    [Fact]
    public async Task RefreshUsesAnIndependentProcessAndPreservesSubscriptionMetadata()
    {
        using var server = OpenServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var folder = _account.AddFolder("Keep folder");
        var feed = _account.AddFeed(Url(server), "Old publisher", folder);
        feed.EditedName = "My feed name";
        feed.NewArticleNotificationsEnabled = true;
        feed.ReaderViewAlwaysEnabled = true;
        _account.SaveChanges();
        int downloaded = 0;
        _account.NewArticlesDownloaded += (_, args) => downloaded += args.NewArticles.Count;
        var response = LocalOpmlRefreshTests.ServeFeedAsync(server, timeout.Token, etag: "\"first\"");
        await _account.RefreshAllAsync(ct: timeout.Token);
        await response;

        Assert.NotNull(_account.RefreshProcessId);
        Assert.NotEqual(Environment.ProcessId, _account.RefreshProcessId);
        var statistics = Assert.IsType<LocalRefreshStatistics>(_account.LastRefreshStatistics);
        Assert.Equal(_account.RefreshProcessId, statistics.ProcessID);
        Assert.Equal(1, statistics.ParsedFeeds);
        Assert.Equal(1, statistics.NewArticles);
        Assert.True(statistics.AllocatedBytes > 0);
        Assert.Equal(Stopwatch.Frequency, statistics.ClockFrequency);
        Assert.True(Assert.Single(statistics.FeedCompletedTimestamps) > 0);
        Assert.Single(await _account.Database.FetchArticlesAsync(feed.FeedID));
        Assert.Equal(1, downloaded);
        Assert.Equal(1, feed.UnreadCount);
        Assert.Equal("Publisher", feed.Name);
        Assert.Equal("My feed name", feed.NameForDisplay);
        Assert.Same(folder, Assert.Single(_account.Folders));
        Assert.Same(feed, Assert.Single(folder.Feeds));
        Assert.True(feed.NewArticleNotificationsEnabled);
        Assert.True(feed.ReaderViewAlwaysEnabled);
        Assert.Equal("\"first\"", feed.ETag);
        Assert.StartsWith("v4-native-parsers:", feed.ContentHash);
        Assert.Contains("Keep folder", File.ReadAllText(_account.OpmlFile!.FilePath));
        Assert.Empty(ActivityLogger.Shared.PendingActivitiesFor(_account.ActivityOwner));
        Assert.Empty(ActivityLogger.Shared.RunningActivitiesFor(_account.ActivityOwner));
    }

    [Fact]
    public async Task RepeatedRefreshKeepsWorkerAndConditionalRequests()
    {
        using var server = OpenServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var feed = _account.AddFeed(Url(server));
        var first = LocalOpmlRefreshTests.ServeFeedAsync(server, timeout.Token, etag: "\"version\"");
        await _account.RefreshAllAsync(ct: timeout.Token);
        await first;
        var pid = _account.RefreshProcessId;
        var hash = feed.ContentHash;
        feed.LastCheckDate = DateTime.UtcNow.AddDays(-3);
        var second = LocalOpmlRefreshTests.ServeFeedAsync(server, timeout.Token, content: "", status: 304, etag: "\"version\"");
        await _account.RefreshAllAsync(ct: timeout.Token);
        Assert.Contains(await second, header => header.Equals("If-None-Match: \"version\"", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(pid, _account.RefreshProcessId);
        Assert.Equal(hash, feed.ContentHash);
        Assert.Single(await _account.Database.FetchArticlesAsync(feed.FeedID));
    }

    [Fact]
    public async Task ReadingAndStarringDuringRefreshKeepCanonicalStatusAndNewContent()
    {
        using var server = OpenServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var feed = _account.AddFeed(Url(server));
        var first = LocalOpmlRefreshTests.ServeFeedAsync(server, timeout.Token);
        await _account.RefreshAllAsync(ct: timeout.Token);
        await first;
        var visible = Assert.Single(await _account.Database.FetchArticlesAsync(feed.FeedID));
        Reset(feed);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = LocalOpmlRefreshTests.ServeFeedAsync(server, timeout.Token, Feed("Updated", "Changed content"),
            beforeResponse: async (_, ct) => { received.TrySetResult(); await release.Task.WaitAsync(ct); });
        var refresh = _account.RefreshAllAsync(ct: timeout.Token);
        try
        {
            await received.Task.WaitAsync(timeout.Token);
            await _account.MarkAsync([visible.ArticleID], ArticleStatus.Key.Read, true, timeout.Token);
            await _account.MarkAsync([visible.ArticleID], ArticleStatus.Key.Starred, true, timeout.Token);
            feed.EditedName = "Edited while refreshing";
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(refresh, response);
        var updated = Assert.Single(await _account.Database.FetchArticlesAsync(feed.FeedID));
        Assert.NotSame(visible, updated);
        Assert.Same(visible.Status, updated.Status);
        Assert.True(updated.Status.Read);
        Assert.True(updated.Status.Starred);
        Assert.Equal("Updated", updated.Title);
        Assert.Equal("Changed content", updated.ContentHtml);
        Assert.Equal("Edited while refreshing", feed.NameForDisplay);
        _account.Database.EmptyCaches();
        var persisted = Assert.Single(await _account.Database.FetchArticlesAsync(feed.FeedID));
        Assert.True(persisted.Status.Read);
        Assert.True(persisted.Status.Starred);
    }

    [Fact]
    public async Task CancellationDrainsWorkerAndAllowsAnotherBatch()
    {
        using var server = OpenServer();
        using var serving = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var feed = _account.AddFeed(Url(server));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = LocalOpmlRefreshTests.ServeFeedAsync(server, serving.Token,
            beforeResponse: async (_, ct) => { received.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        var refresh = _account.RefreshAllAsync(ct: cancellation.Token);
        await received.Task.WaitAsync(serving.Token);
        var pid = _account.RefreshProcessId;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        serving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.Empty(ActivityLogger.Shared.RunningActivitiesFor(_account.ActivityOwner));

        Reset(feed);
        using var retry = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = LocalOpmlRefreshTests.ServeFeedAsync(server, retry.Token);
        await _account.RefreshAllAsync(ct: retry.Token);
        await response;
        Assert.Equal(pid, _account.RefreshProcessId);
        Assert.Single(await _account.Database.FetchArticlesAsync(feed.FeedID));
    }

    [Fact]
    public async Task UnexpectedWorkerExitIsReportedAndNextRefreshRestartsIt()
    {
        using var server = OpenServer();
        using var serving = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var feed = _account.AddFeed(Url(server));
        var old = new Article(_account.AccountID, "cached", feed.FeedID, "cached", "Cached", "Body", null, null,
            null, null, null, null, DateTime.UnixEpoch, null, null, null, new ArticleStatus("cached", false, false, DateTime.UtcNow));
        await _account.Database.UpdateArticlesAsync([old]);
        var cached = Assert.Single(await _account.Database.FetchArticlesByIDsAsync(["cached"]));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = LocalOpmlRefreshTests.ServeFeedAsync(server, serving.Token,
            beforeResponse: async (_, ct) => { received.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        var refresh = _account.RefreshAllAsync(ct: serving.Token);
        await received.Task.WaitAsync(serving.Token);
        int pid = _account.RefreshProcessId!.Value;
        using (var worker = Process.GetProcessById(pid))
        {
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(serving.Token);
        }
        await Assert.ThrowsAsync<IOException>(() => refresh);
        serving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.Null(_account.RefreshProcessId);
        Assert.Empty(ActivityLogger.Shared.RunningActivitiesFor(_account.ActivityOwner));
        var recovered = Assert.Single(await _account.Database.FetchArticlesByIDsAsync(["cached"]));
        Assert.NotSame(cached, recovered);
        Assert.Same(cached.Status, recovered.Status);

        Reset(feed);
        using var retry = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = LocalOpmlRefreshTests.ServeFeedAsync(server, retry.Token);
        await _account.RefreshAllAsync(ct: retry.Token);
        await response;
        Assert.NotEqual(pid, _account.RefreshProcessId);
    }

    [Fact]
    public async Task AccountShutdownDrainsAndStopsItsWorker()
    {
        using var server = OpenServer();
        using var serving = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _account.AddFeed(Url(server));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = LocalOpmlRefreshTests.ServeFeedAsync(server, serving.Token,
            beforeResponse: async (_, ct) => { received.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        var refresh = _account.RefreshAllAsync(ct: serving.Token);
        await received.Task.WaitAsync(serving.Token);
        using var worker = Process.GetProcessById(_account.RefreshProcessId!.Value);
        await _account.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.True(worker.HasExited);
        Assert.Null(_account.RefreshProcessId);
        serving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Fact]
    public async Task MissingWorkerIsAnExplicitErrorRatherThanAnInProcessFallback()
    {
        Account.LocalRefresherFactory = account => new LocalAccountRefreshProcess(account,
            Path.Combine(_account.AccountDirectory, "missing-worker.exe"));
        _account.AddFeed("https://example.com/feed");
        await Assert.ThrowsAsync<FileNotFoundException>(() => _account.RefreshAllAsync());
        Assert.Null(_account.RefreshProcessId);
    }

    [Fact]
    public async Task ParentPipeClosureCancelsTheWorkerAndRemovesItsPrivateMetadata()
    {
        using var server = OpenServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var feed = _account.AddFeed(Url(server));
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = LocalOpmlRefreshTests.ServeFeedAsync(server, timeout.Token,
            beforeResponse: async (_, ct) => { received.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); });
        string workspace = Path.Combine(Path.GetTempPath(), "WinNewsWire-Refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        using var worker = new Process
        {
            StartInfo = new(WorkerPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        worker.StartInfo.ArgumentList.Add("--stdio");
        Assert.True(worker.Start());
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            var ready = LocalRefreshProtocol.Read<LocalRefreshMessage>(
                (await worker.StandardOutput.ReadLineAsync().WaitAsync(timeout.Token))!);
            Assert.Equal("ready", ready.Kind);
            var command = new LocalRefreshCommand("refresh", Guid.NewGuid(),
                new(_account.AccountID, _account.Name, Path.Combine(_account.AccountDirectory, "DB.sqlite3"),
                    workspace, [LocalFeedState.From(feed)], null));
            await worker.StandardInput.WriteLineAsync(LocalRefreshProtocol.Write(command));
            var output = worker.StandardOutput.ReadToEndAsync();
            await received.Task.WaitAsync(timeout.Token);
            worker.StandardInput.Close();
            await worker.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, worker.ExitCode);
            Assert.Contains("\"Kind\":\"cancelled\"", await output);
            Assert.Empty(await errors);
            Assert.False(Directory.Exists(workspace));
        }
        finally
        {
            timeout.Cancel();
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            LocalRefreshProtocol.RemoveWorkspace(workspace);
        }
    }

    private static TcpListener OpenServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static string Url(TcpListener listener) => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/feed";
    private static string Feed(string title, string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel><title>Publisher</title><link>https://example.com/</link><description>Feed</description>
        <item><guid>item-1</guid><title>{title}</title><link>https://example.com/article</link><description>{body}</description></item>
        </channel></rss>
        """;
    private static void Reset(Feed feed)
    {
        feed.LastCheckDate = DateTime.UtcNow.AddDays(-3);
        feed.ContentHash = null;
        feed.CacheControlInfo = null;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        try { await _account.DisposeAsync(); }
        finally
        {
            Account.LocalRefresherFactory = _previous;
            _manager.Dispose();
        }
    }
}
