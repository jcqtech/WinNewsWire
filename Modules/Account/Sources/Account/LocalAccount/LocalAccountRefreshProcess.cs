using System.Diagnostics;
using WinNewsWire.ActivityLog;
using WinNewsWire.Core;
using WinNewsWire.Web;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;
using ActivityKind = WinNewsWire.ActivityLog.ActivityKind;

namespace WinNewsWire.Account;

public sealed class LocalAccountRefreshProcess(Account account, string executable) : ILocalAccountRefresher
{
    private static readonly SemaphoreSlim RefreshQueue = new(1, 1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposalGate = new();
    private Process? _process;
    private string? _workspace;
    private Task<string> _errors = Task.FromResult("");
    private Task? _disposal;
    private int _processID;
    private LocalRefreshStatistics? _statistics;

    public int? ProcessId => Volatile.Read(ref _processID) is > 0 and var id ? id : null;
    public bool CanDisposeSynchronously => ProcessId is null && _gate.CurrentCount == 1;
    public LocalRefreshStatistics? LastStatistics => Volatile.Read(ref _statistics);

    public Task RefreshAllAsync(IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        lock (_disposalGate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            return Task.Run(async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                var token = cancellation.Token;
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    Volatile.Write(ref _statistics, null);
                    // Preserve the application-wide OpenRSS allowance across local-account workers.
                    await RefreshQueue.WaitAsync(token).ConfigureAwait(false);
                    try { await RefreshCoreAsync(progress, token).ConfigureAwait(false); }
                    finally { RefreshQueue.Release(); }
                }
                finally { _gate.Release(); }
            });
        }
    }

    private async Task RefreshCoreAsync(IProgress<ProgressInfo>? progress, CancellationToken token)
    {
        var activities = new Dictionary<int, (int ID, bool Started)>();
        try
        {
            token.ThrowIfCancellationRequested();
            await StartAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var process = _process ?? throw new InvalidOperationException("The refresh worker did not start.");
            var workspace = _workspace ?? throw new InvalidOperationException("The refresh worker has no private workspace.");
            var feeds = account.FlattenedFeeds().ToDictionary(feed => feed.FeedID, StringComparer.Ordinal);
            var before = feeds.Values.Select(LocalFeedState.From).ToDictionary(feed => feed.FeedID, StringComparer.Ordinal);
            var id = Guid.NewGuid();
            var batch = new LocalRefreshBatch(account.AccountID, account.NameForDisplay,
                Path.Combine(account.AccountDirectory, "DB.sqlite3"), workspace, before.Values.ToArray(),
                AppDefaults.Shared.GetValue<DateTime?>(LocalRefreshProtocol.OpenRssRefreshKey));
            await process.StandardInput.WriteLineAsync(LocalRefreshProtocol.Write(new LocalRefreshCommand("refresh", id, batch)))
                .ConfigureAwait(false);
            using var cancelWait = new CancellationTokenSource();
            using var signals = CancellationTokenSource.CreateLinkedTokenSource(token, cancelWait.Token);
            var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, signals.Token);
            bool cancellationSent = false;
            try
            {
                while (true)
                {
                    var reading = process.StandardOutput.ReadLineAsync();
                    if (!cancellationSent && await Task.WhenAny(reading, cancelled).ConfigureAwait(false) == cancelled)
                    {
                        await process.StandardInput.WriteLineAsync(LocalRefreshProtocol.Write(new LocalRefreshCommand("cancel", id)))
                            .ConfigureAwait(false);
                        cancellationSent = true;
                    }
                    var line = cancellationSent
                        ? await reading.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false)
                        : await reading.ConfigureAwait(false);
                    if (line is null) throw new IOException("The refresh worker disconnected: " + await _errors.ConfigureAwait(false));
                    var message = LocalRefreshProtocol.Read<LocalRefreshMessage>(line);
                    if (message.Version != LocalRefreshProtocol.Version || message.ID != id)
                        throw new InvalidDataException("The refresh worker returned a message for a different protocol or batch.");
                    switch (message.Kind)
                    {
                        case "progress":
                            progress?.Report(message.Progress ?? throw new InvalidDataException("Missing worker progress."));
                            break;
                        case "activity":
                            ApplyActivity(message.Activity ?? throw new InvalidDataException("Missing worker activity."), activities);
                            break;
                        case "articles":
                        {
                            var state = message.Feed ?? throw new InvalidDataException("Missing refreshed feed metadata.");
                            ApplyFeed(state);
                            var ids = message.ChangedIDs ?? throw new InvalidDataException("Missing changed article identities.");
                            var newIDs = (message.NewIDs ?? throw new InvalidDataException("Missing new article identities."))
                                .ToHashSet(StringComparer.Ordinal);
                            if (ids.Length > 0)
                            {
                                var changed = await account.Database.ApplyExternalChangesAsync(ids).ConfigureAwait(false);
                                account.RaiseNewArticlesDownloaded(changed.Where(article => newIDs.Contains(article.ArticleID)).ToArray());
                            }
                            break;
                        }
                        case "complete":
                        case "cancelled":
                        case "failed":
                            var statistics = message.Statistics ?? throw new InvalidDataException("Missing worker refresh statistics.");
                            if (statistics.ProcessID != process.Id) throw new InvalidDataException("The refresh statistics came from a different process.");
                            Volatile.Write(ref _statistics, statistics);
                            foreach (var state in message.Feeds ?? throw new InvalidDataException("Missing final feed metadata."))
                                ApplyFeed(state);
                            var prior = AppDefaults.Shared.GetValue<DateTime?>(LocalRefreshProtocol.OpenRssRefreshKey);
                            if (message.LastOpenRssRefresh is { } last && (prior is null || last > prior))
                                AppDefaults.Shared.SetValue(LocalRefreshProtocol.OpenRssRefreshKey, last);
                            account.SaveChanges();
                            await account.RecalculateUnreadCountsAsync().ConfigureAwait(false);
                            if (message.Kind == "failed") throw new IOException("RSS refresh worker failed: " + message.Error);
                            token.ThrowIfCancellationRequested();
                            if (message.Kind == "cancelled") throw new IOException("The refresh worker cancelled an active request unexpectedly.");
                            return;
                        default:
                            throw new InvalidDataException("Unknown refresh worker response.");
                    }
                }
            }
            finally { cancelWait.Cancel(); }

            void ApplyFeed(LocalFeedState state)
            {
                if (!feeds.TryGetValue(state.FeedID, out var feed) || !before.TryGetValue(state.FeedID, out var previous))
                    throw new InvalidDataException("The refresh worker returned an unknown subscription.");
                state.ApplyTo(feed, previous);
                before[state.FeedID] = state;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (var activity in activities.Values)
                ActivityLogger.Shared.DidComplete(activity.ID, "Cancelled", durationIsSignificant: false);
            throw;
        }
        catch (Exception failure)
        {
            foreach (var activity in activities.Values) ActivityLogger.Shared.DidFail(activity.ID, failure);
            var failures = new List<Exception> { failure };
            // A worker can commit a transaction before its corresponding IPC message reaches us.
            if (ProcessId is not null)
            {
                try { await account.Database.EmptyArticleCacheAsync().ConfigureAwait(false); }
                catch (Exception invalidating) { failures.Add(invalidating); }
            }
            try { await StopAsync(checkExitCode: false).ConfigureAwait(false); }
            catch (Exception stopping) { failures.Add(stopping); }
            if (failures.Count > 1)
                throw new AggregateException("The refresh worker failed and could not be reconciled or stopped cleanly.", failures);
            throw;
        }
    }

    private void ApplyActivity(WorkerActivity activity, Dictionary<int, (int ID, bool Started)> activities)
    {
        if (!activities.TryGetValue(activity.ID, out var local))
        {
            local = (ActivityLogger.Shared.CreateActivity(account.ActivityOwner, ActivityKind.RefreshFeedContent(activity.Url),
                activity.Detail), false);
            activities.Add(activity.ID, local);
        }
        if (!local.Started && (activity.State == ActivityState.Running || activity.Started))
        {
            ActivityLogger.Shared.DidStart(local.ID);
            local = (local.ID, true);
            activities[activity.ID] = local;
        }
        if (activity.State == ActivityState.Completed)
        {
            ActivityLogger.Shared.DidComplete(local.ID, activity.Message, activity.DurationIsSignificant, activity.ReturnedFromCache);
            activities.Remove(activity.ID);
        }
        else if (activity.State == ActivityState.Failed)
        {
            var error = new IOException(activity.Error ?? "The feed refresh failed in the worker.");
            ActivityLogger.Shared.DidFail(local.ID, error);
            activities.Remove(activity.ID);
            Trace.TraceError($"Feed refresh worker failed for {activity.Url}: {error}");
        }
    }

    private async Task StartAsync()
    {
        if (_process is not null && !_process.HasExited) return;
        if (_process is not null) await StopAsync(checkExitCode: false).ConfigureAwait(false);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The WinNewsWire refresh worker is missing. Rebuild or repair the application.", executable);
        _workspace = Path.Combine(Path.GetTempPath(), "WinNewsWire-Refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _process = new Process
        {
            StartInfo = new()
            {
                FileName = executable, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        _process.StartInfo.ArgumentList.Add("--stdio");
        _process.StartInfo.Environment.Remove("DOTNET_DiagnosticPorts");
        _process.StartInfo.Environment.Remove("DOTNET_DefaultDiagnosticPortSuspend");
        if (!_process.Start()) throw new IOException("The refresh worker could not be started.");
        Volatile.Write(ref _processID, _process.Id);
        _errors = _process.StandardError.ReadToEndAsync();
        var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false)
            ?? throw new IOException("The refresh worker exited during startup: " + await _errors.ConfigureAwait(false));
        var ready = LocalRefreshProtocol.Read<LocalRefreshMessage>(line);
        if (ready.Kind != "ready" || ready.Version != LocalRefreshProtocol.Version || ready.ProcessID != _process.Id
            || ready.ProcessID == Environment.ProcessId)
            throw new InvalidDataException("The refresh worker did not establish an independent process.");
    }

    private async Task StopAsync(bool checkExitCode)
    {
        var process = _process;
        bool started = Volatile.Read(ref _processID) > 0;
        _process = null;
        Volatile.Write(ref _processID, 0);
        try
        {
            if (process is not null && started)
            {
                if (!process.HasExited) process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    throw new TimeoutException("The owned refresh worker did not stop after its input pipe closed.");
                }
                var error = await _errors.ConfigureAwait(false);
                if (checkExitCode && process.ExitCode != 0)
                    throw new IOException($"The refresh worker exited with code {process.ExitCode}: {error}");
            }
        }
        finally
        {
            process?.Dispose();
            if (_workspace is { } workspace)
            {
                _workspace = null;
                LocalRefreshProtocol.RemoveWorkspace(workspace);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            if (_disposal is not null) return new(_disposal);
            _lifetime.Cancel();
            return new(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopAsync(checkExitCode: true).ConfigureAwait(false); }
        finally
        {
            _lifetime.Dispose();
            _gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_disposalGate)
        {
            if (_disposal is not null)
            {
                if (!_disposal.IsCompleted) throw new InvalidOperationException("Await refresh-worker disposal before closing the account.");
                _disposal.GetAwaiter().GetResult();
                return;
            }
            if (!CanDisposeSynchronously) throw new InvalidOperationException("Use DisposeAsync to stop the refresh worker.");
            _lifetime.Cancel();
            _lifetime.Dispose();
            _disposal = Task.CompletedTask;
        }
    }
}
