using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.ArticleExtractor;
using WinNewsWire.AppShared.ArticleRendering;
using WinNewsWire.AppShared.Favicons;
using WinNewsWire.AppShared.SmartFeeds;
using WinNewsWire.AppShared.Extensions;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.AppShared.Commands;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Models;
using AccountNs = WinNewsWire.Account;

namespace WinNewsWire.ViewModels;

/// <summary>
/// Primary view-model for <see cref="MainContent"/>. Binds to
/// <see cref="AccountNs.AccountManager"/> so feeds added via Preferences (or any other account
/// type) show up in the sidebar, and reading-state changes flow through
/// <see cref="AccountNs.Account.MarkAsync"/> into SQLite + pending-sync queues.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly HashSet<AccountNs.Account> _observedAccounts = new();
    private int _accountUpdateQueued;
    private long _lastAccountChange;
    private int _urgentAccountUpdate;
    private volatile bool _disposed;
    private Images.FeedImageDownloader? _feedImages;
    private readonly HashSet<string> _changedFeedImagePaths = new(StringComparer.OrdinalIgnoreCase);
    private int _articleLoadGeneration;
    private bool _restoringArticleSelection;
    private bool _changingSelection;
    private bool _rebindingSelection;
    private ReadFilterState _readFilters;
    private bool _restoringWindowState;
    private bool _refreshAfterRestoration;
    private UnreadCountDisplay _unreadDisplayMode = AppDefaults.Shared.GetUnreadCountDisplay();
    private Task _readerModeTask = Task.CompletedTask;
    private Task _automaticReadTask = Task.CompletedTask;
    private Task _timelineRefreshTask = Task.CompletedTask;
    private SidebarItemKey?[] _displayedSidebarKeys = [];
    private string? _displayedQuery;
    private bool _displayedGlobalSearch;
    internal Task PendingAutomaticReads => _automaticReadTask;
#if PARITY_PROBE
    internal Images.FeedImageDownloader? ProbeFeedImages
    {
        get => _feedImages;
        set => ObserveFeedImages(value);
    }
    internal int ProbeSectionStatusUpdates;
    internal int ProbeSubscriptionRefreshes;
    internal Action? ProbePreparingTimeline;
    internal Task ProbeDrainViewUpdatesAsync()
    {
        var barrier = new MainThreadAsyncOperation(_ => Task.CompletedTask, "Reader regression barrier");
        if (!_dispatcher.TryEnqueue(() => _viewOperations.Add(barrier)))
            throw new InvalidOperationException("The reader dispatcher is unavailable.");
        return barrier.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }
