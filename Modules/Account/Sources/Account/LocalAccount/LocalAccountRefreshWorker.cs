using WinNewsWire.ActivityLog;
using WinNewsWire.Articles;
using WinNewsWire.ArticlesDatabase;
using WinNewsWire.Core;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.Account;

public static class LocalAccountRefreshWorker
{
    public static async Task RunAsync(TextReader input, TextWriter output)
    {
        var writerGate = new object();
        void Send(LocalRefreshMessage message)
        {
            lock (writerGate)
            {
                output.WriteLine(LocalRefreshProtocol.Write(message));
                output.Flush();
            }
        }

        Send(new("ready", Guid.Empty, ProcessID: Environment.ProcessId));
        Account? account = null;
        LocalRefreshBatch? initial = null;
        CancellationTokenSource? cancellation = null;
        Task work = Task.CompletedTask;
        Guid current = Guid.Empty;
        try
        {
            while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var command = LocalRefreshProtocol.Read<LocalRefreshCommand>(line);
                if (command.Command == "cancel")
                {
                    if (command.ID == current) cancellation?.Cancel();
                    continue;
                }
                if (command.Command != "refresh" || command.Batch is not { } batch || command.ID == Guid.Empty)
                    throw new InvalidDataException("Unknown refresh worker command.");
                if (!work.IsCompleted) throw new InvalidOperationException("The refresh worker is already processing a batch.");
                await work.ConfigureAwait(false);
                Validate(batch);
                if (initial is not null && (batch.AccountID != initial.AccountID || batch.DatabasePath != initial.DatabasePath
                    || batch.Workspace != initial.Workspace))
                    throw new InvalidDataException("The refresh worker cannot change its account or database.");
                initial ??= batch;
                current = command.ID;
                cancellation?.Dispose();
                cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                work = Task.Run(async () =>
                {
                    long allocatedBefore = GC.GetTotalAllocatedBytes();
                    var pauseBefore = GC.GetTotalPauseDuration();
                    int parsedFeeds = 0, newArticles = 0, updatedArticles = 0;
                    var completedFeeds = new System.Collections.Concurrent.ConcurrentQueue<long>();
                    string result = "complete";
                    string? error = null;
                    EventHandler? activityChanged = null;
                    Action<Feed, ArticleChanges>? articlesChanged = null;
                    LocalAccountRefresher? refresher = null;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        AppConfig.SetDataDirectoryOverride(batch.Workspace);
                        AppDefaults.Shared.SetValue(LocalRefreshProtocol.OpenRssRefreshKey, batch.LastOpenRssRefresh);
                        account ??= new Account(batch.AccountID, AccountType.OnMyMac, batch.Name, new LocalAccountDelegate(),
                            settings: null, articlesDatabasePath: batch.DatabasePath);
                        account.Database.EmptyCaches();
                        account.TopLevelFeeds.Clear();
                        account.Folders.Clear();
                        account.TopLevelFeeds.AddRange(batch.Feeds.Select(feed => feed.Create(batch.AccountID)));
                        account.Name = batch.Name;
                        refresher = account.LocalRefresher as LocalAccountRefresher
                            ?? throw new InvalidOperationException("The worker requires the in-process backend refresher.");
                        refresher.DeleteOlderArticles = batch.DeleteOlderArticles;
                        var activityGate = new object();
                        var observed = new Dictionary<int, ActivityState>();
                        var preceding = ActivityLogger.Shared.Snapshot();
                        foreach (var activity in preceding.Completed) observed[activity.ID] = activity.State;
                        activityChanged = (_, _) =>
                        {
                            lock (activityGate)
                            {
                                var snapshot = ActivityLogger.Shared.Snapshot();
                                foreach (var activity in snapshot.Pending.Concat(snapshot.Running).Concat(snapshot.Completed))
                                {
                                    if (activity.Kind.Identifier != "refreshFeedContent" || activity.Kind.Url is not { } url) continue;
                                    if (observed.TryGetValue(activity.ID, out var state) && state == activity.State) continue;
                                    observed[activity.ID] = activity.State;
                                    Send(new("activity", command.ID, Activity: new(activity.ID, url, activity.Detail, activity.State,
                                        activity.StartDate is not null, activity.CompletionMessage, activity.Error?.ToString(),
                                        activity.ReturnedFromCache, activity.DurationIsSignificant)));
                                }
                            }
                        };
                        articlesChanged = (feed, changes) =>
                        {
                            Interlocked.Increment(ref parsedFeeds);
                            completedFeeds.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
                            Interlocked.Add(ref newArticles, changes.New?.Count ?? 0);
                            Interlocked.Add(ref updatedArticles, changes.Updated?.Count ?? 0);
                            Send(new("articles", command.ID, Feed: LocalFeedState.From(feed),
                                ChangedIDs: (changes.New ?? Enumerable.Empty<Article>()).Concat(changes.Updated ?? Enumerable.Empty<Article>())
                                    .Select(article => article.ArticleID).ToArray(),
                                NewIDs: changes.New?.Select(article => article.ArticleID).ToArray() ?? [],
                                DeletedIDs: changes.Deleted?.Select(article => article.ArticleID).ToArray() ?? []));
                        };
                        ActivityLogger.Shared.ActivityDidChange += activityChanged;
                        refresher.FeedUpdated += articlesChanged;
                        await refresher.RefreshAllAsync(new InlineProgress(progress => Send(new("progress", command.ID, Progress: progress))),
                            token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { result = "cancelled"; }
                    catch (Exception failure)
                    {
                        result = "failed";
                        error = failure.ToString();
                    }
                    finally
                    {
                        if (activityChanged is not null) ActivityLogger.Shared.ActivityDidChange -= activityChanged;
                        if (refresher is not null && articlesChanged is not null) refresher.FeedUpdated -= articlesChanged;
                    }
                    Send(new(result, command.ID, Feeds: account?.FlattenedFeeds().Select(LocalFeedState.From).ToArray() ?? [],
                        LastOpenRssRefresh: AppDefaults.Shared.GetValue<DateTime?>(LocalRefreshProtocol.OpenRssRefreshKey), Error: error,
                        Statistics: new(Environment.ProcessId, parsedFeeds, GC.GetTotalAllocatedBytes() - allocatedBefore,
                            (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds, newArticles, updatedArticles,
                            System.Diagnostics.Stopwatch.Frequency, completedFeeds.ToArray())));
                });
            }
        }
        finally
        {
            cancellation?.Cancel();
            try { await work.ConfigureAwait(false); }
            finally
            {
                cancellation?.Dispose();
                try { if (account is not null) await account.DisposeAsync().ConfigureAwait(false); }
                finally { if (initial is not null) LocalRefreshProtocol.RemoveWorkspace(initial.Workspace); }
            }
        }
    }

    private static void Validate(LocalRefreshBatch batch)
    {
        if (string.IsNullOrWhiteSpace(batch.AccountID) || batch.AccountID is "." or ".."
            || batch.AccountID.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !Path.IsPathFullyQualified(batch.DatabasePath) || !File.Exists(batch.DatabasePath)
            || Path.GetFileName(batch.DatabasePath) != "DB.sqlite3" || !LocalRefreshProtocol.IsWorkspace(batch.Workspace)
            || Path.GetFullPath(batch.DatabasePath).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(batch.Workspace))
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The refresh worker requires an existing account database and a separate private workspace.");
        if (batch.Feeds.Any(feed => string.IsNullOrEmpty(feed.FeedID))
            || batch.Feeds.Select(feed => feed.FeedID).Distinct().Count() != batch.Feeds.Length)
            throw new InvalidDataException("The refresh worker received invalid subscription identities.");
    }

    private sealed class InlineProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) => report(value);
    }
}
