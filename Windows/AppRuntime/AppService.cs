using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WinNewsWire.Account;
using WinNewsWire.AppShared.SmartFeeds;
using WinNewsWire.AppShared.Timer;
using WinNewsWire.Core;
using WinNewsWire.ErrorLog;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.AppShared.Favicons;
using WinNewsWire.ActivityLog;
using ActivityLogger = WinNewsWire.ActivityLog.ActivityLog;

namespace WinNewsWire.AppRuntime;

/// <summary>Top-level app runtime. Wires AccountManager, SmartFeedsController, refresh timer,
/// error log, undo manager, app defaults. One shared instance per process.</summary>
public sealed partial class AppService
{
    public static AppService Shared { get; } = new();

    public AccountManager Accounts => AccountManager.Shared;
    public SmartFeedsController SmartFeeds => SmartFeedsController.Shared;
    public AppDefaults Defaults => AppDefaults.Shared;
    public UndoManager Undo { get; } = new();
    public ArticleSpeechService Speech { get; } = new();
    public ErrorLogDatabase ErrorLog { get; }

    private readonly AccountRefreshTimer _refreshTimer;
    private readonly ArticleStatusSyncTimer _syncTimer;
    private readonly WinNewsWire.Services.NewArticleNotifier _notifier;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _startup = Task.CompletedTask;
#if PARITY_PROBE
    internal Task ProbeStartup => _startup;
    internal bool ProbeStarted => _started;
    internal int ProbeMemoryCacheCleanups;
#endif
    private Task? _shutdown;
    private bool _started;
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    private MemoryPressureMonitor? _memoryMonitor;
    private readonly object _maintenanceGate = new();
    private Task _maintenance = Task.CompletedTask;
    private Services.ArticleThumbnailService? _thumbnailImages;
    internal Services.ArticleThumbnailService ThumbnailImages
    {
        get
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            return _thumbnailImages ??= new(_dispatcher
                ?? throw new InvalidOperationException("Thumbnails require the application UI dispatcher."));
        }
    }

    private AppService()
    {
        Images.FeedImageDownloader.ConfigureSharedBackgroundNetworkPolicy(Services.ArticleThumbnailService.NetworkAllowsDownloads);
        ErrorLog = new ErrorLogDatabase(Path.Combine(AppConfig.LogsDirectory, "errors.sqlite3"));
        _refreshTimer = new AccountRefreshTimer(Accounts, (RefreshInterval)Defaults.RefreshIntervalRaw);
        _syncTimer = new ArticleStatusSyncTimer(Accounts, ArticleStatusSyncTimer.NormalInterval);
        _notifier = new WinNewsWire.Services.NewArticleNotifier(Accounts);
        Defaults.Changed += OnDefaultsChanged;
    }

    public void Start()
    {
        if (_shutdown is not null) throw new InvalidOperationException("The application runtime is stopping.");
        if (_started) return;
        _started = true;
        var errors = new List<(string Operation, Exception Error)>();
        try { CacheCleaner.PurgeIfNecessary(Defaults); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            errors.Add(("Purge image caches", error));
        }
        try { StartMemoryMonitoring(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            errors.Add(("Start memory monitoring", error));
        }
        _refreshTimer.Start();
        _syncTimer.Start();
        QueueAutomationUpdate();
        _startup = RefreshInitiallyAsync(_lifetime.Token, errors);
    }

    private async Task RefreshInitiallyAsync(CancellationToken ct, IReadOnlyList<(string Operation, Exception Error)> errors)
    {
        foreach (var (operation, error) in errors) await LogAsync(operation, error);
        if (ct.IsCancellationRequested) return;
        await _notifier.StartAsync();
        if (ct.IsCancellationRequested) return;
        try { await Accounts.RefreshAllAsync(ct: ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { await LogAsync("Initial refresh", ex); }
        if (ct.IsCancellationRequested) return;
        try { await SmartFeeds.RefreshAllAsync(); }
        catch (Exception ex) { await LogAsync("SmartFeeds refresh", ex); }
    }

    public Task StopAsync()
    {
        if (_shutdown is not null) return _shutdown;
        Stop();
        Defaults.Changed -= OnDefaultsChanged;
        return _shutdown = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        await StopAutomationAsync();
        await Speech.StopAsync();
        Task maintenance;
        lock (_maintenanceGate) maintenance = _maintenance;
        try
        {
            await Task.WhenAll(_refreshTimer.StopAsync(), _syncTimer.StopAsync(), _notifier.StopAsync(), _startup, maintenance,
                Accounts.DisposeAsync().AsTask(), StopImageDownloadsAsync());
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            await _refreshTimer.DisposeAsync();
            await _syncTimer.DisposeAsync();
            if (HTMLMetadata.HtmlMetadataDatabase.IsSharedCreated) HTMLMetadata.HtmlMetadataDatabase.Shared.Dispose();
            if (Images.ImageDatabase.IsSharedCreated) Images.ImageDatabase.Shared.Dispose();
        }
    }

    private async Task StopImageDownloadsAsync()
    {
        try
        {
            try
            {
                if (_thumbnailImages is not null) await _thumbnailImages.DisposeAsync();
            }
            finally { await Images.FeedImageDownloader.DisposeSharedAsync(); }
        }
        finally
        {
            if (HTMLMetadata.HtmlMetadataDownloader.IsSharedCreated) await HTMLMetadata.HtmlMetadataDownloader.Shared.DisposeAsync();
        }
    }

    public void Stop()
    {
        _memoryMonitor?.Dispose();
        _memoryMonitor = null;
        _lifetime.Cancel();
        _refreshTimer.Stop();
        _syncTimer.Stop();
        _ = _notifier.StopAsync();
        if (_automation is not null) _ = _automation.StopAsync();
    }

    internal void StartMemoryMonitoring()
    {
        if (_dispatcher is null) throw new InvalidOperationException("Memory-pressure handling requires the application's UI dispatcher.");
        _memoryMonitor ??= new(_dispatcher, ReleaseMemoryCaches);
        _memoryMonitor.Start();
    }

    internal void ReleaseMemoryCaches()
    {
#if PARITY_PROBE
        ProbeMemoryCacheCleanups++;
#endif
        var retained = App.MainWindows.SelectMany(window => window.TimelineArticles)
            .GroupBy(article => article.AccountID)
            .ToDictionary(group => group.Key, group => group.Select(article => article.ArticleID).ToHashSet(StringComparer.Ordinal));
        foreach (var account in Accounts.Accounts)
        {
            if (retained.TryGetValue(account.AccountID, out var ids)) account.Database.TrimArticleCache(ids);
            else account.Database.EmptyArticleCache();
        }
        Helpers.SingleLineTextFieldSizer.EmptyCache();
        Helpers.MultilineTextFieldSizer.EmptyCache();
        Helpers.IconView.EmptyCache();
        ArticleStringFormatter.Shared.EmptyCaches();
        FaviconDownloader.Shared.EmptyCache();
        _thumbnailImages?.EmptyCache();
        if (HTMLMetadata.HtmlMetadataDownloader.IsSharedCreated) HTMLMetadata.HtmlMetadataDownloader.Shared.EmptyCache();
        Web.Downloader.Shared.EmptyCache();
        Articles.AuthorCache.Shared.Clear();
    }

    public Task VacuumAllDatabasesAsync()
    {
        lock (_maintenanceGate)
        {
            if (_lifetime.IsCancellationRequested) throw new InvalidOperationException("The application is stopping.");
            if (_maintenance.IsCompleted) _maintenance = VacuumAllDatabasesCoreAsync(_lifetime.Token);
            return _maintenance;
        }
    }

    private async Task VacuumAllDatabasesCoreAsync(CancellationToken ct)
    {
        await ActivityLogger.Shared.LogActivityAsync(ActivityOwner.App, ActivityKind.VacuumDatabase,
            () => ErrorLog.VacuumAsync(ct), detail: Path.Combine("Logs", "errors.sqlite3"));
        ct.ThrowIfCancellationRequested();
        await Accounts.VacuumAccountDatabasesAsync(ct);
        await ActivityLogger.Shared.LogActivityAsync(ActivityOwner.App, ActivityKind.VacuumDatabase,
            () => HTMLMetadata.HtmlMetadataDatabase.Shared.VacuumAsync(ct), detail: "HTMLMetadata.db");
        await ActivityLogger.Shared.LogActivityAsync(ActivityOwner.App, ActivityKind.VacuumDatabase,
            () => Images.ImageDatabase.Shared.VacuumAsync(ct), detail: "Images.db");
    }

    private void OnDefaultsChanged(object? sender, string key)
    {
        if (key == AppDefaults.Key.RefreshInterval)
            _refreshTimer.Interval = (RefreshInterval)Defaults.RefreshIntervalRaw;
        if (key == AppDefaults.Key.AutomationEnabled)
        {
            if (_dispatcher?.HasThreadAccess == true) QueueAutomationUpdate();
            else if (_dispatcher?.TryEnqueue(QueueAutomationUpdate) != true)
                App.LogException("Update local automation", null, "The application dispatcher is unavailable.");
        }
    }

    public Task LogAsync(string operation, Exception ex)
        => ErrorLog.AddAsync(new ErrorLogEntry(0, DateTime.UtcNow, "App", 0, operation, "", "", 0, ex.ToString()));
}