#endif
    private readonly WinNewsWire.Core.MainThreadOperationQueue _viewOperations;
    public WinNewsWire.Core.UndoManager UndoManager { get; } = new();
    public bool CanUndo => UndoManager.CanUndo;
    public bool CanRedo => UndoManager.CanRedo;
    public bool CanToggleSelectedRead => SelectedArticles.Any(item => !item.IsRead || item.SourceArticle is { } article && CanMarkUnread(article));
    public string UndoTitle => UndoManager.UndoActionName is { } name ? "Undo " + name : "Undo";
    public string RedoTitle => UndoManager.RedoActionName is { } name ? "Redo " + name : "Redo";
    public bool IsRestoringArticleSelection => _restoringArticleSelection;
    internal event EventHandler? SelectionNavigationStarting;
    internal event EventHandler? TimelineArticlesChanged;
    public ArticleSortParameters SortParameters { get; private set; }
    public bool UseColumnLayout => AppDefaults.Shared.UseColumnLayout;
    public TimelineThumbnailsDisplay TimelineThumbnailsDisplay => AppDefaults.Shared.GetTimelineThumbnailsDisplay();
    public TimelineFeedIconsDisplay TimelineFeedIconsDisplay => AppDefaults.Shared.GetTimelineFeedIconsDisplay();
    public UnreadCountDisplay UnreadDisplayMode => _unreadDisplayMode;
    public bool ShowHeaderUnreadCount => _unreadDisplayMode == UnreadCountDisplay.Count && ArticleListUnreadCount > 0;
    public string HeaderUnreadCountText => ShowHeaderUnreadCount ? _unreadDisplayMode.TextFor(ArticleListUnreadCount)! : "";
    public string HeaderUnreadAccessibilityText => ShowHeaderUnreadCount ? _unreadDisplayMode.AccessibilityTextFor(ArticleListUnreadCount)! : "";
    internal bool HasExtractedArticle => _readerModeExtracted is not null;
    // Sidebar
    public ObservableCollection<SidebarItem> SidebarItems { get; } = new();

    // Article list
    [ObservableProperty]
    private ObservableCollection<FeedItem> _articleItems = new();

    [ObservableProperty]
    private FeedItem? _selectedArticle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedArticles))]
    [NotifyPropertyChangedFor(nameof(CanToggleSelectedRead))]
    [NotifyCanExecuteChangedFor(nameof(MarkSelectedReadCommand))]
    private IReadOnlyList<FeedItem> _selectedArticles = Array.Empty<FeedItem>();

    public bool HasSelectedArticles => SelectedArticles.Count > 0;

    [ObservableProperty]
    private string _articleSelectionMessage = "Select an article to read";

    [ObservableProperty]
    private bool _searchAllAccounts = WinNewsWire.Core.AppDefaults.Shared.SearchAllAccounts;

    [ObservableProperty]
    private SidebarItem? _selectedSidebarItem;
    private IReadOnlyList<SidebarItem> _selectedSidebarItems = [];
    private bool _changingSidebarSelection;
    public IReadOnlyList<SidebarItem> SelectedSidebarItems => _selectedSidebarItems;
    public bool HasMultipleSidebarSelection => SelectedSidebarItems.Count > 1;

    [ObservableProperty]
    private string _articleListTitle = "All Articles";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeaderUnreadCount))]
    [NotifyPropertyChangedFor(nameof(HeaderUnreadCountText))]
    [NotifyPropertyChangedFor(nameof(HeaderUnreadAccessibilityText))]
    private int _articleListUnreadCount;

    // Content pane
    [ObservableProperty]
    private string _articleHtml = string.Empty;

    public const string ThemeResourceHost = "winnewswire-theme.local";
    public const string ThemeStylesheetPath = "/stylesheet.css";
    public const string ArticleIconUrl = "https://winnewswire.local/article-icon";
    public ArticleRendering? CurrentRendering { get; private set; }

    // UI state
    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarReadFilterLabel))]
    private bool _isSidebarReadFiltered;

    public string SidebarReadFilterLabel => IsSidebarReadFiltered ? "Show Read Feeds" : "Hide Read Feeds";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _sidebarStatus = "No feeds";

    [ObservableProperty]
    private string _timelineStatus = "No articles";

    [ObservableProperty]
    private string _detailStatus = "";

    [ObservableProperty]
    private bool _isUnifiedLayout = WinNewsWire.Core.AppDefaults.Shared.UnifiedLayout;

    partial void OnIsUnifiedLayoutChanged(bool value)
    {
        if (!_restoringWindowState) AppDefaults.Shared.UnifiedLayout = value;
    }

    public MainViewModel()
    {
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Create the main view-model on the UI thread.");
        _viewOperations = new WinNewsWire.Core.MainThreadOperationQueue(
            SynchronizationContext.Current ?? new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(_dispatcher));
        _readFilters = new ReadFilterState(AppService.Shared.Defaults.ReadFilters);
        var defaults = AppService.Shared.Defaults;
        if (!Enum.TryParse<ArticleSortKey>(defaults.TimelineSortKey, out var sortKey) || !Enum.IsDefined(sortKey))
        {
            System.Diagnostics.Trace.TraceWarning($"Unknown saved sort key: {defaults.TimelineSortKey}.");
            sortKey = ArticleSortKey.Date;
        }
        SortParameters = sortKey == ArticleSortKey.Date && defaults.TimelineGroupByFeed
            ? new(ArticleSortKey.Feed, false)
            : new(sortKey, defaults.TimelineSortDirection != "ascending");
        UndoManager.StackChanged += OnUndoHistoryChanged;
        AppService.Shared.Accounts.AccountsDidChange += OnAccountChanged;
        ObserveAccounts();
        // Re-render the visible article whenever the article text-size preference
        // changes, so the new size takes effect immediately without requiring the
        // user to navigate away and back.
        WinNewsWire.Core.AppDefaults.Shared.Changed += OnDefaultsChanged;
    }

    private void OnDefaultsChanged(object? sender, string key)
    {
        if (_disposed) return;
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => OnDefaultsChanged(sender, key));
            return;
        }
        if (key is WinNewsWire.Core.AppDefaults.Key.ArticleTextSize or WinNewsWire.Core.AppDefaults.Key.CurrentThemeName)
            RefreshArticleHtml();
        if (key == AppDefaults.Key.UseColumnLayout) OnPropertyChanged(nameof(UseColumnLayout));
        if (key == AppDefaultsSharedExtensions.TimelineThumbnailsDisplayKey) OnPropertyChanged(nameof(TimelineThumbnailsDisplay));
        if (key == AppDefaultsSharedExtensions.TimelineFeedIconsDisplayKey) OnPropertyChanged(nameof(TimelineFeedIconsDisplay));
        if (key == AppShared.Sidebar.SidebarFeedOrder.PreferenceKey) BuildSidebar();
        if (key == AppDefaultsSharedExtensions.UnreadCountDisplayKey)
        {
            _unreadDisplayMode = AppDefaults.Shared.GetUnreadCountDisplay();
            void Update(SidebarItem item)
            {
                item.UpdateUnreadCountDisplay(_unreadDisplayMode);
                foreach (var child in item.Children) Update(child);
            }
            foreach (var item in SidebarItems) Update(item);
            OnPropertyChanged(nameof(UnreadDisplayMode));
            OnPropertyChanged(nameof(ShowHeaderUnreadCount));
            OnPropertyChanged(nameof(HeaderUnreadCountText));
            OnPropertyChanged(nameof(HeaderUnreadAccessibilityText));
            UpdateSectionStatus();
        }
    }

    private void OnUndoHistoryChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTitle));
        OnPropertyChanged(nameof(RedoTitle));
    }

    private void ObserveAccounts()
    {
        var accounts = AppService.Shared.Accounts.Accounts.ToHashSet();
        foreach (var account in _observedAccounts.Except(accounts).ToList())
        {
            UnobserveAccount(account);
            _observedAccounts.Remove(account);
        }
        foreach (var account in accounts.Except(_observedAccounts))
        {
            account.AccountStructureChanged += OnAccountChanged;
            account.UnreadCountChanged += OnUnreadCountChanged;
            account.NewArticlesDownloaded += OnNewArticlesDownloaded;
            account.StatusesChanged += OnArticleStatusesChanged;
            _observedAccounts.Add(account);
        }
    }

    private void OnArticleStatusesChanged(object? sender, AccountNs.StatusesChangedEventArgs args)
        => OnUnreadCountChanged(sender, args);

    private void OnNewArticlesDownloaded(object? sender, AccountNs.NewArticlesEventArgs args)
    {
        if (_disposed) return;
        var feeds = args.NewArticles.Select(article => (article.AccountID, article.FeedID)).ToHashSet();
        void Update()
        {
            if (_disposed) return;
            bool relevant = SelectedSidebarItems.Count == 0 || SearchAllAccounts && !string.IsNullOrWhiteSpace(SearchQuery)
                || SelectedSidebarItems.Any(item => item.Tag switch
                {
                    IPseudoFeed => true,
                    AccountNs.Feed feed => feeds.Contains((feed.AccountID, feed.FeedID)),
                    AccountNs.Folder folder => folder.Feeds.Any(feed => feeds.Contains((feed.AccountID, feed.FeedID))),
                    AccountNs.Account account => feeds.Any(feed => feed.AccountID == account.AccountID),
                    _ => false
                });
            if (relevant) OnAccountChanged(sender, args);
        }
        if (_dispatcher.HasThreadAccess) Update();
        else if (!_dispatcher.TryEnqueue(Update))
            System.Diagnostics.Trace.TraceWarning("The closed reader could not schedule a downloaded-article update.");
    }

    private void OnUnreadCountChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess) UpdateArticleStatusPresentation();
        else _dispatcher.TryEnqueue(UpdateArticleStatusPresentation);
    }

    private void UpdateArticleStatusPresentation()
    {
        if (_disposed) return;
        // Read committed statuses, not queued notification values that may already be superseded.
        foreach (var item in ArticleItems)
        {
            if (item.SourceArticle is not { } article) continue;
            item.IsRead = article.Status.Read;
            item.IsStarred = article.Status.Starred;
        }
        UpdateSidebarUnreadCounts();
        OnPropertyChanged(nameof(CanToggleSelectedRead));
        MarkSelectedReadCommand.NotifyCanExecuteChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        long queuedAt = Environment.TickCount64;
        Interlocked.Exchange(ref _lastAccountChange, queuedAt);
        if (args is not AccountNs.NewArticlesEventArgs) Interlocked.Exchange(ref _urgentAccountUpdate, 1);
        if (Interlocked.Exchange(ref _accountUpdateQueued, 1) != 0) return;
        if (!_dispatcher.TryEnqueue(() => _viewOperations.Add(new WinNewsWire.Core.MainThreadAsyncOperation(async cancellation =>
        {
            bool updating = false;
            try
            {
                while (Volatile.Read(ref _urgentAccountUpdate) == 0)
                {
                    long now = Environment.TickCount64;
                    long delay = Math.Min(500 - (now - Interlocked.Read(ref _lastAccountChange)), 2000 - (now - queuedAt));
                    if (delay <= 0) break;
                    await Task.Delay((int)delay, cancellation);
                }
                await UserInputIdle.WaitAsync(cancellation);
                updating = true;
                Interlocked.Exchange(ref _urgentAccountUpdate, 0);
                Interlocked.Exchange(ref _accountUpdateQueued, 0);
                if (_disposed) return;
                if (_restoringWindowState) { _refreshAfterRestoration = true; return; }
#if PARITY_PROBE
                ProbeSubscriptionRefreshes++;
#endif
                ObserveAccounts();
                BuildSidebar();
                await FilterArticlesAsync();
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException
                or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                StatusMessage = $"Could not update subscriptions: {ex.Message}";
                await AppService.Shared.LogAsync("Update subscriptions", ex);
            }
            finally
            {
                if (!updating) Interlocked.Exchange(ref _accountUpdateQueued, 0);
            }
        }, "Update subscriptions"))))
        {
            Interlocked.Exchange(ref _accountUpdateQueued, 0);
        }
    }

    private void UnobserveAccount(AccountNs.Account account)
    {
        account.AccountStructureChanged -= OnAccountChanged;
        account.UnreadCountChanged -= OnUnreadCountChanged;
        account.NewArticlesDownloaded -= OnNewArticlesDownloaded;
        account.StatusesChanged -= OnArticleStatusesChanged;
    }

    public void Dispose()
    {
        _disposed = true;
        ObserveFeedImages(null);
        _readerModeCancellation?.Cancel();
        _viewOperations.CancelAll();
        AppService.Shared.Accounts.AccountsDidChange -= OnAccountChanged;
        WinNewsWire.Core.AppDefaults.Shared.Changed -= OnDefaultsChanged;
        UndoManager.StackChanged -= OnUndoHistoryChanged;
        foreach (var account in _observedAccounts) UnobserveAccount(account);
        _observedAccounts.Clear();
    }

    private static ArticleTextSize CurrentArticleTextSize
        => WinNewsWire.Core.AppDefaults.Shared.ArticleTextSizeRaw is >= 0 and <= 4
            ? (ArticleTextSize)WinNewsWire.Core.AppDefaults.Shared.ArticleTextSizeRaw : ArticleTextSize.Medium;

    [RelayCommand]
    private void ToggleUnifiedLayout() => IsUnifiedLayout = !IsUnifiedLayout;

    public static ArticleIdentityKey Identity(FeedItem item) => new(item.AccountID, item.Id);

    public void SetSelectedArticles(IEnumerable<FeedItem> selection)
    {
        var ordered = ArticleSelection.InTimelineOrder(ArticleItems, selection.Select(Identity), Identity);
        bool sameIdentity = SelectedArticles.Select(Identity).SequenceEqual(ordered.Select(Identity));
        if (!sameIdentity) NotifySelectionNavigationStarting();
        _changingSelection = true;
        _rebindingSelection = sameIdentity;
        try
        {
            SelectedArticles = ordered;
            ArticleSelectionMessage = ordered.Count > 1 ? $"{ordered.Count} articles selected" : "Select an article to read";
            var single = ordered.Count == 1 ? ordered[0] : null;
            SelectedArticle = single;
        }
        finally { _changingSelection = false; _rebindingSelection = false; }
        UpdateSectionStatus();
    }

    private void NotifySelectionNavigationStarting()
    {
        if (!_restoringArticleSelection && !_restoringWindowState && !_changingSidebarSelection)
            SelectionNavigationStarting?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedArticleChanging(FeedItem? oldValue, FeedItem? newValue)
    {
        if (!_changingSelection && (oldValue is null ? (ArticleIdentityKey?)null : Identity(oldValue))
            != (newValue is null ? (ArticleIdentityKey?)null : Identity(newValue)))
            NotifySelectionNavigationStarting();
    }

    partial void OnSelectedArticleChanged(FeedItem? value)
    {
        if (_rebindingSelection) { RefreshArticleHtml(); return; }
        HoveredLink = null;
        _readerModeCancellation?.Cancel();
        if (!_changingSelection)
        {
            SelectedArticles = value is null ? Array.Empty<FeedItem>() : new[] { value };
            ArticleSelectionMessage = "Select an article to read";
        }
        if (value is null) { ArticleHtml = string.Empty; _readerModeExtracted = null; UpdateSectionStatus(); return; }
        if (!value.IsRead && !_restoringArticleSelection)
        {
            var marking = MarkAutomaticallyReadAsync(value);
            _automaticReadTask = _automaticReadTask.IsCompleted ? marking : Task.WhenAll(_automaticReadTask, marking);
        }
        _readerModeExtracted = null;
        ArticleHtml = BuildArticleHtml(value);
        // Per-feed "Always Use Reader View" matches the global default in priority —
        // either trumps the existing in-session toggle and forces reader mode on.
        var feedReaderAlways = AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == value.AccountID)?
            .FlattenedFeeds().FirstOrDefault(f => f.FeedID == value.FeedId)?.ReaderViewAlwaysEnabled
            ?? false;
        if (IsReaderMode || !_restoringWindowState && (AppDefaults.Shared.ReaderModeDefault || feedReaderAlways))
        {
            if (IsReaderMode) _readerModeTask = RunReaderModeAsync(value);
            else IsReaderMode = true;
        }
        UpdateSectionStatus();
    }

    [ObservableProperty]
    private bool _isReaderMode;

    private ExtractedArticle? _readerModeExtracted;
    private CancellationTokenSource? _readerModeCancellation;

    partial void OnIsReaderModeChanged(bool value)
    {
        if (SelectedArticle is null) { UpdateSectionStatus(); return; }
        if (value) { _readerModeTask = RunReaderModeAsync(SelectedArticle); }
        else
        {
            _readerModeCancellation?.Cancel();
            _readerModeExtracted = null;
            ArticleHtml = BuildArticleHtml(SelectedArticle);
        }
        UpdateSectionStatus();
    }

    [RelayCommand]
    private void ToggleReaderMode() => IsReaderMode = !IsReaderMode;

    /// <summary>Re-render the currently selected article into <see cref="ArticleHtml"/> —
    /// invoked by the Article Theme submenu after the user picks a new <c>.nnwtheme</c> so
    /// the WebView swaps over immediately without requiring a new article selection.</summary>
    public void RefreshArticleHtml()
    {
        if (SelectedArticle is null) return;
        ArticleHtml = IsReaderMode && _readerModeExtracted is not null
            ? BuildReaderHtml(SelectedArticle, _readerModeExtracted)
            : BuildArticleHtml(SelectedArticle);
    }

    private async Task RunReaderModeAsync(FeedItem item)
    {
        _readerModeCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _readerModeCancellation = cancellation;
        try
        {
            StatusMessage = "Extracting reader view…";
            var article = item.SourceArticle ?? throw new InvalidOperationException("The selected article has no backing model.");
            var extracted = await SmartReaderArticleExtractor.Shared.ExtractAsync(article, cancellation.Token);
            if (!_disposed && !cancellation.IsCancellationRequested && SelectedArticle is { } selected
                && Identity(selected) == Identity(item) && IsReaderMode)
            {
                if (extracted is null) { StatusMessage = "Reader view unavailable."; return; }
                _readerModeExtracted = extracted;
                ArticleHtml = BuildReaderHtml(selected, extracted);
                StatusMessage = "Reader view ready.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or InvalidOperationException or ArgumentException or FormatException)
        {
            System.Diagnostics.Trace.TraceError($"Reader extraction failed: {ex}");
            if (!_disposed && SelectedArticle is { } selected && Identity(selected) == Identity(item)) StatusMessage = $"Reader error: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_readerModeCancellation, cancellation)) _readerModeCancellation = null;
        }
    }

    private string BuildReaderHtml(FeedItem item, ExtractedArticle extracted) => BuildArticleHtml(item, extracted);

    partial void OnSelectedSidebarItemChanged(SidebarItem? oldValue, SidebarItem? newValue)
    {
        if (!_changingSidebarSelection) SetSelectedSidebarItems(newValue is null ? [] : [newValue]);
    }

    public void SetSelectedSidebarItems(IEnumerable<SidebarItem> items)
    {
        var selection = items.Where(item => item.Identity is not null).DistinctBy(item => item.Identity).ToArray();
        if (_selectedSidebarItems.SequenceEqual(selection)) return;
        bool changed = !_selectedSidebarItems.Select(item => item.Identity).ToHashSet()
            .SetEquals(selection.Select(item => item.Identity));
        if (changed) NotifySelectionNavigationStarting();
        _changingSidebarSelection = true;
        try
        {
            _selectedSidebarItems = selection;
            SelectedSidebarItem = selection.Length == 1 ? selection[0] : null;
            OnPropertyChanged(nameof(SelectedSidebarItems));
            OnPropertyChanged(nameof(HasMultipleSidebarSelection));
            if (changed) SetSelectedArticles(Array.Empty<FeedItem>());
        }
        finally { _changingSidebarSelection = false; }
        OnPropertyChanged(nameof(IsReadFiltered));
        OnPropertyChanged(nameof(CanToggleReadFilter));
        if (_restoringWindowState) return;
        if (changed && !string.IsNullOrEmpty(SearchQuery))
            SearchQuery = string.Empty;
        else
            _ = RefreshTimelineAsync();
    }
    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(CanToggleReadFilter));
        if (!_restoringWindowState) _ = RefreshTimelineAsync();
    }

    partial void OnSearchAllAccountsChanged(bool value)
    {
        AppService.Shared.Defaults.SearchAllAccounts = value;
        OnPropertyChanged(nameof(CanToggleReadFilter));
        if (!_restoringWindowState) _ = RefreshTimelineAsync();
    }

    public void SetSortParameters(ArticleSortParameters parameters, bool persistDefault = true)
    {
        if (!Enum.IsDefined(parameters.Key)) throw new ArgumentOutOfRangeException(nameof(parameters));
        if (persistDefault)
        {
            AppService.Shared.Defaults.SetValues(new Dictionary<string, JsonElement>
            {
                [AppDefaults.Key.TimelineSortKey] = JsonSerializer.SerializeToElement(parameters.Key.ToString()),
                [AppDefaults.Key.TimelineSortDirection] = JsonSerializer.SerializeToElement(parameters.Descending ? "descending" : "ascending"),
                [AppDefaults.Key.TimelineGroupByFeed] = JsonSerializer.SerializeToElement(false)
            });
        }
        SortParameters = parameters;
        OnPropertyChanged(nameof(SortParameters));
        if (!_restoringWindowState) _ = RefreshTimelineAsync();
    }

    internal TimelineWindowState CaptureTimelineState() => new()
    {
        ReadFilters = _readFilters.Snapshot(),
        SelectedArticle = SelectedArticle is { } article ? Identity(article) : null,
        SortParameters = SortParameters
    };

    internal void RestoreWindowLayout(MainWindowState state)
    {
        bool wasRestoring = _restoringWindowState;
        _restoringWindowState = true;
        try
        {
            IsUnifiedLayout = state.IsUnifiedLayout;
            IsSidebarVisible = !state.IsSidebarHidden;
        }
        finally { _restoringWindowState = wasRestoring; }
    }

    internal async Task RestoreTimelineStateAsync(MainWindowState state)
    {
        _restoringWindowState = true;
        try
        {
            ApplyTimelineState(state, "");
            await FilterArticlesAsync(state.Timeline?.SelectedArticle);
            if (!_disposed && IsReaderMode) await _readerModeTask;
        }
        finally
        {
            FinishSelectionRestoration();
        }
    }

    internal Task RestoreNavigationAsync(MainWindowState state, string searchQuery, IReadOnlySet<ArticleIdentityKey> selection)
    {
        _restoringWindowState = true;
        try { ApplyTimelineState(state, searchQuery); }
        finally { FinishSelectionRestoration(); }
        return FilterArticlesForSelectionAsync(selection);
    }

    private void ApplyTimelineState(MainWindowState state, string searchQuery)
    {
        var timeline = state.Timeline ?? new();
        _readFilters = new(timeline.ReadFilters);
        SetSortParameters(timeline.SortParameters ?? SortParameters, persistDefault: false);
        SearchQuery = searchQuery;
        IsReaderMode = state.Detail?.IsReaderMode == true;
        var keys = (state.Sidebar?.SelectedItems ?? []).ToHashSet();
        var items = SidebarItems.SelectMany(section => new[] { section }
            .Concat(section.Children).Concat(section.Children.SelectMany(child => child.Children)));
        SetSelectedSidebarItems(items.Where(item => item.Identity is { } key && keys.Contains(key)));
        IsSidebarReadFiltered = state.Sidebar?.IsReadFiltered == true;
    }

    private void FinishSelectionRestoration()
    {
        _restoringWindowState = false;
        if (_refreshAfterRestoration && !_disposed)
        {
            _refreshAfterRestoration = false;
            OnAccountChanged(this, EventArgs.Empty);
        }
    }

    private (AccountNs.SidebarItemIdentifier? Identifier, AccountNs.ReadFilterType Default) ReadFilterContext
        => ReadFilterContextFor(SelectedSidebarItem);

    private static (AccountNs.SidebarItemIdentifier? Identifier, AccountNs.ReadFilterType Default) ReadFilterContextFor(SidebarItem? item)
        => item?.Tag switch
        {
            AccountNs.Feed feed => (feed.SidebarItemID, feed.DefaultReadFilterType),
            AccountNs.Folder folder => (folder.SidebarItemID, folder.DefaultReadFilterType),
            IPseudoFeed smart => (smart.SidebarItemID, smart.DefaultReadFilterType),
            _ => (null, AccountNs.ReadFilterType.AlwaysRead),
        };

    public bool IsReadFiltered => !HasMultipleSidebarSelection && _readFilters.IsEnabled(ReadFilterContext.Identifier, ReadFilterContext.Default);
    public bool CanToggleReadFilter => SelectedSidebarItems.Count == 1 && ReadFilterContext.Identifier is not null
        && ReadFilterContext.Default != AccountNs.ReadFilterType.AlwaysRead
        && !(SearchAllAccounts && SearchQuery.Length > 0);

    [RelayCommand]
    private async Task ToggleReadFilterAsync()
    {
        if (!CanToggleReadFilter || ReadFilterContext.Identifier is not { } identifier) return;
        var next = new ReadFilterState(_readFilters.Snapshot());
        next.SetEnabled(identifier, !IsReadFiltered);
        AppService.Shared.Defaults.ReadFilters = next.Snapshot();
        _readFilters = next;
        OnPropertyChanged(nameof(IsReadFiltered));
        await PendingAutomaticReads;
        if (_disposed || !ReferenceEquals(_readFilters, next) || ReadFilterContext.Identifier != identifier) return;
        await RefreshTimelineAsync();
    }

    [RelayCommand]
    private void CleanUpReadArticles()
    {
        if (!IsReadFiltered) return;
        var selected = SelectedArticles.Select(Identity).ToHashSet();
        for (int i = ArticleItems.Count - 1; i >= 0; i--)
            if (ArticleItems[i].IsRead && !selected.Contains(Identity(ArticleItems[i])))
                ArticleItems.RemoveAt(i);
        UpdateSectionStatus();
    }

    [RelayCommand]
    private Task MarkOlderArticlesReadAsync()
    {
        if (SelectedArticles.Count == 0) return Task.CompletedTask;
        var cutoff = SelectedArticles.Min(a => a.PublishedDate);
        return MarkArticlesAsync(ArticleItems.Where(a => a.PublishedDate < cutoff).ToList(), ArticleStatus.Key.Read, true);
    }

    private async Task RefreshTimelineAsync()
    {
        try { await FilterArticlesAsync(); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or InvalidOperationException)
        {
            StatusMessage = $"Could not load articles: {ex.Message}";
            await AppService.Shared.LogAsync("Load articles", ex);
        }
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private async Task ReloadFeedsAsync()
    {
        IsLoading = true;
        StatusMessage = "Refreshing feeds…";
        try
        {
            await AppService.Shared.Accounts.RefreshAllAsync();
            OnAccountChanged(this, EventArgs.Empty);
            StatusMessage = $"Refreshed {AppService.Shared.Accounts.ActiveAccounts.Count()} account(s)";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task AddFeedAsync(AddFeedRequest request)
    {
        try { await AddFeedWithResultAsync(request); }
        catch (Exception ex)
        {
            StatusMessage = $"Could not add feed: {ex.Message}";
            await AppService.Shared.LogAsync("Add feed", ex);
        }
    }

    internal async Task<AccountNs.Feed> AddFeedWithResultAsync(AddFeedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Url);
        var account = request.Account ?? AppService.Shared.Accounts.DefaultAccount;
        if (!AppService.Shared.Accounts.ActiveAccounts.Any(candidate => ReferenceEquals(candidate, account)))
            throw new InvalidOperationException("The selected account is no longer active.");
        IsLoading = true;
        StatusMessage = $"Adding feed: {request.Url}";
        try
        {
            var folder = request.FolderName is { Length: > 0 } fn
                ? account.ExistingFolderWithName(fn)
                  ?? await account.CreateFolderAsync(fn)
                : null;
            var feed = await account.CreateFeedAsync(request.Url, request.Name, folder)
                ?? throw new WinNewsWire.FeedFinder.FeedNotFoundException();
            BuildSidebar();
            StatusMessage = $"Fetching: {feed.NameForDisplay}";
            try
            {
                await account.RefreshAllAsync();
                BuildSidebar();
                await FilterArticlesAsync();
                StatusMessage = $"Added: {feed.NameForDisplay}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Added {feed.NameForDisplay}, but could not refresh: {ex.Message}";
                await AppService.Shared.LogAsync("Refresh new feed", ex);
            }
            return feed;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleStarredAsync()
    {
        await MarkArticlesAsync(SelectedArticles, ArticleStatus.Key.Starred, SelectedArticles.Any(a => !a.IsStarred));
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelectedRead))]
    private async Task MarkSelectedReadAsync()
    {
        await MarkArticlesAsync(SelectedArticles, ArticleStatus.Key.Read, SelectedArticles.Any(a => !a.IsRead));
    }

    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        await MarkArticlesAsync(ArticleItems.ToList(), ArticleStatus.Key.Read, true);
    }

    [RelayCommand]
    private void NextArticle()
    {
        if (ArticleItems.Count == 0) return;
        var idx = SelectedArticles.Count == 0 ? -1 : ArticleItems.IndexOf(SelectedArticles[^1]);
        if (idx < ArticleItems.Count - 1) SelectedArticle = ArticleItems[idx + 1];
    }

    [RelayCommand]
    private void PreviousArticle()
    {
        if (ArticleItems.Count == 0) return;
        var idx = SelectedArticles.Count == 0 ? ArticleItems.Count : ArticleItems.IndexOf(SelectedArticles[0]);
        if (idx > 0) SelectedArticle = ArticleItems[idx - 1];
    }

    // --- Initialization ---

    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading…";
        try
        {
            BuildSidebar();
            await FilterArticlesAsync();
            StatusMessage = $"Loaded {AppService.Shared.Accounts.ActiveAccounts.Count()} account(s)";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>Builds the sidebar tree from <see cref="AccountNs.AccountManager"/>:
    /// Smart Feeds at the top, followed by one section per active account, each with its
    /// folders and flat feeds.</summary>
    public void BuildSidebar()
    {
        var rebuilt = new List<SidebarItem>();
        var order = AppShared.Sidebar.SidebarFeedOrder.Load(AppDefaults.Shared);

        // --- Smart Feeds section ---
        var smartHeader = new SidebarItem
        {
            Title = "Smart Feeds",
            ItemType = SidebarItemType.SectionHeader,
            IsExpanded = true,
        };
        foreach (var sf in SmartFeedsController.Shared.SmartFeeds)
        {
            smartHeader.Children.Add(new SidebarItem
            {
                Title = sf.NameForDisplay,
                Icon = sf.NameForDisplay switch
                {
                    "Today" => "\uE706",
                    "All Unread" => "\uE7BC",
                    "Starred" => "\uE734",
                    _ => "\uE7C3",
                },
                ItemType = SidebarItemType.SmartFeed,
                Tag = sf,
                UnreadCount = sf.UnreadCount,
            });
        }
        rebuilt.Add(smartHeader);

        // --- Per-account sections ---
        foreach (var account in AppService.Shared.Accounts.SortedActiveAccounts)
        {
            var accountHeader = new SidebarItem
            {
                Title = account.NameForDisplay,
                ItemType = SidebarItemType.SectionHeader,
                IsExpanded = true,
                Tag = account,
            };

            // Top-level feeds (no folder).
            foreach (var feed in order.OrderedFeeds(account))
            {
                accountHeader.Children.Add(FeedSidebarItem(feed));
            }

            // Folders.
            foreach (var folder in account.Folders.OrderBy(f => f.NameForDisplay, WinNewsWire.Core.LocalizedStandardComparer.Shared))
            {
                var folderItem = new SidebarItem
                {
                    Title = folder.NameForDisplay,
                    Icon = "\uE8B7",
                    ItemType = SidebarItemType.Folder,
                    IsExpanded = true,
                    Tag = folder,
                };
                foreach (var feed in order.OrderedFeeds(account, folder))
                    folderItem.Children.Add(FeedSidebarItem(feed, folder));
                folderItem.UnreadCount = folderItem.Children.Sum(c => c.UnreadCount);
                accountHeader.Children.Add(folderItem);
            }

            accountHeader.UnreadCount = account.FlattenedFeeds().Sum(f => f.UnreadCount);
            rebuilt.Add(accountHeader);
        }

        bool structureChanged = !SameSidebarTree(SidebarItems, rebuilt);
        if (structureChanged)
        {
            MergeSidebarRows(SidebarItems, rebuilt);
        }
        UpdateSidebarUnreadCounts();
        _ = PopulateSidebarFaviconsAsync();
        if (structureChanged || IsSidebarReadFiltered) SidebarRebuilt?.Invoke();
    }

    private static bool SameSidebarTree(IReadOnlyList<SidebarItem> current, IReadOnlyList<SidebarItem> next)
    {
        if (current.Count != next.Count) return false;
        for (int index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = next[index];
            if (!SameSidebarRow(left, right) || !SameSidebarTree(left.Children, right.Children)) return false;
        }
        return true;
    }

    private static bool SameSidebarRow(SidebarItem left, SidebarItem right)
        => left.ItemType == right.ItemType && left.Identity == right.Identity && left.Title == right.Title
            && left.Icon == right.Icon && left.FeedImageIdentity == right.FeedImageIdentity && ReferenceEquals(left.Tag, right.Tag);

    private static void MergeSidebarRows(ObservableCollection<SidebarItem> current, IReadOnlyList<SidebarItem> desired)
    {
        for (int index = 0; index < desired.Count; index++)
        {
            var next = desired[index];
            var existing = index < current.Count && SameSidebarRow(current[index], next) ? current[index]
                : current.FirstOrDefault(row => SameSidebarRow(row, next));
            if (existing is not null)
            {
                MergeSidebarRows(existing.Children, next.Children);
                next = existing;
            }
            if (index < current.Count && ReferenceEquals(current[index], next)) continue;
            int oldIndex = current.IndexOf(next);
            if (oldIndex >= 0) current.Move(oldIndex, index);
            else current.Insert(index, next);
        }
        while (current.Count > desired.Count) current.RemoveAt(current.Count - 1);
    }

    /// <summary>Raised at the end of <see cref="BuildSidebar"/> so the view can re-mirror
    /// <see cref="SidebarItems"/> into its outline list. Without this, a sidebar rebuild
    /// triggered from a background task (e.g. the post-launch refresh in
    /// <see cref="InitializeAsync"/>) would replace SidebarItem instances behind the
    /// view's back, leaving its visible rows pointing at stale items so per-feed
    /// unread-count updates never reach the UI until something else forces the view
    /// to re-bind.</summary>
    public event Action? SidebarRebuilt;

    private async Task PopulateSidebarFaviconsAsync()
    {
        try
        {
            await EnsureFeedImageObserverAsync();
            foreach (var section in SidebarItems.ToList())
                await PopulateFaviconsForSectionAsync(section);
        }
        catch { /* favicons are best-effort */ }
    }

    private async Task PopulateFaviconsForSectionAsync(SidebarItem item)
    {
        if (item.Tag is AccountNs.Feed feed)
        {
            try
            {
                var path = await FaviconDownloader.Shared.FaviconPathAsync(feed);
                if (!string.IsNullOrEmpty(path))
                {
                    item.FaviconPath = path;
                    if (_changedFeedImagePaths.Contains(path)) item.RefreshFavicon();
                }
            }
            catch { }
        }
        foreach (var child in item.Children.ToList())
            await PopulateFaviconsForSectionAsync(child);
    }

    private static SidebarItem FeedSidebarItem(AccountNs.Feed feed, AccountNs.Folder? parentFolder = null) => new()
    {
        Title = feed.NameForDisplay,
        Icon = "\uE774",
        ItemType = SidebarItemType.Feed,
        Tag = feed,
        ParentFolder = parentFolder,
        FeedImageIdentity = (feed.HomePageUrl, feed.FaviconUrl, feed.IconUrl),
        UnreadCount = feed.UnreadCount,
    };

    /// <summary>Load articles matching the current sidebar selection + search query.</summary>
    public Task FilterArticlesAsync(ArticleIdentityKey? restoringSelection = null)
        => FilterArticlesForSelectionAsync(restoringSelection is { } identity ? new HashSet<ArticleIdentityKey> { identity } : null);

    private Task FilterArticlesForSelectionAsync(IReadOnlySet<ArticleIdentityKey>? restoringSelection)
    {
        if (_disposed) return Task.CompletedTask;
        int generation = Interlocked.Increment(ref _articleLoadGeneration);
        var task = FilterArticlesCoreAsync(generation, restoringSelection);
        if (generation == Volatile.Read(ref _articleLoadGeneration)) _timelineRefreshTask = task;
        return task;
    }

    internal async Task WaitForCurrentTimelineAsync(CancellationToken cancellationToken)
    {
        Task current;
        do
        {
            current = _timelineRefreshTask;
            await current.WaitAsync(cancellationToken);
        } while (!ReferenceEquals(current, _timelineRefreshTask));
    }

    private async Task FilterArticlesCoreAsync(int generation, IReadOnlySet<ArticleIdentityKey>? restoringSelection)
    {
        var selection = SelectedSidebarItems.ToArray();
        var query = SearchQuery.Trim();
        var selectedIDs = restoringSelection?.ToHashSet() ?? SelectedArticles.Select(Identity).ToHashSet();
        bool globalSearch = query.Length > 0 && SearchAllAccounts;
        HashSet<Article> results = new();
        string title = "All Articles";

        if (globalSearch)
        {
            foreach (var account in AppService.Shared.Accounts.ActiveAccounts)
                results.UnionWith(await account.Database.SearchAsync(query, account.FlattenedFeeds().Select(f => f.FeedID)));
            title = "All Accounts";
        }
        else
        {
            if (selection.Length == 0)
            {
                results = await FetchSidebarArticlesAsync(null, selectedIDs);
                title = "All Unread";
            }
            else if (selection.Length == 1)
            {
                results = await FetchSidebarArticlesAsync(selection[0], selectedIDs);
                title = selection[0].Title;
            }
            else
            {
                foreach (var selected in selection) results.UnionWith(await FetchSidebarArticlesAsync(selected, selectedIDs));
                title = selection.Length == 1 ? selection[0].Title : "Multiple";
            }
        }
        if (restoringSelection is not null)
            foreach (var group in restoringSelection.GroupBy(identity => identity.AccountID))
                if (AppService.Shared.Accounts.Accounts.FirstOrDefault(account => account.AccountID == group.Key) is { } restoredAccount)
                    results.UnionWith(await restoredAccount.Database.FetchArticlesByIDsAsync(group.Select(identity => identity.ArticleID)));
        results = results.GroupBy(article => new ArticleIdentityKey(article.AccountID, article.ArticleID))
            .Select(group => group.Last()).ToHashSet();

        if (query.Length > 0 && !globalSearch)
        {
            var matches = new HashSet<(string AccountID, string ArticleID)>();
            foreach (var group in results.GroupBy(a => a.AccountID))
            {
                var account = AppService.Shared.Accounts.Accounts.First(a => a.AccountID == group.Key);
                foreach (var article in await account.Database.SearchAsync(query, group.Select(a => a.FeedID), limit: null))
                    matches.Add((article.AccountID, article.ArticleID));
            }
            results.RemoveWhere(a => !matches.Contains((a.AccountID, a.ArticleID)));
        }

        if (generation != Volatile.Read(ref _articleLoadGeneration) || _disposed) return;
        var feedMap = AppService.Shared.Accounts.Accounts.SelectMany(a => a.FlattenedFeeds())
            .GroupBy(f => (f.AccountID, f.FeedID)).ToDictionary(g => g.Key, g => g.First());
        var feedMetadata = feedMap.ToDictionary(pair => pair.Key, pair => new TimelineFeedMetadata(
            pair.Value.Url, pair.Value.HomePageUrl, pair.Value.FaviconUrl ?? pair.Value.IconUrl ?? "",
            pair.Value.NameForDisplay, pair.Value.Authors));
        var previousItems = ArticleItems.ToDictionary(Identity);
        // Prepare new rows off-thread; bound rows and current status/selection are updated only after returning.
        var prepared = await Task.Run(() =>
        {
#if PARITY_PROBE
            ProbePreparingTimeline?.Invoke();
#endif
            return results.ToDictionary(article => new ArticleIdentityKey(article.AccountID, article.ArticleID), article =>
            {
                var feed = feedMetadata.GetValueOrDefault((article.AccountID, article.FeedID));
                if (previousItems.TryGetValue(new(article.AccountID, article.ArticleID), out var previous)
                    && ReferenceEquals(previous.SourceArticle, article)
                    && previous.FeedUrl == feed?.Url && previous.FeedHomePageUrl == feed?.HomePageUrl
                    && previous.FeedIconUrl == (feed?.IconUrl ?? "")
                    && previous.FeedTitle == ArticleStringFormatter.Shared.TruncatedFeedName(feed?.Name ?? "")
                    && previous.Author == article.Byline(feed?.Name, feed?.Authors))
                    return previous;
                return MapArticleToFeedItem(article, feedMetadata);
            });
        });
        if (generation != Volatile.Read(ref _articleLoadGeneration) || _disposed) return;
        if (!UseColumnLayout && !IsUnifiedLayout && TimelineThumbnailsDisplay == TimelineThumbnailsDisplay.Large)
        {
            await AppService.Shared.ThumbnailImages.LoadSavedAsync(results);
            if (generation != Volatile.Read(ref _articleLoadGeneration) || _disposed) return;
        }
        if (restoringSelection is null)
        {
            // The user can change selection while a database fetch is in flight.
            selectedIDs = SelectedArticles.Select(Identity).ToHashSet();
            if (query.Length == 0)
            {
                var fetched = results.Select(article => new ArticleIdentityKey(article.AccountID, article.ArticleID)).ToHashSet();
                foreach (var selected in SelectedArticles)
                    if (selected.SourceArticle is { } article && fetched.Add(Identity(selected)))
                    {
                        results.Add(article);
                        prepared.Add(Identity(selected), selected);
                    }
            }
        }
        string FeedName(Article article) => feedMetadata.GetValueOrDefault((article.AccountID, article.FeedID))?.Name ?? "";
        var ordered = ArticleSorter.Sorted(results, SortParameters, FeedName);

        var items = ordered.Select(article => prepared[new(article.AccountID, article.ArticleID)]).ToList();
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (item.SourceArticle is { } article)
            {
                item.IsRead = article.Status.Read;
                item.IsStarred = article.Status.Starred;
            }
            if (previousItems.TryGetValue(Identity(item), out var previous))
            {
                if (item.FeedIconUrl == previous.FeedIconUrl && item.FeedHomePageUrl == previous.FeedHomePageUrl
                    && item.FeedUrl == previous.FeedUrl)
                    item.FaviconPath = previous.FaviconPath;
                if (!ReferenceEquals(item, previous))
                {
                    previous.UpdateFrom(item);
                    items[index] = previous;
                }
            }
        }
        _restoringArticleSelection = true;
        try
        {
            var keys = selection.Select(item => item.Identity).ToArray();
            if (_displayedQuery == query && _displayedGlobalSearch == globalSearch && _displayedSidebarKeys.SequenceEqual(keys))
                MergeArticleItems(items, previousItems);
            else ArticleItems = new ObservableCollection<FeedItem>(items);
            _displayedSidebarKeys = keys;
            _displayedQuery = query;
            _displayedGlobalSearch = globalSearch;
            var selected = ArticleSelection.InTimelineOrder(items, selectedIDs, Identity);
            if (!SelectedArticles.SequenceEqual(selected)) SetSelectedArticles(selected);
        }
        finally { _restoringArticleSelection = false; }
        ArticleListTitle = query.Length == 0
            ? title
            : $"{title} — Search Results";
        ArticleListUnreadCount = items.Count(i => !i.IsRead);
        UpdateSectionStatus();
        TimelineArticlesChanged?.Invoke(this, EventArgs.Empty);
        _ = PopulateArticleFaviconsAsync(items, feedMap);
    }

    private void MergeArticleItems(IReadOnlyList<FeedItem> items, IReadOnlyDictionary<ArticleIdentityKey, FeedItem> previous)
    {
        var wanted = items.Select(Identity).ToHashSet();
        for (int index = ArticleItems.Count - 1; index >= 0; index--)
            if (!wanted.Contains(Identity(ArticleItems[index]))) ArticleItems.RemoveAt(index);
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (index < ArticleItems.Count && ReferenceEquals(ArticleItems[index], item)) continue;
            int current = previous.TryGetValue(Identity(item), out var old) ? ArticleItems.IndexOf(old) : -1;
            if (current < 0) ArticleItems.Insert(index, item);
            else
            {
                if (!ReferenceEquals(ArticleItems[current], item)) ArticleItems[current] = item;
                if (current != index) ArticleItems.Move(current, index);
            }
        }
    }

    private async Task<HashSet<Article>> FetchSidebarArticlesAsync(SidebarItem? item, IReadOnlySet<ArticleIdentityKey> retained,
        bool respectReadFilter = true)
    {
        var filter = ReadFilterContextFor(item);
        bool unreadOnly = respectReadFilter && _readFilters.IsEnabled(filter.Identifier, filter.Default);
        var selected = item is null || item.Tag is IPseudoFeed { DefaultReadFilterType: AccountNs.ReadFilterType.AlwaysRead }
            ? SelectedArticles.Select(row => row.SourceArticle).OfType<Article>().ToArray() : [];
        var requests = new List<Task<HashSet<Article>>>();
        switch (item?.Tag)
        {
            case IPseudoFeed smart:
                requests.Add(smart.FetchArticlesAsync());
                break;
            case AccountNs.Feed feed:
                if (AppService.Shared.Accounts.Accounts.FirstOrDefault(account => account.AccountID == feed.AccountID) is { } owner)
                    requests.Add(owner.Database.FetchArticlesAsync(feed.FeedID));
                break;
            case AccountNs.Folder folder:
                if (AppService.Shared.Accounts.Accounts.FirstOrDefault(account => account.AccountID == folder.AccountID) is { } folderOwner)
                    requests.Add(folderOwner.Database.FetchArticlesAsync(folder.Feeds.Select(feed => feed.FeedID).ToArray()));
                break;
            case AccountNs.Account account:
                requests.Add(account.Database.FetchArticlesAsync(account.FlattenedFeeds().Select(feed => feed.FeedID).ToArray()));
                break;
            default:
                foreach (var active in AppService.Shared.Accounts.ActiveAccounts)
                    requests.Add(active.FetchUnreadAsync());
                break;
        }
        var fetched = await Task.WhenAll(requests).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var results = fetched.Length == 1 ? fetched[0] : new HashSet<Article>();
            if (fetched.Length != 1)
                foreach (var articles in fetched) results.UnionWith(articles);
            if (unreadOnly)
            {
                results.UnionWith(selected);
                results.RemoveWhere(article => article.Status.Read && !retained.Contains(new(article.AccountID, article.ArticleID)));
            }
            return results;
        }).ConfigureAwait(false);
    }

    private async Task PopulateArticleFaviconsAsync(
        IReadOnlyList<FeedItem> items,
        Dictionary<(string, string), AccountNs.Feed> feedMap)
    {
        try
        {
            await EnsureFeedImageObserverAsync();
            var cache = new Dictionary<(string, string), string?>();
            foreach (var item in items)
            {
                var key = (item.AccountID, item.FeedId);
                if (!cache.TryGetValue(key, out var path))
                {
                    if (feedMap.TryGetValue(key, out var feed))
                        path = await FaviconDownloader.Shared.ImagePathAsync(feed);
                    cache[key] = path;
                }
                if (!string.IsNullOrEmpty(path))
                {
                    item.FaviconPath = path;
                    if (_changedFeedImagePaths.Contains(path)) item.RefreshFavicon();
                }
            }
        }
        catch { /* favicons are best-effort */ }
    }

    private async Task EnsureFeedImageObserverAsync()
    {
        if (_feedImages is not null || _disposed) return;
        var service = await Images.FeedImageDownloader.GetSharedAsync();
        if (!_disposed && _feedImages is null) ObserveFeedImages(service);
    }

    private void ObserveFeedImages(Images.FeedImageDownloader? service)
    {
        if (_feedImages is not null) _feedImages.ImageDidBecomeAvailable -= FeedImageDidBecomeAvailable;
        _feedImages = service;
        if (service is not null) service.ImageDidBecomeAvailable += FeedImageDidBecomeAvailable;
    }

    private void FeedImageDidBecomeAvailable(object? sender, Images.DownloadedImage image)
    {
        if (_disposed) return;
        if (!_dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !ReferenceEquals(sender, _feedImages)) return;
            _changedFeedImagePaths.Add(image.Path);
            foreach (var item in ArticleItems)
                if (string.Equals(item.FaviconPath, image.Path, StringComparison.OrdinalIgnoreCase)) item.RefreshFavicon();
            void RefreshSidebar(SidebarItem item)
            {
                if (string.Equals(item.FaviconPath, image.Path, StringComparison.OrdinalIgnoreCase)) item.RefreshFavicon();
                foreach (var child in item.Children) RefreshSidebar(child);
            }
            foreach (var item in SidebarItems) RefreshSidebar(item);
        }) && !_disposed)
            System.Diagnostics.Trace.TraceError($"Could not dispatch a changed feed image at {image.Url}.");
    }

    private sealed record TimelineFeedMetadata(string Url, string? HomePageUrl, string IconUrl, string Name, IReadOnlySet<Author>? Authors);

    private static FeedItem MapArticleToFeedItem(Article a, IReadOnlyDictionary<(string, string), TimelineFeedMetadata> feedMap)
    {
        var feed = feedMap.TryGetValue((a.AccountID, a.FeedID), out var f) ? f : null;
        return new FeedItem
        {
            SourceArticle = a,
            Id = a.ArticleID,
            AccountID = a.AccountID,
            FeedId = a.FeedID,
            FeedUrl = feed?.Url,
            FeedHomePageUrl = feed?.HomePageUrl,
            Content = a.ContentHtml ?? (a.ContentText is { } text ? $"<pre>{WebUtility.HtmlEncode(text)}</pre>" : a.Summary ?? ""),
            Link = a.RawLink ?? a.RawExternalLink ?? "",
            ExternalLink = a.RawExternalLink ?? "",
            Author = a.Byline(feed?.Name, feed?.Authors),
            PublishedDate = new DateTimeOffset(a.LogicalDatePublished(), TimeSpan.Zero),
            IsRead = a.Status.Read,
            IsStarred = a.Status.Starred,
            ImageUrl = a.RawImageLink ?? "",
            FeedTitle = ArticleStringFormatter.Shared.TruncatedFeedName(feed?.Name ?? ""),
            FeedIconUrl = feed?.IconUrl ?? "",
        };
    }

    private static string? StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var s = System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", " ");
        s = WebUtility.HtmlDecode(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
        return s.Length > 280 ? s[..280] + "…" : s;
    }

    private async Task MarkAutomaticallyReadAsync(FeedItem item)
    {
        await RunArticleActionAsync("Mark Read", () => ApplyArticleStatusAsync(
            new[] { item.SourceArticle ?? throw new InvalidOperationException("The selected article has no backing model.") },
            ArticleStatus.Key.Read, true));
    }

    /// <summary>Bulk-mark a set of articles read/unread (or starred/unstarred) and keep
    /// the sidebar counts and visible list in sync. Mirrors NNW Mac's
    /// <c>MarkStatusCommand</c>-driven contextual menu actions.</summary>
    public async Task MarkArticlesAsync(IEnumerable<FeedItem> articles, ArticleStatus.Key key, bool flag)
    {
        await RunArticleActionAsync("Change article status", async () =>
        {
            var models = articles.Select(a => a.SourceArticle
                ?? throw new InvalidOperationException("An article has no backing model.")).ToList();
            await ExecuteStatusChangeAsync(models, key, flag);
        });
    }

    public Task MarkSidebarItemsReadAsync(IEnumerable<SidebarItem> selection)
    {
        var items = selection.ToArray();
        return RunSidebarActionAsync("Mark feeds read", async () =>
        {
            var articles = new List<Article>();
            foreach (var item in items)
                articles.AddRange(await FetchSidebarArticlesAsync(item, new HashSet<ArticleIdentityKey>(), respectReadFilter: false));
            await ExecuteStatusChangeAsync(articles, ArticleStatus.Key.Read, true);
        });
    }

    private Task ExecuteStatusChangeAsync(IEnumerable<Article> articles, ArticleStatus.Key key, bool flag)
    {
        var command = MarkStatusCommand.Create(articles, key, flag, ApplyArticleStatusAsync, CanMarkUnread);
        return command is null ? Task.CompletedTask : UndoManager.ExecuteAsync(command);
    }

    private async Task ApplyArticleStatusAsync(IReadOnlyCollection<Article> articles, ArticleStatus.Key key, bool flag)
    {
        foreach (var group in articles.GroupBy(a => a.AccountID))
        {
            var account = AppService.Shared.Accounts.Accounts.FirstOrDefault(a => a.AccountID == group.Key)
                ?? throw new InvalidOperationException("The article's account is no longer available.");
            var ids = group.Select(a => a.ArticleID).ToHashSet();
            await account.MarkAsync(ids, key, flag);
            foreach (var item in ArticleItems.Where(a => !_disposed && a.AccountID == group.Key && ids.Contains(a.Id)))
            {
                if (key == ArticleStatus.Key.Read) item.IsRead = flag;
                else item.IsStarred = flag;
            }
            await account.RecalculateUnreadCountsAsync();
        }
        if (_disposed) return;
        UpdateSidebarUnreadCounts();
        OnPropertyChanged(nameof(CanToggleSelectedRead));
        MarkSelectedReadCommand.NotifyCanExecuteChanged();
    }

    private static bool CanMarkUnread(Article article)
        => article.IsAvailableToMarkUnread(AppService.Shared.Accounts.Accounts.FirstOrDefault(account => account.AccountID == article.AccountID)?.Type
            ?? AccountNs.AccountType.OnMyMac);

    public Task UndoAsync() => RunSidebarActionAsync("Undo", UndoManager.UndoAsync);
    public Task RedoAsync() => RunSidebarActionAsync("Redo", UndoManager.RedoAsync);

    public async Task RunSidebarActionAsync(string operation, Func<Task> work)
    {
        await RunArticleActionAsync(operation, work);
        BuildSidebar();
        await RefreshTimelineAsync();
    }

    private async Task RunArticleActionAsync(string operation, Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException
            or UnauthorizedAccessException or InvalidOperationException or System.Net.Http.HttpRequestException
            or WinNewsWire.Secrets.CredentialsException or ArgumentException or NotSupportedException
            or AggregateException or System.Text.Json.JsonException or WinNewsWire.FeedFinder.FeedNotFoundException
            or OperationCanceledException)
        {
            if (!_disposed) StatusMessage = $"{operation} could not finish: {ex.Message}";
            await AppService.Shared.LogAsync(operation, ex);
        }
    }

    /// <summary>Mark every article above <paramref name="article"/> in the current
    /// timeline as read.</summary>
    public Task MarkAboveAsReadAsync(FeedItem article)
    {
        var idx = ArticleItems.IndexOf(article);
        if (idx <= 0) return Task.CompletedTask;
        var slice = ArticleItems.Take(idx).ToList();
        return MarkArticlesAsync(slice, ArticleStatus.Key.Read, true);
    }

    /// <summary>Mark every article below <paramref name="article"/> in the current
    /// timeline as read.</summary>
    public Task MarkBelowAsReadAsync(FeedItem article)
    {
        var idx = ArticleItems.IndexOf(article);
        if (idx < 0 || idx >= ArticleItems.Count - 1) return Task.CompletedTask;
        var slice = ArticleItems.Skip(idx + 1).ToList();
        return MarkArticlesAsync(slice, ArticleStatus.Key.Read, true);
    }

    /// <summary>Mark every unread article in a feed as read (used by the timeline
    /// contextual menu's "Mark All as Read in 'Feed Name'" command).</summary>
    public async Task MarkAllInFeedAsReadAsync(string accountId, string feedId)
    {
        var account = AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == accountId);
        if (account is null) return;
        var unread = await account.Database.FetchUnreadArticlesAsync(new[] { feedId });
        if (unread.Count == 0) return;
        await RunArticleActionAsync("Mark feed read", async () =>
        {
            var command = MarkStatusCommand.Create(unread, ArticleStatus.Key.Read, true, ApplyArticleStatusAsync);
            if (command is not null) await UndoManager.ExecuteAsync(command);
        });

        // Reflect in current timeline view + sidebar
        UpdateSidebarUnreadCounts();
    }

    /// <summary>Locate the sidebar item backed by <paramref name="feedId"/> within
    /// <paramref name="accountId"/> and select it. Returns the matching item if found.</summary>
    public SidebarItem? FindSidebarItemForFeed(string accountId, string feedId)
    {
        foreach (var section in SidebarItems)
        {
            foreach (var child in section.Children)
            {
                if (child.Tag is AccountNs.Feed f && f.AccountID == accountId && f.FeedID == feedId)
                    return child;
                foreach (var grand in child.Children)
                {
                    if (grand.Tag is AccountNs.Feed gf && gf.AccountID == accountId && gf.FeedID == feedId)
                        return grand;
                }
            }
        }
        return null;
    }

    internal async Task RevealActivationAsync(AppActivationRequest request)
    {
        var smart = request.Kind switch
        {
            AppActivationKind.ShowUnread => SmartFeedsController.Shared.UnreadFeed,
            AppActivationKind.ShowToday => SmartFeedsController.Shared.TodayFeed,
            AppActivationKind.ShowStarred => SmartFeedsController.Shared.StarredFeed,
            _ => throw new ArgumentException("The activation does not identify a timeline.", nameof(request))
        };
        _restoringWindowState = true;
        try
        {
            BuildSidebar();
            SearchQuery = "";
            SelectedSidebarItem = SidebarItems.SelectMany(section => section.Children).First(item => ReferenceEquals(item.Tag, smart));
            if (request.ArticleID is null) { await FilterArticlesAsync(); return; }
            foreach (var account in AppService.Shared.Accounts.SortedActiveAccounts)
            {
                if (request.AccountID is not null && account.AccountID != request.AccountID) continue;
                var article = (await account.Database.FetchArticlesByIDsAsync([request.ArticleID])).FirstOrDefault();
                if (article is null) continue;
                var identity = new ArticleIdentityKey(account.AccountID, article.ArticleID);
                await FilterArticlesAsync(identity);
                if (SelectedArticle is not { } selected || Identity(selected) != identity)
                    throw new InvalidOperationException("The requested article could not be selected.");
                await MarkAutomaticallyReadAsync(selected);
                return;
            }
            await FilterArticlesAsync();
            throw new InvalidOperationException("The requested article is not available in an active account.");
        }
        finally { FinishSelectionRestoration(); }
    }

    /// <summary>Adjust the in-memory unread count on the article's owning Feed so the
    /// sidebar reflects the change instantly. <see cref="Account.RecalculateUnreadCountsAsync"/>
    /// otherwise only runs at refresh time, leaving sidebar counts stale until then.</summary>
    private static void AdjustFeedUnreadCount(FeedItem item, int delta)
    {
        if (delta == 0) return;
        var feed = AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == item.AccountID)?
            .FlattenedFeeds().FirstOrDefault(f => f.FeedID == item.FeedId);
        if (feed is null) return;
        feed.UnreadCount = Math.Max(0, feed.UnreadCount + delta);
        feed.OnUnreadCountChanged();
    }

    /// <summary>After a mark-read/unread the sidebar counts are stale; recompute without
    /// rebuilding the tree so selection is preserved.</summary>
    internal async Task RefreshNavigationUnreadCountsAsync(CancellationToken cancellationToken)
    {
        foreach (var account in AppService.Shared.Accounts.ActiveAccounts.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await account.RecalculateUnreadCountsAsync();
        }
        cancellationToken.ThrowIfCancellationRequested();
        await SmartFeedsController.Shared.RefreshAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_disposed) UpdateSidebarUnreadCounts(refreshSmartFeeds: false);
    }

    public void UpdateSidebarUnreadCounts(bool refreshSmartFeeds = true)
    {
        // Per-feed and per-folder counts come straight from in-memory Feed.UnreadCount,
        // which AdjustFeedUnreadCount keeps current. Smart-feed counts (Today / All
        // Unread / Starred) require a DB query so we kick off a refresh and update the
        // sidebar when it returns.
        foreach (var section in SidebarItems)
        {
            foreach (var child in section.Children)
            {
                switch (child.Tag)
                {
                    case AccountNs.Feed feed:
                        child.UnreadCount = feed.UnreadCount;
                        break;
                    case AccountNs.Folder folder:
                        child.UnreadCount = folder.Feeds.Sum(f => f.UnreadCount);
                        break;
                    case IPseudoFeed sf:
                        child.UnreadCount = sf.UnreadCount;
                        break;
                }
                // Update grandchildren (feeds inside folders).
                foreach (var grand in child.Children)
                {
                    if (grand.Tag is AccountNs.Feed f2)
                        grand.UnreadCount = f2.UnreadCount;
                }
            }

            // The account-header section itself ("On My PC", etc.) carries the
            // rolled-up unread count for its account. Recompute from current children.
            if (section.Tag is AccountNs.Account account)
                section.UnreadCount = account.FlattenedFeeds().Sum(f => f.UnreadCount);
        }

        ArticleListUnreadCount = ArticleItems.Count(i => !i.IsRead);
        UpdateSectionStatus();

        // Refresh smart feed counts off the DB and re-tally once they land.
        if (refreshSmartFeeds) _ = RefreshSmartFeedCountsAsync();
    }

    private async Task RefreshSmartFeedCountsAsync()
    {
        try { await SmartFeedsController.Shared.RefreshAllAsync(); }
        catch { return; }
        foreach (var section in SidebarItems)
            foreach (var child in section.Children)
                if (child.Tag is IPseudoFeed sf)
                    child.UnreadCount = sf.UnreadCount;
        UpdateSectionStatus();
    }

    /// <summary>Refresh the three region-specific status strings shown at the bottom of
    /// each pane (sidebar/timeline/detail) — mirrors Mac NNW's per-pane status footer.</summary>
    public void UpdateSectionStatus()
    {
#if PARITY_PROBE
        ProbeSectionStatusUpdates++;
#endif
        int accounts = AppService.Shared.Accounts.ActiveAccounts.Count();
        int feeds = AppService.Shared.Accounts.ActiveAccounts.SelectMany(a => a.FlattenedFeeds()).Count();
        int totalUnread = AppService.Shared.Accounts.ActiveAccounts.SelectMany(a => a.FlattenedFeeds()).Sum(f => f.UnreadCount);
        bool showCounts = _unreadDisplayMode == UnreadCountDisplay.Count;
        SidebarStatus = $"{accounts} account{Plural(accounts)} · {feeds} feed{Plural(feeds)}"
            + (showCounts ? $" · {totalUnread} {AppStrings.Get("Unread")}" : "");

        int total = ArticleItems.Count;
        int unread = ArticleItems.Count(i => !i.IsRead);
        int starred = ArticleItems.Count(i => i.IsStarred);
        TimelineStatus = total == 0 ? "No articles" : $"{total} article{Plural(total)}"
            + (showCounts ? $" · {unread} {AppStrings.Get("Unread")}" : "") + $" · {starred} starred";

        UpdateDetailStatus();

        static string Plural(int n) => n == 1 ? "" : "s";
    }

    private void UpdateDetailStatus()
    {
        if (SelectedArticle is null)
        {
            DetailStatus = "";
        }
        else
        {
            var d = SelectedArticle.PublishedDate.LocalDateTime.ToString("MMM d, yyyy h:mm tt");
            var parts = new List<string> { SelectedArticle.FeedTitle };
            if (!string.IsNullOrEmpty(SelectedArticle.Author)) parts.Add(SelectedArticle.Author);
            parts.Add(d);
            if (IsReaderMode) parts.Add("Reader view");
            DetailStatus = string.Join(" · ", parts);
        }
    }

    // --- Article HTML rendering ---

    public void SetHoveredLink(string? link)
    {
        HoveredLink = string.IsNullOrEmpty(link) ? null : link;
        if (string.IsNullOrEmpty(link)) UpdateDetailStatus();
        else DetailStatus = link;
    }

    [ObservableProperty]
    private string? _hoveredLink;

    private string BuildArticleHtml(FeedItem item, ExtractedArticle? extracted = null)
    {
        var article = item.SourceArticle ?? throw new InvalidOperationException("The selected article has no backing model.");
        var account = AppService.Shared.Accounts.Accounts.FirstOrDefault(candidate => candidate.AccountID == article.AccountID);
        var feed = account?.FlattenedFeeds().FirstOrDefault(candidate => candidate.FeedID == article.FeedID);
        // Loading an icon must not change the document when the same article is rebound.
        var avatar = ArticleIconIdentityUrl(item);
        CurrentRendering = ArticleRenderer.Render(article, extracted, CurrentArticleTextSize, feed, avatarUrl: avatar, accountType: account?.Type);
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CurrentRendering.Style)));
        var stylesheet = CurrentRendering.ThemeFolder is { } folder &&
            System.IO.File.Exists(System.IO.Path.Combine(folder, AppShared.ArticleThemes.ArticleTheme.StylesheetFilename))
                ? $"https://{ThemeResourceHost}{ThemeStylesheetPath}?v={token}" : null;
        return ArticleRenderer.Document(CurrentRendering, stylesheet, includeScripts: false);
    }

    private static string ArticleIconIdentityUrl(FeedItem item) => ArticleIconUrl
        + "?article=" + Uri.EscapeDataString(item.AccountID + "|" + item.Id);

    internal static string ArticleIconSource(FeedItem item) => ArticleIconIdentityUrl(item)
        + "&state=" + (item.FaviconPath is null ? "pending" : "ready")
        + "&revision=" + item.FaviconSource.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Payload for <see cref="MainViewModel.AddFeedCommand"/>.</summary>
public sealed class AddFeedRequest
{
    public string Url { get; init; } = "";
    public string? Name { get; init; }
    public string? FolderName { get; init; }
    public AccountNs.Account? Account { get; init; }
}
