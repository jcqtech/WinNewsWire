using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.Models;
using WinNewsWire.ViewModels;
using WinNewsWire.Web;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.AppShared.Sharing;
using WinNewsWire.AppShared.Commands;
using WinNewsWire.Account;
using AccountNs = WinNewsWire.Account;

namespace WinNewsWire;

public sealed partial class MainContent : UserControl
{
#if PARITY_PROBE
    internal WebView2 ProbeWebView => ContentWebView;
    internal int ProbePendingBrowserTasks => _browserTasks.Count;
    internal bool ProbeWebViewReady => _webViewReady;
    internal int ProbeSidebarRevealCount => _sidebarReveals.Count;
    internal bool ProbeSidebarDeleteInProgress => _sidebarDeleteInProgress;
    internal int ProbeSidebarDragStarts, ProbeSidebarDragCompletions, ProbeSidebarInsertionTargets, ProbeSidebarFolderTargets;
    internal bool ProbeArticleReady => _webViewReady && _pendingArticleDocumentUri is null && _pendingRestoredScroll is null
        && _renderedArticleHtml == ViewModel.ArticleHtml && _readerExecutionContext is not null
        && _readerNavigation == _articleNavigationId && _findDocumentReady && _browserTasks.All(task => task.IsCompleted);
    internal Action<Windows.ApplicationModel.DataTransfer.DataPackage>? ProbeClipboardWriter { get; set; }
    internal Action<Windows.ApplicationModel.DataTransfer.DataPackage>? ProbeShareWriter { get; set; }
    internal static Windows.ApplicationModel.DataTransfer.DataPackageOperation ProbeSidebarDropOperation(
        IReadOnlyList<SidebarItem> items, AccountNs.Account account, AccountNs.Folder? folder, bool copyRequested)
        => SidebarDropOperation(items, account, folder, copyRequested);
    internal Task ProbeTransferSidebarItemsAsync(IReadOnlyList<SidebarItem> items, AccountNs.Account account,
        AccountNs.Folder? folder, bool copyRequested) => TransferSidebarItemsAsync(items, account, folder, copyRequested);
    internal Task ProbePlaceSidebarFeedsAsync(IReadOnlyList<SidebarItem> items, AccountNs.Account account,
        AccountNs.Folder? folder, string? before) => PlaceSidebarFeedsAsync(items, account, folder, before);
    internal static (AccountNs.Account? Account, AccountNs.Folder? Folder) ProbeExternalFeedDropDestination(SidebarItem? item)
        => ExternalFeedDropDestination(item);
    internal static bool ProbeCanDropExternalFeed(Windows.ApplicationModel.DataTransfer.DataPackageView data, SidebarItem? item)
    {
        var (account, folder) = ExternalFeedDropDestination(item);
        return CanDropExternalFeed(data, account, folder);
    }
#endif
    public MainViewModel ViewModel { get; } = new();

    private bool _webViewReady;
    private string? _pendingArticleDocumentUri;
    private string? _renderedArticleHtml;
    private bool _shuttingDown;
    private bool _restoringLayout;
    private readonly TaskCompletionSource _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Initialization => _initialization.Task;
    private readonly HashSet<Task> _browserTasks = [];
    private Task? _shutdownTask;
    internal bool IsShuttingDown => _shuttingDown;
    private bool _isSidebarDragging;
    private bool _isArticleDragging;
    private ScrollViewer? _articleScrollViewer;
    private UIElement? _articleWheelSurface;
    private readonly List<UIElement> _articleWheelTargets = [];
    private PointerEventHandler? _articleWheelHandler;
    private int _articleWheelDirection;
    private double _dragStartX;
    private double _dragStartWidth;
    private double _lastSidebarWidth = 260;
    private bool _syncingArticleSelection;
    private bool _timelineHadFocus;
    private HashSet<ArticleIdentityKey> _displayedSelection = new();
    private readonly List<SidebarItem> _draggedSubscriptions = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<SidebarItem> _sidebarRows = [];
    private readonly HashSet<SidebarItemKey> _expandedSidebarContainers = [];
    private readonly HashSet<SidebarItemKey> _knownSidebarContainers = [];
    private bool _syncingSidebarSelection;
    private const string SidebarDragFormat = "WinNewsWire.SidebarDragSession";
    private static SidebarDragSession? _activeSidebarDrag;
    private sealed record SidebarDragSession(string ID, MainContent Owner, IReadOnlyList<SidebarItem> Items)
    {
        public MainContent? FeedbackOwner { get; set; }
    }
    private AccountNs.Account? _dragDestinationAccount;
    private AccountNs.Folder? _dragDestinationFolder;
    private Windows.ApplicationModel.DataTransfer.DataTransferManager? _sharingManager;
    private Windows.Foundation.TypedEventHandler<Windows.ApplicationModel.DataTransfer.DataTransferManager,
        Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs>? _shareRequestHandler;

    public MainContent()
    {
        try
        {
            InitializeComponent();
            InitializeArticleFindControls();
            InitializeToolbar();
            InitializeColumnLayout();
            InitializeStatusBar();
        }
        catch (Exception error)
        {
            App.LogException("Initialize main reader controls", error);
            throw;
        }
        InitializeWebViewAsync();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SelectionNavigationStarting += SelectionNavigationStarting;
        WinNewsWire.Core.AppDefaults.Shared.Changed += OnArticlePreferencesChanged;
        ViewModel.SidebarRebuilt += () =>
        {
            if (_shuttingDown) return;
            QueueStatusBarUpdate();
            if (DispatcherQueue.HasThreadAccess) { BuildSidebarUI(); QueueToolbarUpdate(); }
            else DispatcherQueue.TryEnqueue(() => { if (!_shuttingDown) { BuildSidebarUI(); QueueToolbarUpdate(); } });
        };
        // Suspend single-key accelerators (j/k/r/m/s/u/l) while the inline
        // rename TextBox has focus so typing those letters doesn't trigger
        // sidebar actions. Ctrl-modified accelerators stay live so standard
        // text-editing shortcuts (Ctrl+A/C/V/X/Z) keep working in the field.
        Loaded += MainContent_Loaded;
        SidebarListView.ItemsSource = _sidebarRows;
        SidebarListView.AddHandler(UIElement.DragOverEvent, new DragEventHandler(SidebarListView_DragOver), true);
        SidebarListView.AddHandler(UIElement.DropEvent, new DragEventHandler(SidebarListView_Drop), true);

        // App-wide guard: whenever any text-input control (TextBox, PasswordBox,
        // AutoSuggestBox, RichEditBox) gains focus, suspend the unmodified
        // KeyboardAccelerators on this UserControl so typing letters doesn't
        // trigger sidebar/article actions. Restore the moment focus moves to a
        // non-text control. Catches the title-bar search box, dialog inputs,
        // and the feedback window inputs in addition to the inline rename box.
        Microsoft.UI.Xaml.Input.FocusManager.GotFocus += FocusManager_GotFocus;
        Microsoft.UI.Xaml.Input.FocusManager.LostFocus += FocusManager_LostFocus;
    }

    private static bool IsTextInputControl(object? element)
        => element is TextBox or PasswordBox or AutoSuggestBox or RichEditBox;

    private void FocusManager_GotFocus(object? sender, Microsoft.UI.Xaml.Input.FocusManagerGotFocusEventArgs e)
    {
        if (_shuttingDown || e.NewFocusedElement is FrameworkElement element && !ReferenceEquals(element.XamlRoot, XamlRoot)) return;
        if (IsWithin(e.NewFocusedElement as DependencyObject, ArticleListView)) _timelineHadFocus = true;
        else if (IsTextInputControl(e.NewFocusedElement) || IsWithin(e.NewFocusedElement as DependencyObject, SidebarListView)
            || IsWithin(e.NewFocusedElement as DependencyObject, WebPageView))
            _timelineHadFocus = false;
        if (IsTextInputControl(e.NewFocusedElement) || IsWithin(e.NewFocusedElement as DependencyObject, WebPageView))
            SuspendSingleKeyAccelerators();
    }

    private static bool IsWithin(DependencyObject? element, DependencyObject ancestor)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void ArticleListView_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_shuttingDown && _articleScrollViewer is { } scroll
            && e.Pointer.PointerDeviceType is PointerDeviceType.Touch or PointerDeviceType.Pen)
        {
            _articleWheelDirection = 0;
            scroll.IsScrollInertiaEnabled = true;
        }
    }

    private void ArticleListView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_shuttingDown || _articleScrollViewer is not { } scroll) return;
        var properties = e.GetCurrentPoint(ArticleListView).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0
            || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        int direction = Math.Sign(properties.MouseWheelDelta);
        if (direction != _articleWheelDirection)
        {
            // Cancel the previous destination before native handling starts the new wheel animation.
            double offset = scroll.VerticalOffset;
            _articleWheelSurface?.CancelDirectManipulations();
            scroll.ChangeView(null, offset, null, disableAnimation: true);
        }
        _articleWheelDirection = direction;
    }

    private void ArticleListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        QueueTimelineAppearanceUpdate();
        if (_syncingArticleSelection || ViewModel.IsRestoringArticleSelection) return;
        _syncingArticleSelection = true;
        try { ViewModel.SetSelectedArticles(ArticleListView.SelectedItems.OfType<FeedItem>()); }
        finally { _syncingArticleSelection = false; }
    }

    private void SyncArticleSelection()
    {
        if (_shuttingDown || _syncingArticleSelection) return;
        _syncingArticleSelection = true;
        try
        {
            var identities = ViewModel.SelectedArticles.Select(MainViewModel.Identity).ToHashSet();
            bool identityChanged = !_displayedSelection.SetEquals(identities);
            var desired = ViewModel.SelectedArticles.ToHashSet();
            foreach (var item in ArticleListView.SelectedItems.OfType<FeedItem>().ToList())
                if (!desired.Contains(item)) ArticleListView.SelectedItems.Remove(item);
            foreach (var item in ViewModel.SelectedArticles)
                if (!ArticleListView.SelectedItems.Contains(item)) ArticleListView.SelectedItems.Add(item);
            if (identityChanged && ViewModel.SelectedArticles.Count == 1)
            {
                var article = ViewModel.SelectedArticles[0];
                RevealTimelineSelection(article);
            }
            _displayedSelection = identities;
        }
        finally { _syncingArticleSelection = false; }
    }

    public bool TrySelectAllArticles()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
        if (!IsTextInputControl(focused) && IsWithin(focused as DependencyObject, SidebarListView))
        {
            _syncingSidebarSelection = true;
            try
            {
                SidebarListView.SelectedItems.Clear();
                foreach (var item in _sidebarRows.Where(item => item.IsSelectable))
                    SidebarListView.SelectedItems.Add(item);
                ViewModel.SetSelectedSidebarItems(_sidebarRows.Where(item => item.IsSelectable));
            }
            finally { _syncingSidebarSelection = false; }
            return true;
        }
        if (!_timelineHadFocus) return false;
        ArticleListView.SelectAll();
        return true;
    }

    public bool TryCopySelectedArticles()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
        if (!IsTextInputControl(focused) && IsWithin(focused as DependencyObject, SidebarListView))
        {
            if (ViewModel.SelectedSidebarItems.Count == 0) return false;
            CopyToClipboard(string.Join(Environment.NewLine, ViewModel.SelectedSidebarItems.Select(item =>
                item.Tag is AccountNs.Feed feed ? feed.Url : item.Title)));
            return true;
        }
        if (!_timelineHadFocus || !ViewModel.HasSelectedArticles) return false;
        var models = ViewModel.SelectedArticles.Select(a => a.SourceArticle).OfType<WinNewsWire.Articles.Article>().ToList();
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ArticleSharing.PlainText(models));
        package.SetHtmlFormat(Windows.ApplicationModel.DataTransfer.HtmlFormatHelper.CreateHtmlFormat(ArticleSharing.Html(models)));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        return true;
    }

    private void FocusManager_LostFocus(object? sender, Microsoft.UI.Xaml.Input.FocusManagerLostFocusEventArgs e)
    {
        if (_shuttingDown || e.OldFocusedElement is FrameworkElement element && !ReferenceEquals(element.XamlRoot, XamlRoot)) return;
        if (IsTextInputControl(e.OldFocusedElement) || IsWithin(e.OldFocusedElement as DependencyObject, WebPageView))
            ResumeSingleKeyAccelerators();
    }

    /// <summary>Keyboard accelerators that were enabled before the inline-rename
    /// suspended them. Re-enabled on commit/cancel.</summary>
    private readonly List<Microsoft.UI.Xaml.Input.KeyboardAccelerator> _suspendedAccelerators = new();

    /// <summary>Disable every single-key (unmodified) <see cref="KeyboardAccelerator"/>
    /// on this UserControl so it can't fire while the user is typing in the
    /// inline rename TextBox. We don't touch Ctrl-modified accelerators because
    /// they're useful inside the text field (Ctrl+A/C/V/X/Z).</summary>
    private void SuspendSingleKeyAccelerators()
    {
        if (_suspendedAccelerators.Count > 0) return;
        foreach (var acc in this.KeyboardAccelerators)
        {
            if (acc.IsEnabled
                && acc.Modifiers == Windows.System.VirtualKeyModifiers.None)
            {
                acc.IsEnabled = false;
                _suspendedAccelerators.Add(acc);
            }
        }
    }

    private void ResumeSingleKeyAccelerators()
    {
        if (_toolbarDialog is not null) return;
        foreach (var acc in _suspendedAccelerators) acc.IsEnabled = true;
        _suspendedAccelerators.Clear();
    }

    private bool _initialized;

    private async void MainContent_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            UpdateToolbarLayout();
            ArticleListView.ApplyTemplate();
            // ListView's template does not forward the attached inertia setting to this ScrollViewer.
            _articleScrollViewer = FindDescendantByName(ArticleListView, "ScrollViewer") as ScrollViewer
                ?? throw new InvalidOperationException("The article list scroll viewer was not created.");
            _articleScrollViewer.IsScrollInertiaEnabled = true;
            InitializeTimelineThumbnails();
            _articleWheelHandler = ArticleListView_PointerWheelChanged;
            _articleWheelSurface = FindDescendantByName(_articleScrollViewer, "ScrollContentPresenter") as UIElement
                ?? _articleScrollViewer.Content as UIElement
                ?? throw new InvalidOperationException("The article list scroll surface was not created.");
            foreach (var target in new[]
            {
                _articleWheelSurface,
                FindDescendantByName(_articleScrollViewer, "VerticalScrollBar") as UIElement,
                FindDescendantByName(_articleScrollViewer, "HorizontalScrollBar") as UIElement
            }.OfType<UIElement>())
            {
                _articleWheelTargets.Add(target);
                target.AddHandler(UIElement.PointerWheelChangedEvent, _articleWheelHandler, handledEventsToo: true);
            }
            WinNewsWire.Input.MiddleClickAutoScroll.Attach(ArticleListView);
            WinNewsWire.Input.MiddleClickAutoScroll.Attach(SidebarListView);
            // RightTapped reliably tells us the visual that was right-clicked
            // (OriginalSource is stable across bubbling, unlike ContextRequested).
            // We pair it with Closed handlers so the captured target is cleared
            // when the flyout dismisses — necessary because RightTapped doesn't
            // fire on TreeView whitespace in WinUI 3.
            SidebarListView.RightTapped += SidebarListView_RightTapped;
            ArticleListView.RightTapped += ArticleListView_RightTapped;
            SidebarContextMenu.Closed += SidebarContextMenu_Closed;
            ArticleContextMenu.Closed += ArticleContextMenu_Closed;
            // Initialize state-dependent toolbar labels.
            UpdateStarButtonIcon();
            UpdateReaderModeButtonLabel();
            await ViewModel.InitializeAsync();
            if (_shuttingDown) { _initialization.TrySetCanceled(); return; }
            BuildSidebarUI();
            ApplyUnifiedLayout();
            _initialization.TrySetResult();
        }
        catch (Exception ex)
        {
            _initialization.TrySetException(ex);
            App.LogException("MainContent.Loaded", ex);
            throw;
        }
    }

    internal MainWindowState CaptureState(WindowBounds bounds, bool maximized) => new()
    {
        Bounds = bounds,
        IsMaximized = maximized,
        SidebarWidth = SidebarColumn.Width.Value > 0 ? SidebarColumn.Width.Value : _lastSidebarWidth,
        TimelineWidth = !_appliedColumnLayout && ArticleListColumn.Width.IsAbsolute && ArticleListColumn.Width.Value > 0
            ? ArticleListColumn.Width.Value : _lastArticleListWidth,
        ColumnLayoutTimelineHeight = _appliedColumnLayout && !_appliedReadingOnly && ColumnTimelineRow.ActualHeight > 0
            ? ColumnTimelineRow.ActualHeight : _lastColumnTimelineHeight,
        IsSidebarHidden = !ViewModel.IsSidebarVisible && !_autoCollapsedSidebar,
        IsUnifiedLayout = ViewModel.IsUnifiedLayout,
        Sidebar = new()
        {
            IsReadFiltered = ViewModel.IsSidebarReadFiltered,
            SelectedItems = ViewModel.SelectedSidebarItems.Select(item => item.Identity!.Value).ToArray(),
            ExpandedContainers = _initialization.Task.IsCompletedSuccessfully
                ? SidebarContainers().Where(container => _expandedSidebarContainers.Contains(container.Identity!.Value))
                    .Select(container => container.Identity!.Value).ToArray() : null
        },
        Timeline = ViewModel.CaptureTimelineState(),
        Detail = new(ViewModel.IsReaderMode && ViewModel.HasExtractedArticle,
            ViewModel.SelectedArticle is { } article && MainViewModel.Identity(article) == _renderedArticleIdentity ? _articleScrollPosition : 0)
    };

    internal async Task RestoreStateAsync(MainWindowState state)
    {
        await _initialization.Task;
        if (_shuttingDown) throw new OperationCanceledException();
        _restoringLayout = true;
        ++_browsingVersion;
        ShowWebPage(false);
        bool layoutApplied = false;
        ++_sidebarAnimationVersion;
        try
        {
            _lastSidebarWidth = Math.Clamp(state.SidebarWidth, 200, 400);
            _lastArticleListWidth = Math.Clamp(state.TimelineWidth, 280, 600);
            _lastColumnTimelineHeight = state.ColumnLayoutTimelineHeight;
            SidebarColumn.Width = new GridLength(_lastSidebarWidth);
            ArticleListColumn.Width = new GridLength(_lastArticleListWidth);
            _autoCollapsedSidebar = false;
            _autoCollapsedArticleList = false;
            _expandedSidebarContainers.Clear();
            _knownSidebarContainers.Clear();
            var savedContainers = state.Sidebar?.ExpandedContainers;
            var expanded = (savedContainers ?? state.Sidebar?.ExpandedFolders ?? []).ToHashSet();
            foreach (var container in SidebarContainers())
            {
                var key = container.Identity!.Value;
                _knownSidebarContainers.Add(key);
                if (expanded.Contains(key))
                    _expandedSidebarContainers.Add(key);
                container.IsExpanded = _expandedSidebarContainers.Contains(key);
            }
            var detail = state.Detail ?? new();
            if (state.Timeline?.SelectedArticle is { } identity)
                _pendingRestoredScroll = (identity, Math.Max(0, detail.ScrollY), detail.IsReaderMode);
            ViewModel.RestoreWindowLayout(state);
            _restoringLayout = false;
            ApplyMainLayout(rememberGeometry: false);
            MainContentGrid.UpdateLayout();
            layoutApplied = true;
            _restoringLayout = true;
            await ViewModel.RestoreTimelineStateAsync(state);
            if (_shuttingDown) throw new OperationCanceledException();
            BuildSidebarUI();
            SyncArticleSelection();
            if (_pendingRestoredScroll is { WaitForReader: true } pending && !ViewModel.HasExtractedArticle)
            {
                _pendingRestoredScroll = (pending.Identity, pending.Y, false);
                if (_webViewReady && !string.IsNullOrEmpty(ViewModel.ArticleHtml)) LoadArticleHtml(ViewModel.ArticleHtml);
            }
        }
        finally
        {
            _restoringLayout = false;
            if (!_shuttingDown && !layoutApplied) ApplyMainLayout(rememberGeometry: false);
        }
    }

    /// <summary>
    /// Closes the WebView2 control cleanly before app/window teardown.
    /// WinUI 3 apps otherwise throw winrt::hresult_error (0x8007139F,
    /// "The group or resource is not in the correct state...") during exit
    /// because the CoreWebView2 environment is still running when the UI
    /// thread unwinds. Calling Close() shuts it down on the UI thread first.
    /// </summary>
    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        if (_shuttingDown) return;
        ClearSidebarDropFeedback();
        if (ReferenceEquals(_activeSidebarDrag?.FeedbackOwner, this)) _activeSidebarDrag.FeedbackOwner = null;
        if (ReferenceEquals(_activeSidebarDrag?.Owner, this))
        {
            _activeSidebarDrag.FeedbackOwner?.ClearSidebarDropFeedback();
            _activeSidebarDrag = null;
        }
        try
        {
            _shuttingDown = true;
            DisposeTimelineAppearance();
            _timelineFeedIcons.Clear();
            await StopTimelineThumbnailsAsync();
            DisposeStatusBar();
            CancelTimelineReveal();
            ArticleListView.PointerEntered -= ArticleListView_PointerEntered;
            if (_articleWheelHandler is { } wheel)
            {
                foreach (var target in _articleWheelTargets)
                    target.RemoveHandler(UIElement.PointerWheelChangedEvent, wheel);
            }
            _articleWheelTargets.Clear();
            _articleWheelSurface = null;
            _articleWheelHandler = null;
            _articleScrollViewer = null;
            StopSidebarReveals();
            ++_browsingVersion;
            await WinNewsWire.AppRuntime.AppService.Shared.Speech.StopAsync(this);
            ShutdownToolbar();
            StopArticleFind(detach: true);
            _navigationCancellation.Cancel();
            await _nextUnreadTask;
            await _backTask;
            _navigationCancellation.Dispose();
            _readerExecutionContext = null;
            ContentWebView.SizeChanged -= ReaderSizeChanged;
            if (_readerResizeTimer is { } resizeTimer)
            {
                resizeTimer.Stop();
                resizeTimer.Tick -= ReaderResizeTimerTick;
                _readerResizeTimer = null;
            }
            if (_readerMessages is not null) _readerMessages.DevToolsProtocolEventReceived -= ReaderMessageReceived;
            _initialization.TrySetCanceled();
            _webViewReady = false;
            if (_starWatchedArticle is { } watched) watched.PropertyChanged -= SelectedArticle_PropertyChanged;
            _starWatchedArticle = null;
            ViewModel.Dispose();
            ViewModel.SelectionNavigationStarting -= SelectionNavigationStarting;
            if (_sharingManager is not null && _shareRequestHandler is not null)
                _sharingManager.DataRequested -= _shareRequestHandler;
            WinNewsWire.Core.AppDefaults.Shared.Changed -= OnArticlePreferencesChanged;
            // Detach ViewModel events first so any background activity during
            // AppService.Stop() can't dispatch back into XAML elements that
            // are about to be (or already) torn down. Otherwise we get a
            // RO_E_CLOSED (0x80000013) thrown on the UI thread during exit.
            try { ViewModel.PropertyChanged -= ViewModel_PropertyChanged; } catch { }
            // FocusManager.GotFocus/LostFocus are static events; subscribing
            // pins this MainContent instance for the lifetime of the static
            // FocusManager. On window close those handlers can still fire
            // against a torn-down XAML tree, producing an AccessViolation
            // inside Microsoft.ui.xaml.dll. Unsubscribe defensively.
            try { Microsoft.UI.Xaml.Input.FocusManager.GotFocus -= FocusManager_GotFocus; } catch { }
            try { Microsoft.UI.Xaml.Input.FocusManager.LostFocus -= FocusManager_LostFocus; } catch { }
            await Task.WhenAll(_browserTasks.ToArray());
            if (_readerRuntimeEnabled && ContentWebView.CoreWebView2 is { } core)
            {
                await core.CallDevToolsProtocolMethodAsync("Runtime.removeBinding", JsonSerializer.Serialize(new { name = ReaderBindingName }));
                await core.CallDevToolsProtocolMethodAsync("Runtime.disable", "{}");
                _readerRuntimeEnabled = false;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            App.LogException("WebView2 shutdown", ex);
        }
        finally
        {
            try { WebPageView?.Close(); }
            finally { ContentWebView?.Close(); }
        }
    }

    private async void TrackBrowserTask(Task task)
    {
        _browserTasks.Add(task);
        try { await task; }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
            or ArgumentException or JsonException or IOException)
        {
            App.LogException("Article browser operation", error);
            if (!_shuttingDown) ViewModel.StatusMessage = "Could not update the article browser: " + error.Message;
        }
        finally { _browserTasks.Remove(task); }
    }

    private const string IframeProxyBase = "https://winnewswire.local/iframe-proxy";
    private string? _mappedThemeFolder;
    private ulong _articleNavigationId;
    private ArticleIdentityKey? _renderedArticleIdentity;
    private double _articleScrollPosition;
    private double _restoreArticleScrollPosition;
    private (ArticleIdentityKey Identity, double Y, bool WaitForReader)? _pendingRestoredScroll;
    private int _htmlLoadGeneration;

    private static readonly System.Text.RegularExpressions.Regex IframeRegex =
        new(@"<iframe\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""'][^>]*?>\s*(?:</iframe>)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Push the user's chosen appearance into the WebView2 profile so
    /// the article view's <c>prefers-color-scheme</c> media query agrees with
    /// the rest of the WinUI shell. Called once at WebView init and again
    /// whenever the user flips the appearance menu.</summary>
    public void ApplyWebViewColorScheme()
    {
        try
        {
            var profile = ContentWebView?.CoreWebView2?.Profile;
            if (profile is null) return;
            profile.PreferredColorScheme = WinNewsWire.Core.AppDefaults.Shared.AppearanceMode switch
            {
                WinNewsWire.Core.AppDefaults.Appearance.Light => CoreWebView2PreferredColorScheme.Light,
                WinNewsWire.Core.AppDefaults.Appearance.Dark => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
        }
        catch { /* older WebView2 runtimes ignore PreferredColorScheme — no-op */ }
    }

    private async void InitializeWebViewAsync()
    {
        try
        {
            await ContentWebView.EnsureCoreWebView2Async(await AppRuntime.WebViewEnvironment.GetAsync());
            if (_shuttingDown) return;
            WinNewsWire.Web.UserAgent.BrowserValue = ContentWebView.CoreWebView2.Settings.UserAgent;
            ContentWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ContentWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ContentWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            ContentWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Have the article webview honor the app's appearance preference so
            // the `@media (prefers-color-scheme: dark)` rules inside the article
            // stylesheet light up the right palette even when the user has chosen
            // a theme that differs from Windows.
            ApplyWebViewColorScheme();

            ContentWebView.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (!_showingWebPage && e.IsUserInitiated) TrackBrowserTask(OpenLinkInWebViewAsync(e.Uri));
                else ViewModel.StatusMessage = "An automatic article popup was blocked.";
            };

            // Embeds need an HTTP(S) parent origin; theme assets stay in their mapped folder.
            ContentWebView.CoreWebView2.AddWebResourceRequestedFilter(
                "https://winnewswire.local/*", CoreWebView2WebResourceContext.All);
            ContentWebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
            ContentWebView.CoreWebView2.NavigationStarting += (sender, navigation) =>
            {
                bool isArticleDocument = _pendingArticleDocumentUri is not null
                    && !navigation.IsUserInitiated && !navigation.IsRedirected
                    && string.Equals(navigation.Uri, _pendingArticleDocumentUri, StringComparison.Ordinal);
                if (isArticleDocument && (_renderedArticleHtml != ViewModel.ArticleHtml
                    || ViewModel.SelectedArticle is not { } selected || MainViewModel.Identity(selected) != _renderedArticleIdentity))
                {
                    navigation.Cancel = true;
                    _pendingArticleDocumentUri = null;
                    return;
                }
                if (!isArticleDocument)
                {
                    navigation.Cancel = true;
                    if (!_shuttingDown) ViewModel.StatusMessage = "An automatic article navigation was blocked.";
                    return;
                }
                if (isArticleDocument) _pendingArticleDocumentUri = null;
                _articleNavigationId = navigation.NavigationId;
                _readerExecutionContext = null;
                _findDocumentReady = false;
                StopArticleFind();
            };
            ContentWebView.CoreWebView2.NavigationCompleted += OnArticleNavigationCompleted;
            ContentWebView.CoreWebView2.DOMContentLoaded += OnArticleDomContentLoaded;
            var interaction = InitializeReaderInteractionAsync(ContentWebView.CoreWebView2);
            TrackBrowserTask(interaction);
            await interaction;
            if (_shuttingDown) return;

            _webViewReady = true;

            if (!string.IsNullOrEmpty(ViewModel.ArticleHtml))
                LoadArticleHtml(ViewModel.ArticleHtml);
        }
        catch (Exception ex)
        {
            ReportArticleLoadError("Initialize article reader", ex);
        }
    }

    private void OnArticleNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_shuttingDown || args.NavigationId != _articleNavigationId || args.IsSuccess) return;
        if (args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled && _pendingArticleDocumentUri is not null) return;
        ReportArticleLoadError("Display article", new InvalidOperationException("The article document could not load: " + args.WebErrorStatus));
    }

    private void ReportArticleLoadError(string operation, Exception error)
    {
        App.LogException(operation, error);
        if (_shuttingDown) return;
        _pendingArticleDocumentUri = null;
        _renderedArticleHtml = null;
        ViewModel.StatusMessage = "Could not display the article: " + error.Message;
        ViewModel.ArticleSelectionMessage = ViewModel.StatusMessage;
        ContentWebView.Visibility = Visibility.Collapsed;
        EmptyContentState.Visibility = _showingWebPage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnArticleDomContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
        => TrackBrowserTask(ProcessArticleDocumentAsync(sender, args));

    private async Task ProcessArticleDocumentAsync(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        if (_shuttingDown || args.NavigationId != _articleNavigationId) return;
        try
        {
            // ExecuteScriptAsync remains available when untrusted document scripts are disabled.
            var script = "(function () { if (window.__wnwArticleProcessed) return;\n"
                + AppShared.ArticleRendering.ArticleRenderer.BrowserScripts
                + "\n;const process = processPage; processPage = function () { if (!window.__wnwArticleProcessed) process(); };"
                + "window.selectedHTML = selectedHTML; window.stopMediaPlayback = stopMediaPlayback; processPage(); })();";
            await sender.ExecuteScriptAsync(script);
            if (_shuttingDown || args.NavigationId != _articleNavigationId) return;
            await InstallReaderInteractionAsync(sender, args.NavigationId);
            if (_shuttingDown || args.NavigationId != _articleNavigationId) return;
            var scroll = _restoreArticleScrollPosition > 0
                ? "window.scrollTo(0," + _restoreArticleScrollPosition.ToString(System.Globalization.CultureInfo.InvariantCulture) + ");"
                : "";
            await sender.ExecuteScriptAsync(scroll + "window.__wnwNavigationId = "
                + JsonSerializer.Serialize(args.NavigationId.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ";");
            if (!_shuttingDown && args.NavigationId == _articleNavigationId) await RefreshArticleIconAsync();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or JsonException or InvalidDataException)
        {
            if (_shuttingDown || args.NavigationId != _articleNavigationId) return;
            System.Diagnostics.Trace.TraceError($"Article page processing failed: {error}");
            if (!_shuttingDown) ViewModel.StatusMessage = "Could not finish formatting the article: " + error.Message;
        }
        finally
        {
            if (!_shuttingDown && args.NavigationId == _articleNavigationId)
            {
                _findDocumentReady = true;
                if (!_showingWebPage && IsArticleFindVisible) await QueueArticleFindAsync(ArticleFindOperation.Start, reset: true);
            }
        }
    }

    private async void DispatchWebViewShortcut(string? cmd)
    {
        try
        {
            switch (cmd)
            {
                case "next":             ViewModel.NextArticleCommand.Execute(null); break;
                case "prev":             ViewModel.PreviousArticleCommand.Execute(null); break;
                case "refresh":
                    await ViewModel.ReloadFeedsCommand.ExecuteAsync(null);
                    BuildSidebarUI();
                    break;
                case "markRead":         await ViewModel.MarkSelectedReadCommand.ExecuteAsync(null); break;
                case "toggleStar":       await ViewModel.ToggleStarredCommand.ExecuteAsync(null); break;
                case "markAllRead":
                    await ViewModel.MarkAllReadCommand.ExecuteAsync(null);
                    ViewModel.BuildSidebar();
                    BuildSidebarUI();
                    break;
                case "inspector":
                    await ShowInspectorAsync();
                    break;
                case "newFeed":          await NewFeedAsync(); break;
                case "newFolder":        await NewFolderAsync(); break;
                case "toggleSidebar":    ViewModel.ToggleSidebarCommand.Execute(null); break;
                case "toggleReaderMode": ViewModel.ToggleReaderModeCommand.Execute(null); break;
                case "nextUnread":       await SelectNextUnreadAsync(); break;
                case "gotoToday":        await GoToSmartFeedAsync(WinNewsWire.AppShared.SmartFeeds.SmartFeedsController.Shared.TodayFeed); break;
                case "gotoUnread":       await GoToSmartFeedAsync(WinNewsWire.AppShared.SmartFeeds.SmartFeedsController.Shared.UnreadFeed); break;
                case "gotoStarred":      await GoToSmartFeedAsync(WinNewsWire.AppShared.SmartFeeds.SmartFeedsController.Shared.StarredFeed); break;
                case "copyArticleUrls":  CopySelectedLink(); break;
                case "copyExternalUrls": CopySelectedLink(external: true); break;
                case "findInArticle":    await ShowArticleFindAsync(); break;
                case "findNext":         await FindNextInArticleAsync(); break;
                case "findPrevious":     await FindNextInArticleAsync(backwards: true); break;
                case "useSelectionForFind": await UseArticleSelectionForFindAsync(); break;
                case "jumpToSelection":  await JumpToArticleSelectionAsync(); break;
                case "articleSearch":
                    (App.WindowForXamlRoot(XamlRoot) as MainWindow)?.FocusArticleSearch();
                    break;
                case "toggleToolbar": ToggleToolbar(); break;
                case "openBrowser":      OpenSelectedInBrowser(); break;
                case "fullScreen":
                    (App.WindowForXamlRoot(XamlRoot) as MainWindow
                        ?? throw new InvalidOperationException("The reader's main window is unavailable.")).ToggleFullScreen();
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
            or System.Runtime.InteropServices.COMException or System.Net.Http.HttpRequestException)
        {
            App.LogException("Reader shortcut " + cmd, ex);
            if (!_shuttingDown) ViewModel.StatusMessage = "Could not complete the reader shortcut: " + ex.Message;
        }
    }

    internal async Task CaptureScrollPositionAsync()
    {
        if (_shuttingDown || !_webViewReady || _pendingArticleDocumentUri is not null || ViewModel.SelectedArticle is not { } article
            || MainViewModel.Identity(article) != _renderedArticleIdentity) return;
        var navigation = _articleNavigationId;
        var identity = _renderedArticleIdentity;
        string json;
        try
        {
            // Navigation and shutdown capture directly instead of relying on coalesced scroll notifications.
            json = await ContentWebView.ExecuteScriptAsync("({navigation:window.__wnwNavigationId,offset:window.scrollY})");
        }
        catch (Exception error) when (_shuttingDown && (error is System.Runtime.InteropServices.COMException or InvalidOperationException)) { return; }
        if (_shuttingDown || navigation != _articleNavigationId || identity != _renderedArticleIdentity) return;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("navigation", out var savedNavigation)
            || savedNavigation.ValueKind != JsonValueKind.String
            || savedNavigation.GetString() != navigation.ToString(System.Globalization.CultureInfo.InvariantCulture)) return;
        if (!root.TryGetProperty("offset", out var offset) || !offset.TryGetDouble(out var y) || !double.IsFinite(y) || y < 0)
            throw new System.IO.InvalidDataException("The article returned an invalid reading position.");
        _articleScrollPosition = y;
    }

    private void LoadArticleHtml(string html, bool forceReload = false)
    {
        if (_shuttingDown) return;
        bool hasContent = !string.IsNullOrEmpty(html);
        EmptyContentState.Visibility = hasContent || _showingWebPage ? Visibility.Collapsed : Visibility.Visible;
        ContentWebView.Visibility = hasContent && !_showingWebPage ? Visibility.Visible : Visibility.Collapsed;
        if (hasContent) TrackBrowserTask(LoadArticleHtmlAsync(html, forceReload));
        else
        {
            ++_htmlLoadGeneration;
            _pendingArticleDocumentUri = null;
            _renderedArticleHtml = null;
        }
    }

    private async Task LoadArticleHtmlAsync(string html, bool forceReload)
    {
        if (_shuttingDown) return;
        int generation = ++_htmlLoadGeneration;
        var article = ViewModel.SelectedArticle;
        ArticleIdentityKey? identity = article is null ? null : MainViewModel.Identity(article);
        var themeFolder = ViewModel.CurrentRendering?.ThemeFolder;
        var thumbnailMode = ViewModel.TimelineThumbnailsDisplay.ToThumbnailMode();
        bool routesThumbnailImages = ShowsTimelineThumbnails;
        bool IsCurrent() => !_shuttingDown && generation == _htmlLoadGeneration && html == ViewModel.ArticleHtml
            && ViewModel.SelectedArticle is { } selected && MainViewModel.Identity(selected) == identity
            && string.Equals(themeFolder, ViewModel.CurrentRendering?.ThemeFolder, StringComparison.OrdinalIgnoreCase)
            && thumbnailMode == ViewModel.TimelineThumbnailsDisplay.ToThumbnailMode()
            && routesThumbnailImages == ShowsTimelineThumbnails;
        if (!forceReload && _pendingRestoredScroll is null && identity == _renderedArticleIdentity
            && html == _renderedArticleHtml && string.Equals(themeFolder, _mappedThemeFolder, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (identity == _renderedArticleIdentity && _pendingRestoredScroll is null)
                await CaptureScrollPositionAsync();
            if (!IsCurrent()) return;
            string? thumbnailUrl = null;
            if (routesThumbnailImages && thumbnailMode is { } mode
                && article?.SourceArticle is { } source && _thumbnailService is { } images)
                thumbnailUrl = await images.ThumbnailUrlAsync(source, mode, _thumbnailLifetime.Token);
            if (!IsCurrent()) return;
            NavigateArticleHtml(html, article, identity, themeFolder, thumbnailUrl);
        }
        catch (OperationCanceledException) when (_shuttingDown || _thumbnailLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
            or JsonException or System.IO.InvalidDataException)
        {
            if (generation == _htmlLoadGeneration) ReportArticleLoadError("Display article", error);
        }
    }

    private void NavigateArticleHtml(string html, Models.FeedItem? article, ArticleIdentityKey? identity, string? themeFolder,
        string? thumbnailUrl)
    {
        if (identity != _renderedArticleIdentity) _ = WinNewsWire.AppRuntime.AppService.Shared.Speech.StopAsync(this);
        _restoreArticleScrollPosition = identity == _renderedArticleIdentity ? _articleScrollPosition : 0;
        if (_pendingRestoredScroll is { } pending)
        {
            if (identity != pending.Identity) _pendingRestoredScroll = null;
            else if (!pending.WaitForReader || ViewModel.HasExtractedArticle || !ViewModel.IsReaderMode)
            {
                _restoreArticleScrollPosition = pending.Y;
                _pendingRestoredScroll = null;
            }
        }
        _renderedArticleIdentity = identity;
        _articleScrollPosition = _restoreArticleScrollPosition;
        if (!string.Equals(_mappedThemeFolder, themeFolder, StringComparison.OrdinalIgnoreCase))
        {
            if (_mappedThemeFolder is not null)
                ContentWebView.CoreWebView2.ClearVirtualHostNameToFolderMapping(MainViewModel.ThemeResourceHost);
            if (themeFolder is not null)
                ContentWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    MainViewModel.ThemeResourceHost, themeFolder, CoreWebView2HostResourceAccessKind.DenyCors);
            _mappedThemeFolder = themeFolder;
        }
        ContentWebView.CoreWebView2.Settings.IsScriptEnabled =
            WinNewsWire.Core.AppDefaults.Shared.ArticleContentJavascriptEnabled
            && !AppShared.ArticleRendering.ArticleRenderingSpecialCases.ShouldDisableJavaScript(
                [article?.Link, article?.FeedUrl, article?.FeedHomePageUrl]);
        var routed = thumbnailUrl is null ? html : AppShared.ArticleRendering.ArticleRenderer.RouteThumbnailImages(
            html, thumbnailUrl, ViewModel.CurrentRendering?.BaseUrl);
        var prepared = PrepareArticleHtml(routed);
        // NavigateToString raises NavigationStarting with a data URI, not its about:blank Source.
        _pendingArticleDocumentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prepared));
        _renderedArticleHtml = html;
        ContentWebView.NavigateToString(prepared);
    }

    private void OnArticlePreferencesChanged(object? sender, string key)
    {
        if (key == AppDefaultsSharedExtensions.TimelineIconSizeKey && !_shuttingDown)
        {
            if (DispatcherQueue.HasThreadAccess) QueueTimelineFeedIconsUpdate();
            else if (!DispatcherQueue.TryEnqueue(QueueTimelineFeedIconsUpdate))
                throw new InvalidOperationException("The timeline icon-size dispatcher is unavailable.");
            return;
        }
        if (key is AppDefaultsSharedExtensions.TimelineShowSeparatorsKey or AppDefaultsSharedExtensions.TimelineFadeReadArticlesKey
            or AppDefaultsSharedExtensions.TimelineFeedNameAndDateAboveTitleKey && !_shuttingDown)
        {
            if (DispatcherQueue.HasThreadAccess) QueueTimelineAppearanceUpdate();
            else if (!DispatcherQueue.TryEnqueue(QueueTimelineAppearanceUpdate))
                throw new InvalidOperationException("The timeline preference dispatcher is unavailable.");
            return;
        }
        if (key is not (WinNewsWire.Core.AppDefaults.Key.ArticleContentJavascriptEnabled
            or AppDefaultsSharedExtensions.TimelineThumbnailsDisplayKey) || !_webViewReady || _shuttingDown) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            if (!DispatcherQueue.TryEnqueue(() => OnArticlePreferencesChanged(sender, key)))
                throw new InvalidOperationException("The article preference dispatcher is unavailable.");
            return;
        }
        LoadArticleHtml(ViewModel.ArticleHtml, forceReload: true);
    }

    private async Task RefreshArticleIconAsync()
    {
        if (_shuttingDown || !_webViewReady || ViewModel.SelectedArticle is not { } article
            || MainViewModel.Identity(article) != _renderedArticleIdentity) return;
        ulong navigation = _articleNavigationId;
        var source = MainViewModel.ArticleIconSource(article);
        try
        {
            await ContentWebView.ExecuteScriptAsync("if (window.__wnwNavigationId === "
                + JsonSerializer.Serialize(navigation.ToString(System.Globalization.CultureInfo.InvariantCulture))
                + ") { const icon = document.getElementById('nnwImageIcon'); const source = "
                + JsonSerializer.Serialize(source) + "; if (icon && icon.getAttribute('src') !== source) icon.src = source; }");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            if (!_shuttingDown && navigation == _articleNavigationId) App.LogException("Update article favicon", error);
        }
    }

    private static string PrepareArticleHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        if (html.IndexOf("<iframe", StringComparison.OrdinalIgnoreCase) < 0) return html;

        return IframeRegex.Replace(html, m =>
        {
            var src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (!Uri.TryCreate(src, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) return m.Value;
            var proxyUrl = IframeProxyBase + "?u=" + Uri.EscapeDataString(src);
            var safeProxy = System.Net.WebUtility.HtmlEncode(proxyUrl);
            var start = m.Groups[1].Index - m.Index;
            return m.Value[..start] + safeProxy + m.Value[(start + m.Groups[1].Length)..];
        });
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            var uri = new Uri(args.Request.Uri);
            if (uri.AbsolutePath == AppShared.ArticleRendering.ArticleImageResource.ResourcePath)
            {
                var deferral = args.GetDeferral();
                TrackBrowserTask(ServeArticleImageAsync(sender, args, uri, deferral, _articleNavigationId));
                return;
            }
            if (uri.AbsolutePath == "/article-icon")
            {
                var article = ViewModel.SelectedArticle;
                var key = uri.Query.TrimStart('?').Split('&').FirstOrDefault(value => value.StartsWith("article=", StringComparison.Ordinal));
                if (article is null || key is null || Uri.UnescapeDataString(key[8..]) != article.AccountID + "|" + article.Id)
                {
                    SetWebResource(sender, args, Array.Empty<byte>(), "text/plain", 404);
                    return;
                }
                if (article.FaviconPath is { } iconPath)
                {
                    if (Core.AppDefaults.Shared.GetGrayscaleIcons() && !iconPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        var deferral = args.GetDeferral();
                        TrackBrowserTask(ServeGrayscaleArticleIconAsync(sender, args, iconPath,
                            article.FaviconSource.Revision, deferral, _articleNavigationId));
                        return;
                    }
                    var contentType = System.IO.Path.GetExtension(iconPath).ToLowerInvariant() switch
                    {
                        ".svg" => "image/svg+xml", ".ico" => "image/x-icon", ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif", ".webp" => "image/webp", ".png" => "image/png", _ => "application/octet-stream"
                    };
                    SetWebResource(sender, args, System.IO.File.ReadAllBytes(iconPath), contentType);
                }
                else
                {
                    const string icon = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 48 48\"><rect width=\"48\" height=\"48\" rx=\"6\" fill=\"#888\"/><circle cx=\"12\" cy=\"36\" r=\"4\" fill=\"white\"/><path d=\"M8 22a18 18 0 0 1 18 18M8 10a30 30 0 0 1 30 30\" fill=\"none\" stroke=\"white\" stroke-width=\"5\"/></svg>";
                    SetWebResource(sender, args, System.Text.Encoding.UTF8.GetBytes(icon), "image/svg+xml");
                }
                return;
            }
            if (uri.AbsolutePath != "/iframe-proxy") return;
            string? origSrc = null;
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
            {
                if (part.StartsWith("u=", StringComparison.OrdinalIgnoreCase))
                {
                    origSrc = Uri.UnescapeDataString(part.Substring(2));
                    break;
                }
            }
            if (!Uri.TryCreate(origSrc, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
            {
                SetWebResource(sender, args, Array.Empty<byte>(), "text/plain", 400);
                return;
            }
            var safeSrc = System.Net.WebUtility.HtmlEncode(origSrc);
            var pageHtml =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
                + "html,body{margin:0;padding:0;height:100%;background:#000;overflow:hidden;}"
                + "iframe{width:100%;height:100%;border:0;display:block;}"
                + "</style></head><body><iframe src=\"" + safeSrc + "\""
                + " allow=\"accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share\""
                + " allowfullscreen></iframe></body></html>";
            var bytes = System.Text.Encoding.UTF8.GetBytes(pageHtml);
            SetWebResource(sender, args, bytes, "text/html; charset=utf-8");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or UriFormatException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceError($"Article resource could not be loaded: {ex}");
            if (!_shuttingDown) SetWebResource(sender, args, Array.Empty<byte>(), "text/plain", 500);
        }
    }

    private static void SetWebResource(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args, byte[] bytes, string contentType, int status = 200)
    {
        var stream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(new System.IO.MemoryStream(bytes, writable: false));
        args.Response = sender.Environment.CreateWebResourceResponse(
            stream, status, status == 200 ? "OK" : "Resource unavailable", "Content-Type: " + contentType + "\r\nCache-Control: no-store");
    }

    private FeedItem? _starWatchedArticle;

    private void UpdateStarButtonIcon()
    {
        bool starred = ViewModel.HasSelectedArticles && ViewModel.SelectedArticles.All(a => a.IsStarred);
        StarButtonIcon.Glyph = starred ? "\uE735" : "\uE734";
        // Keep tooltip + AutomationProperties.Name in sync with state so screen
        // readers announce the current action (not the original static label).
        var label = AppShared.AppStrings.Get(starred ? "ToolbarUnstar" : "Toolbar_markStar");
        SetToolbarActionLabel("markStar", label);
    }

    private void UpdateReaderModeButtonLabel()
    {
        bool on = ViewModel.IsReaderMode;
        SetToolbarActionLabel("readerView", AppShared.AppStrings.Get(on ? "ToolbarExitReaderView" : "Toolbar_readerView"));
    }

    private void SelectedArticle_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_shuttingDown) return;
        if (e.PropertyName == nameof(FeedItem.IsStarred))
            DispatcherQueue.TryEnqueue(() => { if (!_shuttingDown) UpdateStarButtonIcon(); });
        else if (e.PropertyName == nameof(FeedItem.FaviconSource))
            DispatcherQueue.TryEnqueue(() => { if (!_shuttingDown) TrackBrowserTask(RefreshArticleIconAsync()); });
    }

    private void RewatchSelectedArticleForStar()
    {
        if (_starWatchedArticle is { } prev)
            prev.PropertyChanged -= SelectedArticle_PropertyChanged;
        _starWatchedArticle = ViewModel.SelectedArticle;
        if (_starWatchedArticle is { } cur)
            cur.PropertyChanged += SelectedArticle_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_shuttingDown) return;
        if (e.PropertyName is nameof(ViewModel.UseColumnLayout) or nameof(ViewModel.IsUnifiedLayout)
            or nameof(ViewModel.SelectedSidebarItems) or nameof(ViewModel.SearchQuery) or nameof(ViewModel.SearchAllAccounts))
            QueueTimelineAppearanceUpdate();
        if (e.PropertyName is nameof(ViewModel.TimelineFeedIconsDisplay) or nameof(ViewModel.SelectedSidebarItems)
            or nameof(ViewModel.SearchQuery) or nameof(ViewModel.SearchAllAccounts)
            or nameof(ViewModel.UseColumnLayout) or nameof(ViewModel.IsUnifiedLayout))
            QueueTimelineFeedIconsUpdate();
        if (e.PropertyName == nameof(ViewModel.ArticleItems)) ObserveThumbnailItems();
        if (e.PropertyName is nameof(ViewModel.UseColumnLayout) or nameof(ViewModel.IsUnifiedLayout)
            or nameof(ViewModel.TimelineThumbnailsDisplay))
            QueueTimelineThumbnailRefresh();
        if (e.PropertyName is nameof(ViewModel.UseColumnLayout) or nameof(ViewModel.IsUnifiedLayout))
            LoadArticleHtml(ViewModel.ArticleHtml, forceReload: true);
        if (e.PropertyName == nameof(ViewModel.TimelineThumbnailsDisplay)
            && ViewModel.SelectedArticle is { } selected && !ViewModel.UseColumnLayout && !ViewModel.IsUnifiedLayout)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_shuttingDown && ReferenceEquals(ViewModel.SelectedArticle, selected)
                    && !ViewModel.UseColumnLayout && !ViewModel.IsUnifiedLayout)
                    ArticleListView.ScrollIntoView(selected);
            });
        if (e.PropertyName is nameof(ViewModel.SidebarStatus) or nameof(ViewModel.SelectedArticle)
            or nameof(ViewModel.HoveredLink) or nameof(ViewModel.IsSidebarVisible) or nameof(ViewModel.IsUnifiedLayout))
            QueueStatusBarUpdate();
        if (e.PropertyName is nameof(ViewModel.SelectedArticles) or nameof(ViewModel.SelectedArticle)
            or nameof(ViewModel.CanToggleSelectedRead) or nameof(ViewModel.IsReaderMode) or nameof(ViewModel.IsLoading)
            or nameof(ViewModel.IsReadFiltered) or nameof(ViewModel.CanToggleReadFilter)
            or nameof(ViewModel.IsSidebarVisible) or nameof(ViewModel.IsUnifiedLayout) or nameof(ViewModel.UseColumnLayout)
            or nameof(ViewModel.ArticleItems) or nameof(ViewModel.ArticleListUnreadCount) or nameof(ViewModel.SelectedSidebarItems))
        {
            if (DispatcherQueue.HasThreadAccess) UpdateToolbarState();
            QueueToolbarUpdate();
        }
        if (e.PropertyName == nameof(ViewModel.SelectedSidebarItems) && !_restoringLayout) SyncSidebarSelection();
        if (e.PropertyName == nameof(ViewModel.IsSidebarReadFiltered) && !_restoringLayout) BuildSidebarUI();
        if (e.PropertyName == nameof(ViewModel.SortParameters)) UpdateTimelineColumnHeaders();
        if (e.PropertyName == nameof(ViewModel.SelectedArticles))
        {
            DispatcherQueue.TryEnqueue(SyncArticleSelection);
            UpdateStarButtonIcon();
        }
        else if (e.PropertyName == nameof(ViewModel.SelectedArticle))
        {
            if (ViewModel.SelectedArticle is null) HideArticleFind();
            if (ViewModel.SelectedArticle is null) _ = WinNewsWire.AppRuntime.AppService.Shared.Speech.StopAsync(this);
            // Re-subscribe to the new article's IsStarred change so the toolbar
            // glyph updates whenever the property flips — relying on ArticleHtml
            // changing isn't enough because BuildArticleHtml produces the same
            // string when only the starred state changed, which the
            // [ObservableProperty]-generated setter then suppresses as a no-op.
            RewatchSelectedArticleForStar();
            DispatcherQueue.TryEnqueue(() => { if (!_shuttingDown) UpdateStarButtonIcon(); });
        }
        else if (e.PropertyName == nameof(ViewModel.ArticleHtml))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_shuttingDown) return;
                if (_webViewReady)
                {
                    LoadArticleHtml(ViewModel.ArticleHtml);
                }
                else
                {
                    EmptyContentState.Visibility = Visibility.Visible;
                    ContentWebView.Visibility = Visibility.Collapsed;
                }

                UpdateStarButtonIcon();
            });
        }
        else if (e.PropertyName == nameof(ViewModel.IsSidebarVisible))
        {
            if (_restoringLayout) return;
            // If the user toggled the sidebar manually (e.g. via the menu),
            // forget that we auto-collapsed it so we don't stomp their choice
            // the next time the width crosses the breakpoint.
            if (!_selfChangingSidebar) _autoCollapsedSidebar = false;
            DispatcherQueue.TryEnqueue(() => AnimateSidebar(ViewModel.IsSidebarVisible));
        }
        else if (e.PropertyName is nameof(ViewModel.IsUnifiedLayout) or nameof(ViewModel.UseColumnLayout))
        {
            if (_restoringLayout) return;
            DispatcherQueue.TryEnqueue(ApplyUnifiedLayout);
        }
        else if (e.PropertyName == nameof(ViewModel.IsReaderMode))
        {
            DispatcherQueue.TryEnqueue(() => { if (!_shuttingDown) UpdateReaderModeButtonLabel(); });
        }
    }

    private void ApplyUnifiedLayout()
    {
        ApplyMainLayout();
    }

    private double _lastArticleListWidth = 380;

    // --- Responsive collapse (sidebar first, then article list) ----------
    //
    // As the window narrows, hide the sidebar; if it gets narrower still, also
    // hide the article list, leaving the detail pane in focus. Restore on
    // grow, but only if the collapse was auto-triggered — never override an
    // explicit user toggle.
    private const double SidebarBreakpointPx = 1000;
    private const double ArticleListBreakpointPx = 700;
    private bool _autoCollapsedSidebar;
    private bool _autoCollapsedArticleList;
    private bool? _wasAboveSidebarBreakpoint;
    private bool? _wasAboveArticleListBreakpoint;
    private bool _selfChangingSidebar;

    private void MainContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        if (_shuttingDown || _restoringLayout || _applyingMainLayout || width <= 0) return;
        // Unified mode owns the column widths; don't fight it.
        if (ViewModel.IsUnifiedLayout) return;

        bool aboveSidebar = width >= SidebarBreakpointPx;
        if (_wasAboveSidebarBreakpoint != aboveSidebar)
        {
            if (!aboveSidebar && ViewModel.IsSidebarVisible)
            {
                _autoCollapsedSidebar = true;
                _selfChangingSidebar = true;
                ViewModel.IsSidebarVisible = false;
                _selfChangingSidebar = false;
            }
            else if (aboveSidebar && _autoCollapsedSidebar && !ViewModel.IsSidebarVisible)
            {
                _autoCollapsedSidebar = false;
                _selfChangingSidebar = true;
                ViewModel.IsSidebarVisible = true;
                _selfChangingSidebar = false;
            }
            _wasAboveSidebarBreakpoint = aboveSidebar;
        }

        if (ViewModel.UseColumnLayout) return;
        bool aboveList = width >= ArticleListBreakpointPx;
        if (_wasAboveArticleListBreakpoint != aboveList)
        {
            ReadingAreaColumn.MinWidth = aboveList ? 580 : ArticlePaneMinimumWidth;
            TimelinePanel.Visibility = ArticleSplitter.Visibility = aboveList ? Visibility.Visible : Visibility.Collapsed;
            ArticleDividerColumn.Width = aboveList ? GridLength.Auto : new GridLength(0);
            bool listVisibleNow = ArticleListColumn.Width.Value > 0;
            if (!aboveList && listVisibleNow)
            {
                _autoCollapsedArticleList = true;
                if (ArticleListColumn.Width.Value > 0)
                    _lastArticleListWidth = ArticleListColumn.Width.Value;
                ArticleListColumn.Width = new Microsoft.UI.Xaml.GridLength(0);
                ArticleListColumn.MinWidth = 0;
            }
            else if (aboveList && _autoCollapsedArticleList && !listVisibleNow)
            {
                _autoCollapsedArticleList = false;
                ArticleListColumn.Width = new Microsoft.UI.Xaml.GridLength(
                    _lastArticleListWidth > 0 ? _lastArticleListWidth : 380);
                ArticleListColumn.MinWidth = 280;
            }
            _wasAboveArticleListBreakpoint = aboveList;
        }
    }

    private IEnumerable<SidebarItem> SidebarContainers() => ViewModel.SidebarItems
        .SelectMany(section => new[] { section }.Concat(section.Children))
        .Where(item => item.IsExpandable);

    private static SidebarItemKey FilterIdentity(SidebarItemKey key)
        => key.Kind == SidebarItemType.Feed ? key with { ParentFolderID = null } : key;

    private void BuildSidebarUI(bool preserveVisible = false)
    {
        if (_shuttingDown) return;
        var selected = ViewModel.SelectedSidebarItems.Select(item => item.Identity).ToHashSet();
        foreach (var container in SidebarContainers())
        {
            var key = container.Identity!.Value;
            if (_knownSidebarContainers.Add(key) && container.IsExpanded
                || container.Children.Any(child => selected.Contains(child.Identity)
                    || child.Children.Any(descendant => selected.Contains(descendant.Identity))))
                _expandedSidebarContainers.Add(key);
            container.IsExpanded = _expandedSidebarContainers.Contains(key);
        }
        var exceptions = ViewModel.SelectedSidebarItems.Select(item => FilterIdentity(item.Identity!.Value)).ToHashSet();
        foreach (var parent in ViewModel.SelectedSidebarItems.Select(item => item.ParentFolder).OfType<AccountNs.Folder>())
            exceptions.Add(new(SidebarItemType.Folder, parent.AccountID,
                parent.FolderID.ToString(System.Globalization.CultureInfo.InvariantCulture), null));
        if (preserveVisible)
            foreach (var row in _sidebarRows)
                if (row.Identity is { } key) exceptions.Add(FilterIdentity(key));
        bool IsVisible(SidebarItem item) => !ViewModel.IsSidebarReadFiltered || item.Tag is not (AccountNs.Feed or AccountNs.Folder)
            || item.UnreadCount > 0 || item.Identity is { } key && exceptions.Contains(FilterIdentity(key));
        var rows = new List<SidebarItem>();
        foreach (var section in ViewModel.SidebarItems)
        {
            rows.Add(section);
            foreach (var child in section.Children.Where(IsVisible))
            {
                rows.Add(child);
                if (child.IsFolder && child.IsExpanded) rows.AddRange(child.Children.Where(IsVisible));
            }
        }
        var restored = rows.Where(item => item.Identity is not null && selected.Contains(item.Identity)).ToArray();
        var visibleSelection = restored.Where(item => item.IsSelectable).ToArray();
        if (_sidebarRows.SequenceEqual(rows) && SidebarListView.SelectedItems.OfType<SidebarItem>().SequenceEqual(visibleSelection)) return;
        var wasSynchronizing = _syncingSidebarSelection;
        _syncingSidebarSelection = true;
        try
        {
            // WinUI recreates unrelated containers for Move notifications; update only the changed range.
            int first = 0, oldEnd = _sidebarRows.Count, newEnd = rows.Count;
            while (first < oldEnd && first < newEnd && ReferenceEquals(_sidebarRows[first], rows[first])) first++;
            while (oldEnd > first && newEnd > first && ReferenceEquals(_sidebarRows[oldEnd - 1], rows[newEnd - 1]))
            {
                oldEnd--;
                newEnd--;
            }
            for (int index = oldEnd - 1; index >= first; index--) _sidebarRows.RemoveAt(index);
            for (int index = first; index < newEnd; index++) _sidebarRows.Insert(index, rows[index]);
            foreach (var item in SidebarListView.SelectedItems.OfType<SidebarItem>().ToArray())
                if (!visibleSelection.Contains(item)) SidebarListView.SelectedItems.Remove(item);
            foreach (var item in visibleSelection)
                if (!SidebarListView.SelectedItems.Contains(item)) SidebarListView.SelectedItems.Add(item);
            ViewModel.SetSelectedSidebarItems(restored);
        }
        finally { _syncingSidebarSelection = wasSynchronizing; }
    }

    private void SyncSidebarSelection()
    {
        if (_shuttingDown || _syncingSidebarSelection) return;
        var identities = ViewModel.SelectedSidebarItems.Select(item => item.Identity).ToHashSet();
        if (identities.Any(key => !_sidebarRows.Any(item => item.Identity == key)))
        {
            BuildSidebarUI(preserveVisible: true);
            return;
        }
        _syncingSidebarSelection = true;
        try
        {
            foreach (var item in SidebarListView.SelectedItems.OfType<SidebarItem>().ToArray())
                if (!item.IsSelectable || !identities.Contains(item.Identity)) SidebarListView.SelectedItems.Remove(item);
            foreach (var item in _sidebarRows.Where(item => item.IsSelectable && identities.Contains(item.Identity)))
                if (!SidebarListView.SelectedItems.Contains(item)) SidebarListView.SelectedItems.Add(item);
        }
        finally { _syncingSidebarSelection = false; }
    }

    private int _sidebarAnimationVersion;

    private void SetSidebarVisibility(bool show)
    {
        if (!show && SidebarColumn.Width.Value > 0) _lastSidebarWidth = SidebarColumn.Width.Value;
        SidebarColumn.MaxWidth = show ? 400 : 0;
        SidebarColumn.MinWidth = show ? 200 : 0;
        SidebarColumn.Width = new GridLength(show ? _lastSidebarWidth : 0);
        SidebarPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        QueueStatusBarUpdate();
        ContentContainer.Margin = new Thickness(show ? 0 : ContentPaneMargin, ContentPaneMargin, ContentPaneMargin, 0);
    }

    private void AnimateSidebar(bool show)
    {
        if (_shuttingDown || _restoringLayout || ViewModel.IsUnifiedLayout) return;
        int version = ++_sidebarAnimationVersion;
        var compositor = ElementCompositionPreview.GetElementVisual(SidebarPanel).Compositor;
        var sidebarVisual = ElementCompositionPreview.GetElementVisual(SidebarPanel);
        var splitterVisual = ElementCompositionPreview.GetElementVisual(SidebarSplitter);

        var duration = TimeSpan.FromMilliseconds(250);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        if (show)
        {
            // Restore column sizing first so layout has target width
            SetSidebarVisibility(true);

            // Slide + fade in
            var slideIn = compositor.CreateVector3KeyFrameAnimation();
            slideIn.InsertKeyFrame(0f, new Vector3(-60f, 0, 0), easing);
            slideIn.InsertKeyFrame(1f, Vector3.Zero, easing);
            slideIn.Duration = duration;

            var fadeIn = compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(0f, 0f, easing);
            fadeIn.InsertKeyFrame(1f, 1f, easing);
            fadeIn.Duration = duration;

            sidebarVisual.StartAnimation("Offset", slideIn);
            sidebarVisual.StartAnimation("Opacity", fadeIn);
            splitterVisual.StartAnimation("Opacity", fadeIn);
        }
        else
        {
            // Remember current width for restore
            if (SidebarColumn.Width.Value > 0) _lastSidebarWidth = SidebarColumn.Width.Value;

            // Slide + fade out, then collapse
            var slideOut = compositor.CreateVector3KeyFrameAnimation();
            slideOut.InsertKeyFrame(0f, Vector3.Zero, easing);
            slideOut.InsertKeyFrame(1f, new Vector3(-60f, 0, 0), easing);
            slideOut.Duration = duration;

            var fadeOut = compositor.CreateScalarKeyFrameAnimation();
            fadeOut.InsertKeyFrame(0f, 1f, easing);
            fadeOut.InsertKeyFrame(1f, 0f, easing);
            fadeOut.Duration = duration;

            // Use a scoped batch to collapse after animation finishes
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            sidebarVisual.StartAnimation("Offset", slideOut);
            sidebarVisual.StartAnimation("Opacity", fadeOut);
            splitterVisual.StartAnimation("Opacity", fadeOut);
            batch.End();

            batch.Completed += (s, a) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_shuttingDown || version != _sidebarAnimationVersion) return;
                    SetSidebarVisibility(false);
                    // Reset visual state so next show starts clean
                    sidebarVisual.Offset = Vector3.Zero;
                    sidebarVisual.Opacity = 1f;
                    splitterVisual.Opacity = 1f;
                });
            };
        }
    }

    // --- Sidebar splitter drag ---
    private void SidebarSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isSidebarDragging = true;
        _dragStartX = e.GetCurrentPoint(MainContentGrid).Position.X;
        _dragStartWidth = SidebarColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void SidebarSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSidebarDragging) return;
        var currentX = e.GetCurrentPoint(MainContentGrid).Position.X;
        var delta = currentX - _dragStartX;
        var newWidth = Math.Clamp(_dragStartWidth + delta, SidebarColumn.MinWidth, SidebarColumn.MaxWidth);
        SidebarColumn.Width = new GridLength(newWidth);
        e.Handled = true;
    }

    // --- Article splitter drag ---
    private void ArticleSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isArticleDragging = true;
        var point = e.GetCurrentPoint(ArticleContentGrid).Position;
        _dragStartX = ViewModel.UseColumnLayout ? point.Y : point.X;
        _dragStartWidth = ViewModel.UseColumnLayout ? ColumnTimelineRow.ActualHeight : ArticleListColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ArticleSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isArticleDragging) return;
        if (ViewModel.UseColumnLayout)
        {
            var deltaY = e.GetCurrentPoint(ArticleContentGrid).Position.Y - _dragStartX;
            _lastColumnTimelineHeight = Math.Clamp(_dragStartWidth + deltaY, 120, MaximumColumnTimelineHeight);
            ApplyColumnTimelineHeight();
            e.Handled = true;
            return;
        }
        var currentX = e.GetCurrentPoint(ArticleContentGrid).Position.X;
        var delta = currentX - _dragStartX;
        var newWidth = Math.Clamp(_dragStartWidth + delta, ArticleListColumn.MinWidth, ArticleListColumn.MaxWidth);
        ArticleListColumn.Width = new GridLength(newWidth);
        e.Handled = true;
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isSidebarDragging = false;
        _isArticleDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Splitter_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(ReferenceEquals(sender, ArticleSplitter) && ViewModel.UseColumnLayout
            ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
    }

    private void Splitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSidebarDragging && !_isArticleDragging)
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private const double KeyboardSplitterStepPx = 8;

    private void SidebarSplitter_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!ResizeColumnWithKey(SidebarColumn, e.Key)) return;
        e.Handled = true;
        _lastSidebarWidth = SidebarColumn.ActualWidth;
    }

    private void ArticleSplitter_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (ViewModel.UseColumnLayout)
        {
            bool shift = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            double step = shift ? 32 : 8;
            double? height = e.Key switch
            {
                Windows.System.VirtualKey.Up => ColumnTimelineRow.ActualHeight - step,
                Windows.System.VirtualKey.Down => ColumnTimelineRow.ActualHeight + step,
                Windows.System.VirtualKey.Home => 120,
                Windows.System.VirtualKey.End => MaximumColumnTimelineHeight,
                _ => null
            };
            if (height is not { } value) return;
            _lastColumnTimelineHeight = Math.Clamp(value, 120, MaximumColumnTimelineHeight);
            ApplyColumnTimelineHeight();
            e.Handled = true;
            return;
        }
        if (!ResizeColumnWithKey(ArticleListColumn, e.Key)) return;
        e.Handled = true;
        _lastArticleListWidth = ArticleListColumn.ActualWidth;
    }

    /// <summary>Keyboard-driven column resize. Left/Right move 8 px per press;
    /// Shift+Arrow moves 32 px for faster traversal; Home/End jump to min/max.
    /// Returns true if the keystroke was consumed.</summary>
    private static bool ResizeColumnWithKey(ColumnDefinition col, Windows.System.VirtualKey key)
    {
        var current = col.ActualWidth > 0 ? col.ActualWidth : col.Width.Value;
        double next = current;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        double step = shift ? KeyboardSplitterStepPx * 4 : KeyboardSplitterStepPx;

        switch (key)
        {
            case Windows.System.VirtualKey.Left: next = current - step; break;
            case Windows.System.VirtualKey.Right: next = current + step; break;
            case Windows.System.VirtualKey.Home: next = col.MinWidth; break;
            case Windows.System.VirtualKey.End: next = col.MaxWidth > 0 ? col.MaxWidth : current; break;
            default: return false;
        }

        var max = col.MaxWidth > 0 ? col.MaxWidth : double.MaxValue;
        next = Math.Clamp(next, col.MinWidth, max);
        col.Width = new GridLength(next);
        return true;
    }

    // --- Event handlers ---
    private void SidebarListView_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_shuttingDown || _syncingSidebarSelection) return;
        _syncingSidebarSelection = true;
        try
        {
            var selected = SidebarListView.SelectedItems.OfType<SidebarItem>().Select(item => item.Identity).ToHashSet();
            ViewModel.SetSelectedSidebarItems(_sidebarRows.Where(item => item.IsSelectable && selected.Contains(item.Identity)));
        }
        finally { _syncingSidebarSelection = false; }
    }

    private void SidebarListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            if (args.ItemContainer.ContentTemplateRoot is FrameworkElement root && _sidebarReveals.TryGetValue(root, out var stop))
                stop();
            args.ItemContainer.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty);
            return;
        }
        if (args.Item is not SidebarItem item) return;
        bool heading = item.ItemType == SidebarItemType.SectionHeader;
        if (heading) args.ItemContainer.Padding = new Thickness(10, 6, 0, 6);
        else args.ItemContainer.ClearValue(Control.PaddingProperty);
        var selectable = item.IsSelectable;
        args.ItemContainer.IsEnabled = selectable;
        args.ItemContainer.IsTabStop = selectable;
        args.ItemContainer.CanDrag = item.Tag is AccountNs.Feed or AccountNs.Folder;
        args.ItemContainer.AllowDrop = item.Tag is AccountNs.Account or AccountNs.Feed or AccountNs.Folder;
        args.ItemContainer.SetBinding(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty,
            new Microsoft.UI.Xaml.Data.Binding
            {
                Source = item,
                Path = new PropertyPath(nameof(SidebarItem.AccessibilityName)),
                Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay
            });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLevel(args.ItemContainer, item.OutlineLevel);
        if (FindDescendantByName(args.ItemContainer, "Root") is Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter presenter)
            presenter.SelectionIndicatorVisualEnabled = !heading;
        else if (args.Phase == 0)
            args.RegisterUpdateCallback(1, SidebarListView_ContainerContentChanging);
    }

    private void SidebarReadFilter_Changed(object sender, RoutedEventArgs args)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle)
            ViewModel.IsSidebarReadFiltered = toggle.IsChecked == true;
    }

    private void SidebarContainerExpansion_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: SidebarItem { IsExpandable: true } bound }
            && _sidebarRows.FirstOrDefault(item => item.Identity == bound.Identity) is { } item)
        {
            SetSidebarContainerExpanded(item, !item.IsExpanded);
        }
    }

    private void SetSidebarContainerExpanded(SidebarItem item, bool expanded)
    {
        if (!item.IsExpandable || item.Identity is not { } key || item.IsExpanded == expanded) return;
        StopSidebarReveals();
        if (expanded) _expandedSidebarContainers.Add(key);
        else _expandedSidebarContainers.Remove(key);
        item.IsExpanded = expanded;
        if (!expanded)
        {
            var descendants = item.Children.SelectMany(child => new[] { child }.Concat(child.Children))
                .Select(child => child.Identity).ToHashSet();
            if (ViewModel.SelectedSidebarItems.Any(selected => descendants.Contains(selected.Identity)))
                ViewModel.SetSelectedSidebarItems(ViewModel.SelectedSidebarItems
                    .Where(selected => !descendants.Contains(selected.Identity)).Append(item));
        }
        BuildSidebarUI();
        if (expanded && item.Tag is AccountNs.Folder folder) RevealFolderRows(folder);
    }

    private readonly Dictionary<FrameworkElement, Action> _sidebarReveals = [];

    private void StopSidebarReveals()
    {
        foreach (var stop in _sidebarReveals.Values.ToArray()) stop();
    }

    private void RevealFolderRows(AccountNs.Folder folder)
    {
        if (_shuttingDown || _restoringLayout || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        SidebarListView.UpdateLayout();
        if (SidebarListView.ItemsPanelRoot is not Panel panel) return;
        foreach (var container in panel.Children.OfType<ListViewItem>())
        {
            if (container.Content is not SidebarItem { ParentFolder: { } parent } || !ReferenceEquals(parent, folder)
                || container.ContentTemplateRoot is not FrameworkElement root) continue;
            var bounds = container.TransformToVisual(SidebarListView)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (bounds.Bottom <= 0 || bounds.Top >= SidebarListView.ActualHeight) continue;
            var originalTransform = root.RenderTransform;
            var transform = new TranslateTransform();
            root.RenderTransform = transform;
            var duration = new Duration(TimeSpan.FromMilliseconds(220));
            var fade = new DoubleAnimation { From = 0, To = root.Opacity, Duration = duration };
            var down = new DoubleAnimation { From = -12, To = 0, Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(fade, root);
            Storyboard.SetTargetProperty(fade, nameof(Opacity));
            Storyboard.SetTarget(down, transform);
            Storyboard.SetTargetProperty(down, nameof(TranslateTransform.Y));
            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(down);
            void Finish()
            {
                storyboard.Completed -= Completed;
                storyboard.Stop();
                root.RenderTransform = originalTransform;
                _sidebarReveals.Remove(root);
            }
            void Completed(object? sender, object args) => Finish();
            _sidebarReveals.Add(root, Finish);
            storyboard.Completed += Completed;
            storyboard.Begin();
        }
    }

    private async void SidebarListView_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_shuttingDown) return;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
        if (IsTextInputControl(focused) || !IsWithin(focused as DependencyObject, SidebarListView)) return;
        if (args.Key == Windows.System.VirtualKey.Delete && CanDeleteSidebarSelection)
        {
            args.Handled = true;
            await DeleteSelectedAsync();
            return;
        }
        if (ViewModel.SelectedSidebarItem is not { } selected) return;
        if (args.Key == Windows.System.VirtualKey.Left)
        {
            if (selected.IsExpandable && selected.IsExpanded) SetSidebarContainerExpanded(selected, false);
            else if (selected.ParentFolder is { } parent)
                ViewModel.SelectedSidebarItem = _sidebarRows.FirstOrDefault(item => ReferenceEquals(item.Tag, parent));
            else return;
            args.Handled = true;
        }
        else if (args.Key == Windows.System.VirtualKey.Right && selected.IsExpandable)
        {
            if (!selected.IsExpanded) SetSidebarContainerExpanded(selected, true);
            else if (_sidebarRows.Skip(_sidebarRows.IndexOf(selected) + 1).FirstOrDefault() is { } first
                && first.OutlineLevel == selected.OutlineLevel + 1) ViewModel.SelectedSidebarItem = first;
            args.Handled = true;
        }
    }

    private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ExecuteToolbarItemAsync("toggleSidebar");
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolbarItemAsync("refresh");
    }
    private async void NewFeed_Click(object sender, RoutedEventArgs e)
        => await NewFeedAsync();

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
        => await NewFolderAsync();

    private async void StarButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolbarItemAsync("markStar");
    }

    private async void MarkAllReadButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteToolbarItemAsync("markAllAsRead");
    }

    private async void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
        => await ExecuteToolbarItemAsync("openInBrowser");

    private async void ShareButton_Click(object sender, RoutedEventArgs e) => await ExecuteToolbarItemAsync("share");

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchQuery = args.QueryText ?? string.Empty;
    }

    // ---------- Context menus ----------
    //
    // The sidebar and timeline each have an empty MenuFlyout in
    // XAML; we rebuild its Items collection when the menu opens, mirroring NetNewsWire
    // Mac's SidebarViewController+ContextualMenus and TimelineViewController+ContextualMenus.

    private WinNewsWire.Models.SidebarItem? _sidebarRightClickedItem;
    private WinNewsWire.Models.FeedItem? _articleRightClickedItem;

    /// <summary>Walk the visual tree looking for a DataContext that matches <typeparamref name="T"/>.</summary>
    private static T? FindAncestorDataContext<T>(Microsoft.UI.Xaml.DependencyObject? element) where T : class
    {
        while (element is not null)
        {
            if (element is FrameworkElement fe)
            {
                if (fe.DataContext is T t) return t;
            }
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void SidebarListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // RightTapped's OriginalSource is the original visual under the pointer and
        // does not change as the event bubbles, so capturing here always sees the
        // deepest element. We unconditionally write the result (null on whitespace)
        // so a previous click's target can't leak into the next menu open.
        _sidebarRightClickedItem = FindAncestorDataContext<WinNewsWire.Models.SidebarItem>(
            e.OriginalSource as Microsoft.UI.Xaml.DependencyObject);
    }

    private void ArticleListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _articleRightClickedItem = FindAncestorDataContext<WinNewsWire.Models.FeedItem>(
            e.OriginalSource as Microsoft.UI.Xaml.DependencyObject);
    }

    private async void ArticleListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_shuttingDown || FindAncestorDataContext<WinNewsWire.Models.FeedItem>(e.OriginalSource as DependencyObject) is not { } article
            || !ViewModel.ArticleItems.Contains(article) || article.Link.Length == 0) return;
        bool invertPreference = (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        e.Handled = true;
        await OpenLinkInBrowserAsync(article.Link, invertPreference);
    }

    private void SidebarContextMenu_Closed(object? sender, object e)
    {
        // Clear the captured target after the flyout closes so the next right-click
        // starts from a clean slate. Without this, right-clicking whitespace would
        // inherit the previously captured row (RightTapped doesn't fire on TreeView
        // whitespace in WinUI 3).
        _sidebarRightClickedItem = null;
    }

    private void ArticleContextMenu_Closed(object? sender, object e)
    {
        _articleRightClickedItem = null;
    }

    private static MenuFlyoutItem MakeMenuItem(string text, string? glyph, RoutedEventHandler click, object? tag = null,
        bool isEnabled = true, IconElement? icon = null)
    {
        var mi = new MenuFlyoutItem { Text = text, IsEnabled = isEnabled, Tag = tag };
        if (icon is not null) mi.Icon = icon;
        else if (!string.IsNullOrEmpty(glyph))
            mi.Icon = new FontIcon { Glyph = glyph };
        mi.Click += click;
        return mi;
    }

    // ===== Sidebar context menu =====

    private void SidebarContextMenu_Opening(object sender, object e)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();

        // Match NetNewsWire Mac: the menu reflects the row the user actually
        // right-clicked. If they right-clicked empty whitespace (no row), we
        // show the no-selection menu (New Feed / New Folder), regardless of
        // which sidebar item happens to be selected.
        var item = _sidebarRightClickedItem;
        PopulateSidebarSelectionMenu(menu, item);
    }

    private void PopulateSidebarSelectionMenu(MenuFlyout menu, SidebarItem? item)
    {
        if (item is null)
        {
            BuildSidebarMenuForNoSelection(menu);
            return;
        }
        if (ViewModel.HasMultipleSidebarSelection && ViewModel.SelectedSidebarItems.Any(selected => selected.Identity == item.Identity))
        {
            var selection = ViewModel.SelectedSidebarItems.ToArray();
            menu.Items.Add(MakeMenuItem("Mark All as Read", "\uE73E", async (_, _) => await ViewModel.MarkSidebarItemsReadAsync(selection)));
            menu.Items.Add(MakeMenuItem("Refresh", "\uE72C", async (_, _) => await RefreshSidebarItemsAsync(selection)));
            if (selection.All(selected => selected.Tag is AccountNs.Feed))
                menu.Items.Add(MakeMenuItem("Copy Feed URLs", "\uE8C8", (_, _) =>
                    CopyToClipboard(string.Join(Environment.NewLine, selection.Select(selected => ((AccountNs.Feed)selected.Tag!).Url)))));
            if (CanDeleteSidebarItems(selection))
                menu.Items.Add(MakeMenuItem("Delete", "\uE74D", async (_, _) => await DeleteSidebarItemsAsync(selection)));
            return;
        }

        switch (item.Tag)
        {
            case WinNewsWire.Account.Feed feed:
                BuildSidebarMenuForFeed(menu, item, feed);
                break;
            case WinNewsWire.Account.Folder folder:
                BuildSidebarMenuForFolder(menu, item, folder);
                break;
            case WinNewsWire.Account.Account account:
                BuildSidebarMenuForAccount(menu, item, account);
                break;
            case WinNewsWire.AppShared.SmartFeeds.IPseudoFeed:
                BuildSidebarMenuForSmartFeed(menu, item);
                break;
            default:
                BuildSidebarMenuForNoSelection(menu);
                break;
        }
    }

    private void BuildSidebarMenuForNoSelection(MenuFlyout menu)
    {
        menu.Items.Add(MakeMenuItem("New Feed\u2026", "\uE774", NewFeed_Click));
        menu.Items.Add(MakeMenuItem("New Folder\u2026", "\uE8B7", NewFolder_Click));
    }

    private void BuildSidebarMenuForFeed(MenuFlyout menu, WinNewsWire.Models.SidebarItem item, WinNewsWire.Account.Feed feed)
    {
        if (feed.UnreadCount > 0)
        {
            menu.Items.Add(MakeMenuItem("Mark All as Read", "\uE73E", CtxMarkAllRead_Click, item));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (!string.IsNullOrEmpty(feed.HomePageUrl) && Uri.TryCreate(feed.HomePageUrl, UriKind.Absolute, out _))
        {
            menu.Items.Add(MakeMenuItem("Open Home Page", "\uE774", CtxOpenHomePage_Click, item));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(MakeMenuItem("Copy Feed URL", "\uE8C8", CtxCopyFeedUrl_Click, item));
        if (!string.IsNullOrEmpty(feed.HomePageUrl))
        {
            menu.Items.Add(MakeMenuItem("Copy Home Page URL", "\uE8C8", CtxCopyHomePageUrl_Click, item));
        }
        menu.Items.Add(new MenuFlyoutSeparator());

        // Per-feed Notifications + Always Use Reader View — checkmark state mirrors
        // NetNewsWire's NSMenuItem.state. ToggleMenuFlyoutItem gives WinUI's native
        // check visual; we still set Tag so the existing dispatcher pattern works.
        var notifyToggle = new ToggleMenuFlyoutItem
        {
            Text = feed.NotificationDisplayName,
            IsChecked = feed.NewArticleNotificationsEnabled,
            Tag = item,
        };
        notifyToggle.Icon = new FontIcon { Glyph = "\uEA8F" };
        notifyToggle.Click += CtxToggleNotifications_Click;
        menu.Items.Add(notifyToggle);

        var readerToggle = new ToggleMenuFlyoutItem
        {
            Text = "Always Use Reader View",
            IsChecked = feed.ReaderViewAlwaysEnabled,
            Tag = item,
        };
        readerToggle.Icon = new FontIcon { Glyph = "\uE7BC" };
        readerToggle.Click += CtxToggleReaderView_Click;
        menu.Items.Add(readerToggle);

        menu.Items.Add(new MenuFlyoutSeparator());

        // Refresh and Get Info are WinNewsWire-specific extensions that have no analog
        // contextual command on Mac NNW (Refresh All is keyboard-only there) but match
        // the existing toolbar functionality the user already expects.
        menu.Items.Add(MakeMenuItem("Refresh", "\uE72C", CtxRefresh_Click, item));
        menu.Items.Add(MakeMenuItem("Rename\u2026", "\uE8AC", CtxRename_Click, item));
        menu.Items.Add(MakeMenuItem("Delete", "\uE74D", CtxDelete_Click, item));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeMenuItem("Get Info\u2026", "\uE946", CtxGetInfo_Click, item));
    }

    private void BuildSidebarMenuForFolder(MenuFlyout menu, WinNewsWire.Models.SidebarItem item, WinNewsWire.Account.Folder folder)
    {
        var unread = folder.Feeds.Sum(f => f.UnreadCount);
        if (unread > 0)
        {
            menu.Items.Add(MakeMenuItem("Mark All as Read", "\uE73E", CtxMarkAllRead_Click, item));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        menu.Items.Add(MakeMenuItem("Refresh", "\uE72C", CtxRefresh_Click, item));
        menu.Items.Add(MakeMenuItem("Rename\u2026", "\uE8AC", CtxRename_Click, item));
        menu.Items.Add(MakeMenuItem("Delete", "\uE74D", CtxDelete_Click, item));
    }

    private void BuildSidebarMenuForAccount(MenuFlyout menu, WinNewsWire.Models.SidebarItem item, WinNewsWire.Account.Account account)
    {
        if (item.UnreadCount > 0)
            menu.Items.Add(MakeMenuItem("Mark All as Read", "\uE73E", CtxMarkAllRead_Click, item));
        menu.Items.Add(MakeMenuItem("Refresh", "\uE72C", CtxRefresh_Click, item));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeMenuItem("New Feed\u2026", "\uE774", NewFeed_Click));
        menu.Items.Add(MakeMenuItem("New Folder\u2026", "\uE8B7", NewFolder_Click));
    }

    private void BuildSidebarMenuForSmartFeed(MenuFlyout menu, WinNewsWire.Models.SidebarItem item)
    {
        if (item.UnreadCount > 0)
            menu.Items.Add(MakeMenuItem("Mark All as Read", "\uE73E", CtxMarkAllRead_Click, item));
    }

    // Helpers shared between toolbar/menu wiring. Toolbar paths still call into the
    // existing handlers above; the contextual paths supply the target via Tag.
    private static WinNewsWire.Models.SidebarItem? TargetSidebarItem(object? sender, WinNewsWire.Models.SidebarItem? fallback)
        => (sender as FrameworkElement)?.Tag as WinNewsWire.Models.SidebarItem ?? fallback;

    private async void CtxMarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item is null) return;
        await ViewModel.MarkSidebarItemsReadAsync([item]);
    }

    private async void CtxRefresh_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item is not null) await RefreshSidebarItemsAsync([item]);
        else await ViewModel.ReloadFeedsCommand.ExecuteAsync(null);
    }

    private Task RefreshSidebarItemsAsync(IReadOnlyList<SidebarItem> items)
        => ViewModel.RunSidebarActionAsync("Refresh feeds", async () =>
        {
            var accounts = items.Any(item => item.Tag is WinNewsWire.AppShared.SmartFeeds.IPseudoFeed)
                ? AppRuntime.AppService.Shared.Accounts.ActiveAccounts
                : items.Select(AccountForSidebarItem).OfType<AccountNs.Account>().Distinct();
            await Task.WhenAll(accounts.Select(account => account.RefreshAllAsync()));
        });

    private void CtxRename_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item is null) return;
        // Inline-rename for the right-clicked item — same code path as the
        // menu bar's Feed > Rename… so behavior is consistent.
        BeginInlineRenameForItem(item);
    }

    private async void CtxDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem) is { } item)
            await DeleteSidebarItemsAsync([item]);
    }

    private bool _sidebarDeleteInProgress;

    public bool CanDeleteSidebarSelection => CanDeleteSidebarItems(ViewModel.SelectedSidebarItems);

    private static bool CanDeleteSidebarItems(IReadOnlyList<SidebarItem> items)
        => items.Count > 0 && items.All(item => (item.Tag is AccountNs.Feed or AccountNs.Folder)
            && AccountForSidebarItem(item) is { IsActive: true } account
            && (item.Tag is not AccountNs.Folder || !account.Type.Has(AccountNs.AccountBehavior.DisallowFolderManagement)));

    private async Task DeleteSidebarItemsAsync(IReadOnlyList<SidebarItem> items)
    {
        if (_shuttingDown || _sidebarDeleteInProgress || !CanDeleteSidebarItems(items)) return;
        _sidebarDeleteInProgress = true;
        try
        {
            var requested = items.Select(item => item.Identity).ToHashSet();
            var confirm = new ContentDialog
            {
                Title = "Delete?",
                Content = items.Count == 1
                    ? $"Are you sure you want to delete \u201C{items[0].Title}\u201D? You can undo this change."
                    : $"Delete the {items.Count} selected feeds and folders? You can undo this change.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            AppWindows.WindowThemeHelper.Apply(confirm);
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            var visible = _sidebarRows.ToArray();
            var liveItems = visible.Where(item => requested.Contains(item.Identity)).ToArray();
            if (!CanDeleteSidebarItems(liveItems))
            {
                ViewModel.StatusMessage = AppStrings.Get("SidebarDeleteUnavailable");
                return;
            }
            var folders = liveItems.Select(item => item.Tag).OfType<AccountNs.Folder>()
                .Select(folder => (folder.AccountID, folder.FolderID)).ToHashSet();
            bool IsDeleted(SidebarItem item) => requested.Contains(item.Identity)
                || item.ParentFolder is { } parent && folders.Contains((parent.AccountID, parent.FolderID));
            bool selectionDeleted = ViewModel.SelectedSidebarItems.Any(IsDeleted);
            SidebarItemKey? nextIdentity = null;
            if (selectionDeleted)
            {
                var indices = Enumerable.Range(0, visible.Length).Where(index => liveItems.Contains(visible[index])).ToArray();
                for (int index = indices[^1] + 1; index < visible.Length; index++)
                {
                    if (IsDeleted(visible[index])) continue;
                    if (visible[index].IsSelectable) nextIdentity = visible[index].Identity;
                    break;
                }
                if (nextIdentity is null && indices[0] > 0 && visible[indices[0] - 1].IsSelectable)
                    nextIdentity = visible[indices[0] - 1].Identity;
            }
            bool completed = false;
            await ViewModel.RunSidebarActionAsync("Delete", async () =>
            {
                var nodes = liveItems.Select(item => new WinNewsWire.Tree.Node(item.Tag!,
                    item.ParentFolder is { } folder ? new WinNewsWire.Tree.Node(folder) : null));
                var command = DeleteCommand.Create(nodes) ?? throw new InvalidOperationException("The selected subscriptions are no longer available.");
                await ViewModel.UndoManager.ExecuteAsync(command);
                completed = true;
            });
            if (completed && !_shuttingDown && selectionDeleted && ViewModel.SelectedSidebarItems.Count == 0
                && nextIdentity is { } key && _sidebarRows.FirstOrDefault(item => item.Identity == key) is { } next)
            {
                ViewModel.SetSelectedSidebarItems([next]);
                SidebarListView.ScrollIntoView(next);
            }
        }
        finally { _sidebarDeleteInProgress = false; }
    }

    private async void CtxOpenHomePage_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item?.Tag is WinNewsWire.Account.Feed feed && !string.IsNullOrEmpty(feed.HomePageUrl))
            await OpenLinkInBrowserAsync(feed.HomePageUrl);
    }

    private void CtxCopyFeedUrl_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item?.Tag is WinNewsWire.Account.Feed feed)
            CopyToClipboard(feed.Url);
    }

    private void CtxCopyHomePageUrl_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item?.Tag is WinNewsWire.Account.Feed feed && !string.IsNullOrEmpty(feed.HomePageUrl))
            CopyToClipboard(feed.HomePageUrl);
    }

    private void CtxToggleNotifications_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item?.Tag is not WinNewsWire.Account.Feed feed) return;
        feed.NewArticleNotificationsEnabled = (sender as ToggleMenuFlyoutItem)?.IsChecked
            ?? !feed.NewArticleNotificationsEnabled;
        WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == feed.AccountID)?.SaveChanges();
    }

    private void CtxToggleReaderView_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item?.Tag is not WinNewsWire.Account.Feed feed) return;
        feed.ReaderViewAlwaysEnabled = (sender as ToggleMenuFlyoutItem)?.IsChecked
            ?? !feed.ReaderViewAlwaysEnabled;
        WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == feed.AccountID)?.SaveChanges();

        // If the user enabled "always" while this feed's article is on screen,
        // flip reader mode on immediately so the change is visible without
        // navigating away and back.
        if (feed.ReaderViewAlwaysEnabled
            && ViewModel.SelectedArticle is { } article
            && article.AccountID == feed.AccountID
            && article.FeedId == feed.FeedID
            && !ViewModel.IsReaderMode)
        {
            ViewModel.IsReaderMode = true;
        }
    }

    private async void CtxGetInfo_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        await ShowInspectorForAsync(item);
    }

    // ===== Article (timeline) context menu =====

    private void ArticleContextMenu_Opening(object sender, object e)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();

        // Match Mac NNW: clicking empty whitespace shows no menu at all. Otherwise
        // if the right-clicked row is part of the selection, act on the selection;
        // else act on the right-clicked row alone.
        var clicked = _articleRightClickedItem;
        if (clicked is null) return;

        var articles = ArticleSelection.ForContextMenu(ViewModel.ArticleItems.ToList(),
            ViewModel.SelectedArticles, clicked, MainViewModel.Identity);

        var anyUnread = articles.Any(a => !a.IsRead);
        var anyRead = articles.Any(a => a.IsRead);
        var anyUnstarred = articles.Any(a => !a.IsStarred);
        var anyStarred = articles.Any(a => a.IsStarred);

        if (anyUnread)
            menu.Items.Add(MakeMenuItem("Mark as Read", null, CtxMarkRead_Click, articles, icon: ReadStatusMenuIcon()));
        if (anyRead)
            menu.Items.Add(MakeMenuItem("Mark as Unread", null, CtxMarkUnread_Click, articles, icon: ReadStatusMenuIcon(markUnread: true)));
        if (anyUnstarred)
            menu.Items.Add(MakeMenuItem("Mark as Starred", "\uE734", CtxMarkStarred_Click, articles));
        if (anyStarred)
            menu.Items.Add(MakeMenuItem("Mark as Unstarred", "\uE735", CtxMarkUnstarred_Click, articles));

        var first = articles.First();
        var last = articles.Last();
        var firstIdx = ViewModel.ArticleItems.IndexOf(first);
        var lastIdx = ViewModel.ArticleItems.IndexOf(last);

        if (firstIdx > 0 && ViewModel.ArticleItems.Take(firstIdx).Any(a => !a.IsRead))
            menu.Items.Add(MakeMenuItem("Mark Above as Read", null, CtxMarkAboveRead_Click, articles, icon: ReadStatusMenuIcon()));
        if (lastIdx >= 0 && lastIdx < ViewModel.ArticleItems.Count - 1
            && ViewModel.ArticleItems.Skip(lastIdx + 1).Any(a => !a.IsRead))
            menu.Items.Add(MakeMenuItem("Mark Below as Read", null, CtxMarkBelowRead_Click, articles, icon: ReadStatusMenuIcon()));
        if (ViewModel.ArticleItems.Any(a => !a.IsRead && a.PublishedDate < articles.Min(selected => selected.PublishedDate)))
            menu.Items.Add(MakeMenuItem("Mark Older as Read", null, CtxMarkOlderRead_Click, articles, icon: ReadStatusMenuIcon()));

        // Single-article-only items
        if (articles.Count == 1)
        {
            var a = articles[0];
            var feed = WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
                .FirstOrDefault(x => x.AccountID == a.AccountID)?
                .FlattenedFeeds().FirstOrDefault(f => f.FeedID == a.FeedId);
            if (feed is not null)
            {
                AppendSeparatorIfNeeded(menu);
                // Only offer "Select Feed in Sidebar" when the timeline isn't already
                // scoped to that feed.
                var current = ViewModel.SelectedSidebarItem?.Tag as WinNewsWire.Account.Feed;
                if (current is null || current.AccountID != a.AccountID || current.FeedID != a.FeedId)
                {
                    var label = $"Select \u201C{feed.NameForDisplay}\u201D in Sidebar";
                    menu.Items.Add(MakeMenuItem(label, null, CtxSelectFeedInSidebar_Click, a));
                }
                if (feed.UnreadCount > 0)
                {
                    var label = $"Mark All as Read in \u201C{feed.NameForDisplay}\u201D";
                    menu.Items.Add(MakeMenuItem(label, "\uE73E", CtxMarkAllInFeedAsRead_Click, a));
                }
            }

            if (!string.IsNullOrEmpty(a.Link))
            {
                AppendSeparatorIfNeeded(menu);
                menu.Items.Add(MakeMenuItem("Open in Browser", "\uE774", CtxArticleOpenBrowser_Click, a));
                menu.Items.Add(MakeMenuItem(Browser.InvertedOpenTitle, "\uE774", async (_, _) => await OpenLinkInBrowserAsync(a.Link, invertPreference: true), a));
                AppendSeparatorIfNeeded(menu);
                menu.Items.Add(MakeMenuItem("Copy Article URL", "\uE8C8", CtxArticleCopyLink_Click, a));
                if (!string.IsNullOrEmpty(a.ExternalLink) && a.ExternalLink != a.Link)
                    menu.Items.Add(MakeMenuItem("Copy External URL", "\uE8C8", CtxArticleCopyExternalLink_Click, a));
            }
            menu.Items.Add(MakeMenuItem("Copy Title", null, CtxArticleCopyTitle_Click, a));
            menu.Items.Add(MakeMenuItem("Copy Article (as HTML)", "\uE8C8", CtxArticleCopyRich_Click, a));
        }

        AppendSeparatorIfNeeded(menu);
        menu.Items.Add(MakeMenuItem("Share\u2026", "\uE72D", CtxArticleShare_Click, articles));
    }

    private static void AppendSeparatorIfNeeded(MenuFlyout menu)
    {
        if (menu.Items.Count == 0) return;
        if (menu.Items[menu.Items.Count - 1] is MenuFlyoutSeparator) return;
        menu.Items.Add(new MenuFlyoutSeparator());
    }

    private static List<WinNewsWire.Models.FeedItem> ArticlesFrom(object sender, WinNewsWire.Models.FeedItem? fallback)
    {
        if ((sender as MenuFlyoutItem)?.Tag is List<WinNewsWire.Models.FeedItem> list) return list;
        if ((sender as MenuFlyoutItem)?.Tag is WinNewsWire.Models.FeedItem one) return new() { one };
        return fallback is null ? new() : new() { fallback };
    }

    private static WinNewsWire.Models.FeedItem? ArticleFrom(object sender, WinNewsWire.Models.FeedItem? fallback)
    {
        if ((sender as MenuFlyoutItem)?.Tag is WinNewsWire.Models.FeedItem one) return one;
        if ((sender as MenuFlyoutItem)?.Tag is List<WinNewsWire.Models.FeedItem> list && list.Count > 0) return list[0];
        return fallback;
    }

    private async void CtxMarkRead_Click(object sender, RoutedEventArgs e)
        => await ViewModel.MarkArticlesAsync(ArticlesFrom(sender, ViewModel.SelectedArticle), WinNewsWire.Articles.ArticleStatus.Key.Read, true);

    private async void CtxMarkUnread_Click(object sender, RoutedEventArgs e)
        => await ViewModel.MarkArticlesAsync(ArticlesFrom(sender, ViewModel.SelectedArticle), WinNewsWire.Articles.ArticleStatus.Key.Read, false);

    private async void CtxMarkStarred_Click(object sender, RoutedEventArgs e)
        => await ViewModel.MarkArticlesAsync(ArticlesFrom(sender, ViewModel.SelectedArticle), WinNewsWire.Articles.ArticleStatus.Key.Starred, true);

    private async void CtxMarkUnstarred_Click(object sender, RoutedEventArgs e)
        => await ViewModel.MarkArticlesAsync(ArticlesFrom(sender, ViewModel.SelectedArticle), WinNewsWire.Articles.ArticleStatus.Key.Starred, false);

    private async void CtxMarkAboveRead_Click(object sender, RoutedEventArgs e)
    {
        var first = ArticlesFrom(sender, ViewModel.SelectedArticle).FirstOrDefault();
        if (first is not null) await ViewModel.MarkAboveAsReadAsync(first);
    }

    private async void CtxMarkBelowRead_Click(object sender, RoutedEventArgs e)
    {
        var last = ArticlesFrom(sender, ViewModel.SelectedArticle).LastOrDefault();
        if (last is not null) await ViewModel.MarkBelowAsReadAsync(last);
    }

    private async void CtxMarkOlderRead_Click(object sender, RoutedEventArgs e)
    {
        var articles = ArticlesFrom(sender, ViewModel.SelectedArticle);
        if (articles.Count == 0) return;
        var cutoff = articles.Min(a => a.PublishedDate);
        await ViewModel.MarkArticlesAsync(ViewModel.ArticleItems.Where(a => a.PublishedDate < cutoff).ToList(),
            WinNewsWire.Articles.ArticleStatus.Key.Read, true);
    }

    private void CtxSelectFeedInSidebar_Click(object sender, RoutedEventArgs e)
    {
        var a = ArticleFrom(sender, ViewModel.SelectedArticle);
        if (a is null) return;
        var sidebarItem = ViewModel.FindSidebarItemForFeed(a.AccountID, a.FeedId);
        if (sidebarItem is not null)
        {
            ViewModel.SelectedSidebarItem = sidebarItem;
            // Re-sync the outline's selected row to match.
            BuildSidebarUI();
        }
    }

    private async void CtxMarkAllInFeedAsRead_Click(object sender, RoutedEventArgs e)
    {
        var a = ArticleFrom(sender, ViewModel.SelectedArticle);
        if (a is null) return;
        await ViewModel.MarkAllInFeedAsReadAsync(a.AccountID, a.FeedId);
    }

    // Toggle Read/Starred remain bound to keyboard accelerators / toolbar buttons; they
    // continue to operate on the current SelectedArticle.
    private async void CtxToggleRead_Click(object sender, RoutedEventArgs e)
        => await ViewModel.MarkSelectedReadCommand.ExecuteAsync(null);

    private async void CtxToggleStarred_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ToggleStarredCommand.ExecuteAsync(null);

    private async void CtxArticleOpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        var a = ArticleFrom(sender, ViewModel.SelectedArticle);
        if (a is { Link: { Length: > 0 } link })
            await OpenLinkInBrowserAsync(link);
    }

    private void CtxArticleCopyLink_Click(object sender, RoutedEventArgs e)
    {
        var a = ArticleFrom(sender, ViewModel.SelectedArticle);
        if (a is { Link: { Length: > 0 } link }) CopyToClipboard(link);
    }

    private void CtxArticleCopyExternalLink_Click(object sender, RoutedEventArgs e)
    {
        var a = ArticleFrom(sender, ViewModel.SelectedArticle);
        if (a is { ExternalLink: { Length: > 0 } link }) CopyToClipboard(link);
    }

    private void CtxArticleCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        var a = ArticleFrom(sender, ViewModel.SelectedArticle);
        if (a is { Title: { Length: > 0 } title }) CopyToClipboard(title);
    }

    private void CtxArticleCopyRich_Click(object sender, RoutedEventArgs e)
    {
        var a = ArticleFrom(sender, ViewModel.SelectedArticle);
        if (a is null) return;

        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        var link = a.Link ?? string.Empty;
        var title = a.Title ?? string.Empty;
        var plain = string.IsNullOrEmpty(link) ? title : $"{title}\r\n{link}";
        pkg.SetText(plain);

        var safeTitle = System.Net.WebUtility.HtmlEncode(title);
        var safeLink = System.Net.WebUtility.HtmlEncode(link);
        var body = a.Content ?? a.Summary ?? string.Empty;
        var html = $"<html><body><h1><a href=\"{safeLink}\">{safeTitle}</a></h1>{body}</body></html>";
        pkg.SetHtmlFormat(Windows.ApplicationModel.DataTransfer.HtmlFormatHelper.CreateHtmlFormat(html));

        if (Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            pkg.SetWebLink(uri);
            pkg.SetApplicationLink(uri);
        }
        pkg.Properties.Title = title;

        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
    }

    private void CtxArticleShare_Click(object sender, RoutedEventArgs e)
        => ShareArticles(ArticlesFrom(sender, ViewModel.SelectedArticle));

    private void ShareSelectedArticle()
    {
        if (DisplayedWebPageUri is { } uri)
        {
            string title = _pendingWebPagePresentation is null ? WebPageView.CoreWebView2.DocumentTitle : uri.AbsoluteUri;
            ShowShareUI(data =>
            {
                data.Properties.Title = string.IsNullOrWhiteSpace(title) ? uri.AbsoluteUri : title;
                data.SetWebLink(uri);
                data.SetText(uri.AbsoluteUri);
            });
        }
        else ShareArticles(ViewModel.SelectedArticles);
    }

    private void ShareArticles(IReadOnlyList<FeedItem> articles)
    {
        if (articles.Count == 0) return;
        var models = articles.Select(a => a.SourceArticle).OfType<WinNewsWire.Articles.Article>().ToList();
        ShowShareUI(data =>
        {
            var subject = ArticleSharing.Subject(models);
            data.Properties.Title = subject.Length > 0 ? subject : "Articles";
            data.SetText(ArticleSharing.PlainText(models));
            data.SetHtmlFormat(Windows.ApplicationModel.DataTransfer.HtmlFormatHelper.CreateHtmlFormat(ArticleSharing.Html(models)));
            if (articles.Count == 1 && Uri.TryCreate(articles[0].Link, UriKind.Absolute, out var uri))
                data.SetWebLink(uri);
        });
    }

    private void ShowShareUI(Action<Windows.ApplicationModel.DataTransfer.DataPackage> populate)
    {
#if PARITY_PROBE
        if (ProbeShareWriter is { } writer)
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            populate(data);
            writer(data);
            return;
        }
#endif
        if (App.WindowForXamlRoot(XamlRoot) is not { } window) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var dtm = Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop
                .GetForWindow(hwnd);

            if (_sharingManager is not null && _shareRequestHandler is not null)
                _sharingManager.DataRequested -= _shareRequestHandler;
            _sharingManager = dtm;
            _shareRequestHandler = (s, args) =>
            {
                populate(args.Request.Data);
                if (_shareRequestHandler is not null) dtm.DataRequested -= _shareRequestHandler;
            };
            dtm.DataRequested += _shareRequestHandler;

            Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop
                .ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ViewModel.StatusMessage = $"Could not share articles: {ex.Message}";
        }
    }

    private void CopyToClipboard(string text)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
#if PARITY_PROBE
            if (ProbeClipboardWriter is { } writer)
            {
                writer(pkg);
                return;
            }
#endif
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            ViewModel.StatusMessage = $"Could not copy to the clipboard: {error.Message}";
            App.LogException("Copy to clipboard", error);
        }
    }

    // ---------- Keyboard accelerators ----------

    private void AccelNext_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { SelectNext(); e.Handled = true; }

    private void AccelPrev_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { SelectPrev(); e.Handled = true; }

    private async void AccelNextUnread_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (IsWithin(focused, ContentWebView)) return;
        e.Handled = true;
        await SelectNextUnreadAsync();
    }

    private async void AccelRefresh_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ViewModel.ReloadFeedsCommand.ExecuteAsync(null); }

    private async void AccelMarkRead_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ViewModel.MarkSelectedReadCommand.ExecuteAsync(null); }

    private async void AccelToggleStar_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ViewModel.ToggleStarredCommand.ExecuteAsync(null); }

    private async void AccelMarkAllRead_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ViewModel.MarkAllReadCommand.ExecuteAsync(null); ViewModel.BuildSidebar(); BuildSidebarUI(); }

    private async void AccelInspector_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ShowInspectorAsync(); }

    private async void AccelNewFeed_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await NewFeedAsync(); }

    private void AccelToggleSidebar_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; ViewModel.ToggleSidebarCommand.Execute(null); }

    private void AccelReaderMode_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; ViewModel.ToggleReaderModeCommand.Execute(null); }

    // ---------- Sidebar drag/drop (feed → folder reparent) ----------

    /// <summary>Show the full title in a tooltip only when the row is too narrow
    /// to display it. <see cref="TextBlock.IsTextTrimmed"/> flips whenever the
    /// pane resizes, so we mirror that into <see cref="ToolTipService.ToolTip"/>.</summary>
    private void SidebarTitle_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args)
        => UpdateTrimmedTooltip(sender);

    private void SidebarTitle_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb) UpdateTrimmedTooltip(tb);
    }

    private static void UpdateTrimmedTooltip(TextBlock tb)
    {
        ToolTipService.SetToolTip(tb, tb.IsTextTrimmed ? tb.Text : null);
    }

    private void SidebarListView_DragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
#if PARITY_PROBE
        ProbeSidebarDragStarts++;
#endif
        ClearSidebarDropFeedback();
        StopSidebarReveals();
        _draggedSubscriptions.Clear();
        _dragDestinationAccount = null;
        _dragDestinationFolder = null;
        foreach (var value in args.Items)
        {
            if (value is not SidebarItem { Tag: AccountNs.Feed or AccountNs.Folder } item)
            {
                args.Cancel = true;
                _draggedSubscriptions.Clear();
                return;
            }
            _draggedSubscriptions.Add(item);
        }
        if (_draggedSubscriptions.Any(item => item.Tag is AccountNs.Feed) &&
            _draggedSubscriptions.Any(item => item.Tag is AccountNs.Folder))
        {
            args.Cancel = true;
            _draggedSubscriptions.Clear();
            return;
        }
        var selected = _draggedSubscriptions.ToHashSet();
        _draggedSubscriptions.Clear();
        _draggedSubscriptions.AddRange(_sidebarRows.Where(selected.Contains));
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
            | Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        _activeSidebarDrag = new(Guid.NewGuid().ToString("N"), this, _draggedSubscriptions.ToArray());
        args.Data.Properties[SidebarDragFormat] = _activeSidebarDrag.ID;
        if (_draggedSubscriptions.Count == 1 && _draggedSubscriptions[0].Tag is AccountNs.Feed feed)
        {
            args.Data.SetText(feed.Url);
            if (Uri.TryCreate(feed.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                args.Data.SetWebLink(uri);
        }
    }

    private static bool CopyModifierIsDown()
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private static IReadOnlyList<SidebarItem>? InternalSidebarDrag(Windows.ApplicationModel.DataTransfer.DataPackageView data)
        => _activeSidebarDrag is { } session && data.Properties.TryGetValue(SidebarDragFormat, out var value)
            && value as string == session.ID ? session.Items : null;

    private static Windows.ApplicationModel.DataTransfer.DataPackageOperation SidebarDropOperation(
        IReadOnlyList<SidebarItem> items, AccountNs.Account? account, AccountNs.Folder? folder, bool copyRequested, bool allowReorder = false)
    {
        if (account?.IsActive != true || items.Count == 0 || items.Any(item => AccountForSidebarItem(item) is null)
            || folder is not null && !account.Folders.Contains(folder))
            return Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        if (items.Any(item => item.Tag is AccountNs.Folder))
        {
            if (items.Any(item => item.Tag is not AccountNs.Folder || ReferenceEquals(AccountForSidebarItem(item), account))
                || folder is not null || account.Type.Has(AccountNs.AccountBehavior.DisallowFolderManagement))
                return Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            if (items.Any(item => account.ExistingFolderWithName(((AccountNs.Folder)item.Tag!).NameForDisplay) is not null))
                return Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
        var feeds = items.Select(item => item.Tag).OfType<AccountNs.Feed>().ToArray();
        if (feeds.Length != items.Count)
            return Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        var destinationFeeds = folder?.Feeds ?? account.TopLevelFeeds;
        if (items.Any(item => destinationFeeds.Any(existing => existing.Url == ((AccountNs.Feed)item.Tag!).Url)
            && !(allowReorder && !copyRequested && ReferenceEquals(AccountForSidebarItem(item), account)
                && ReferenceEquals(item.ParentFolder, folder))))
            return Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        if (folder is null && account.Type.Has(AccountNs.AccountBehavior.DisallowFeedInRootFolder))
            return Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        bool allInSameAccount = feeds.All(feed => feed.AccountID == account.AccountID);
        if (allInSameAccount && copyRequested && folder is null
            && account.Type.Has(AccountNs.AccountBehavior.DisallowFeedCopyInRootFolder))
            return Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        if (account.Type.Has(AccountNs.AccountBehavior.DisallowFeedInMultipleFolders)
            && feeds.Any(feed => feed.AccountID == account.AccountID ? copyRequested : account.ExistingFeedWithUrl(feed.Url) is not null))
            return Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        return allInSameAccount && !copyRequested
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private void SidebarListView_DragOver(object sender, DragEventArgs args)
    {
        var dragged = InternalSidebarDrag(args.DataView);
        var placement = SidebarDropTarget(args, dragged);
        var target = placement.Row;
        _dragDestinationAccount = placement.Account;
        _dragDestinationFolder = placement.Folder;
        ClearSidebarDropFeedback();
        if (dragged is null)
        {
            (_dragDestinationAccount, _dragDestinationFolder) = ExternalFeedDropDestination(target);
            var canSubscribe = CanDropExternalFeed(args.DataView, _dragDestinationAccount, _dragDestinationFolder);
            args.AcceptedOperation = canSubscribe ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
                : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            if (canSubscribe)
            {
                args.DragUIOverride.Caption = "Add feed to " + (_dragDestinationFolder?.NameForDisplay ?? _dragDestinationAccount!.NameForDisplay);
                args.DragUIOverride.IsCaptionVisible = true;
            }
            args.Handled = true;
            return;
        }
        bool copyRequested = CopyModifierIsDown();
        bool arrange = CanArrangeSidebarFeeds(dragged, _dragDestinationAccount, copyRequested);
        args.AcceptedOperation = target is not null && dragged.Any(item => item.Identity == target.Identity)
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.None
            : SidebarDropOperation(dragged, _dragDestinationAccount, _dragDestinationFolder, copyRequested, arrange);
        if (args.AcceptedOperation != Windows.ApplicationModel.DataTransfer.DataPackageOperation.None)
        {
            var copy = args.AcceptedOperation == Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            bool mixed = dragged.Any(item => ReferenceEquals(AccountForSidebarItem(item), _dragDestinationAccount))
                && dragged.Any(item => !ReferenceEquals(AccountForSidebarItem(item), _dragDestinationAccount));
            args.DragUIOverride.Caption = mixed && !copyRequested ? "Transfer subscriptions"
                : copy ? "Copy subscriptions" : "Move feeds to " + (_dragDestinationFolder?.NameForDisplay ?? _dragDestinationAccount!.NameForDisplay);
            args.DragUIOverride.IsCaptionVisible = true;
            ShowSidebarDropFeedback(placement, arrange);
            if (_activeSidebarDrag is { } session)
            {
                if (!ReferenceEquals(session.FeedbackOwner, this)) session.FeedbackOwner?.ClearSidebarDropFeedback();
                session.FeedbackOwner = this;
            }
        }
        args.Handled = true;
    }

    private sealed record SidebarPlacement(SidebarItem? Row, AccountNs.Account? Account, AccountNs.Folder? Folder, bool After);

    private SidebarPlacement SidebarDropTarget(DragEventArgs args, IReadOnlyList<SidebarItem>? dragged)
    {
        var target = FindAncestorDataContext<SidebarItem>(args.OriginalSource as DependencyObject);
        var point = args.GetPosition(SidebarListView);
        if (target is null && SidebarListView.ItemsPanelRoot is Panel panel)
        {
            foreach (var container in panel.Children.OfType<ListViewItem>())
            {
                var bounds = container.TransformToVisual(SidebarListView)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
                if (bounds.Contains(point)) { target = container.Content as SidebarItem; break; }
            }
        }
        var account = AccountForSidebarItem(target);
        var folder = target?.Tag as AccountNs.Folder ?? target?.ParentFolder;
        if (target is null && dragged is not null)
        {
            var last = _sidebarRows.LastOrDefault(item => AccountForSidebarItem(item) is not null);
            account = AccountForSidebarItem(last);
        }
        bool after = target?.Tag is AccountNs.Feed && SidebarListView.ContainerFromItem(target) is ListViewItem row
            && args.GetPosition(row).Y >= row.ActualHeight / 2;
        return new(target, account, folder, after);
    }

    private static bool CanArrangeSidebarFeeds(IReadOnlyList<SidebarItem> items, AccountNs.Account? account, bool copyRequested)
        => !copyRequested && account is not null && items.Count > 0
            && items.All(item => item.Tag is AccountNs.Feed feed && feed.AccountID == account.AccountID);

    private static string? SidebarInsertionAnchor(SidebarPlacement target, IReadOnlyList<SidebarItem> dragged)
    {
        if (target.Row?.Tag is not AccountNs.Feed feed || target.Account is null) return null;
        if (!target.After) return feed.Url;
        var moving = dragged.Select(item => ((AccountNs.Feed)item.Tag!).Url).ToHashSet(StringComparer.Ordinal);
        return AppShared.Sidebar.SidebarFeedOrder.Load(Core.AppDefaults.Shared).OrderedFeeds(target.Account, target.Folder)
            .SkipWhile(item => item.Url != feed.Url).Skip(1).FirstOrDefault(item => !moving.Contains(item.Url))?.Url;
    }

    private void ShowSidebarDropFeedback(SidebarPlacement target, bool arrange)
    {
        if (target.Row is null || SidebarListView.ContainerFromItem(target.Row) is not ListViewItem container) return;
        bool insertion = arrange && target.Row.Tag is AccountNs.Feed;
        if (!insertion && target.Row.Tag is not AccountNs.Folder) return;
#if PARITY_PROBE
        if (insertion) ProbeSidebarInsertionTargets++;
        else ProbeSidebarFolderTargets++;
#endif
        var bounds = container.TransformToVisual(SidebarListView)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
        double inset = insertion ? container.Padding.Left + target.Row.OutlineMargin.Left + 20 : 2;
        SidebarDropIndicator.Margin = new Thickness(bounds.Left + inset, insertion && target.After ? bounds.Bottom - 1 : bounds.Top, 0, 0);
        SidebarDropIndicator.Width = Math.Max(0, bounds.Width - inset - 4);
        SidebarDropIndicator.Height = insertion ? 2 : bounds.Height;
        SidebarDropIndicator.BorderThickness = insertion ? new Thickness(0, 2, 0, 0) : new Thickness(1);
        SidebarDropIndicator.CornerRadius = new CornerRadius(insertion ? 0 : 4);
        SidebarDropIndicator.Visibility = Visibility.Visible;
    }

    private void ClearSidebarDropFeedback() => SidebarDropIndicator.Visibility = Visibility.Collapsed;
    private void SidebarListView_DragLeave(object sender, DragEventArgs args) => ClearSidebarDropFeedback();

    private static (AccountNs.Account? Account, AccountNs.Folder? Folder) ExternalFeedDropDestination(SidebarItem? item)
    {
        if (item?.Tag is not (AccountNs.Account or AccountNs.Feed or AccountNs.Folder))
            return (WinNewsWire.AppRuntime.AppService.Shared.Accounts.DefaultAccount, null);
        return (AccountForSidebarItem(item), item.Tag as AccountNs.Folder ?? item.ParentFolder);
    }

    private static bool CanDropExternalFeed(Windows.ApplicationModel.DataTransfer.DataPackageView data,
        AccountNs.Account? account, AccountNs.Folder? folder)
        => account is not null && WinNewsWire.AppRuntime.AppService.Shared.Accounts.ActiveAccounts.Any(candidate => ReferenceEquals(candidate, account))
            && (folder is null || account.Folders.Contains(folder))
            && (folder is not null || !account.Type.Has(AccountNs.AccountBehavior.DisallowFeedInRootFolder))
            && (data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink)
                || data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text));

    private async void SidebarListView_Drop(object sender, DragEventArgs args)
    {
        if (_shuttingDown) return;
        ClearSidebarDropFeedback();
        var dragged = InternalSidebarDrag(args.DataView);
        var placement = SidebarDropTarget(args, dragged);
        var target = placement.Row;
        var account = placement.Account;
        var folder = placement.Folder;
        if (dragged is not null)
        {
            args.Handled = true;
            bool copyRequested = CopyModifierIsDown();
            bool arrange = CanArrangeSidebarFeeds(dragged, account, copyRequested);
            args.AcceptedOperation = target is not null && dragged.Any(item => item.Identity == target.Identity)
                ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.None
                : SidebarDropOperation(dragged, account, folder, copyRequested, arrange);
            if (args.AcceptedOperation == Windows.ApplicationModel.DataTransfer.DataPackageOperation.None) return;
            var completion = args.GetDeferral();
            try
            {
                if (arrange) await PlaceSidebarFeedsAsync(dragged, account!, folder, SidebarInsertionAnchor(placement, dragged));
                else await TransferSidebarItemsAsync(dragged, account!, folder, copyRequested);
            }
            finally { completion.Complete(); }
            return;
        }
        (account, folder) = ExternalFeedDropDestination(target);
        if (!CanDropExternalFeed(args.DataView, account, folder)) return;
        args.Handled = true;
        args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        var deferral = args.GetDeferral();
        await ViewModel.RunSidebarActionAsync("Add feed", async () =>
        {
            string url;
            try
            {
                var text = args.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.WebLink)
                    ? (await args.DataView.GetWebLinkAsync()).AbsoluteUri : (await args.DataView.GetTextAsync()).Trim();
                if (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(['\r', '\n', '\t']) >= 0)
                    throw new InvalidOperationException("Drop one feed or website URL at a time.");
                url = WinNewsWire.Core.SharedFeedUrl.NormalizeUserInput(text);
                if (!CanDropExternalFeed(args.DataView, account, folder))
                    throw new InvalidOperationException("The account or folder is no longer available for this feed.");
                args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            }
            catch (System.Runtime.InteropServices.COMException error)
            {
                throw new InvalidOperationException("The dragged link could not be read. Try dragging it again or paste it into New Feed.", error);
            }
            finally { deferral.Complete(); }
            if (!_shuttingDown) await ShowNewFeedDialogAsync(url, account, folder);
        });
    }

    private void SidebarListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
#if PARITY_PROBE
        ProbeSidebarDragCompletions++;
#endif
        if (ReferenceEquals(_activeSidebarDrag?.Owner, this))
        {
            _activeSidebarDrag.FeedbackOwner?.ClearSidebarDropFeedback();
            _activeSidebarDrag = null;
        }
        ClearSidebarDropFeedback();
        _draggedSubscriptions.Clear();
        _dragDestinationAccount = null;
        _dragDestinationFolder = null;
    }

    private Task PlaceSidebarFeedsAsync(IReadOnlyList<SidebarItem> dragged, AccountNs.Account account, AccountNs.Folder? folder, string? before)
        => ViewModel.RunSidebarActionAsync("Move feeds", async () =>
        {
            if (!CanArrangeSidebarFeeds(dragged, account, copyRequested: false)
                || SidebarDropOperation(dragged, account, folder, copyRequested: false, allowReorder: true)
                    == Windows.ApplicationModel.DataTransfer.DataPackageOperation.None)
                throw new InvalidOperationException("The selected feeds cannot be moved here.");
            await ViewModel.UndoManager.ExecuteAsync(new PlaceFeedsCommand(account,
                dragged.Select(item => ((AccountNs.Feed)item.Tag!, item.ParentFolder)), folder, before, Core.AppDefaults.Shared));
            ViewModel.BuildSidebar();
            if (folder is not null && ViewModel.SidebarItems.SelectMany(section => section.Children)
                .FirstOrDefault(item => ReferenceEquals(item.Tag, folder)) is { IsExpanded: false } folderItem)
                SetSidebarContainerExpanded(folderItem, true);
            var urls = dragged.Select(item => ((AccountNs.Feed)item.Tag!).Url).ToHashSet(StringComparer.Ordinal);
            var moved = ViewModel.SidebarItems.SelectMany(section => section.Children.Concat(section.Children.SelectMany(child => child.Children)))
                .Where(item => item.Tag is AccountNs.Feed feed && feed.AccountID == account.AccountID
                    && urls.Contains(feed.Url) && ReferenceEquals(item.ParentFolder, folder)).ToArray();
            ViewModel.SetSelectedSidebarItems(moved);
        });

    private Task TransferSidebarItemsAsync(IReadOnlyList<SidebarItem> dragged, AccountNs.Account destinationAccount,
        AccountNs.Folder? destination, bool copyRequested)
        => ViewModel.RunSidebarActionAsync("Transfer subscriptions", async () =>
        {
            if (SidebarDropOperation(dragged, destinationAccount, destination, copyRequested)
                == Windows.ApplicationModel.DataTransfer.DataPackageOperation.None)
                throw new InvalidOperationException("The selected subscriptions cannot be dropped here.");
            foreach (var item in dragged)
            {
                var account = AccountForSidebarItem(item)
                    ?? throw new InvalidOperationException("The source account is no longer available.");
                if (item.Tag is AccountNs.Folder folder)
                {
                    if (destination is not null) throw new InvalidOperationException("Drop folders on a different account heading.");
                    await destinationAccount.CopyFolderAsync(folder);
                    continue;
                }
                var feed = (AccountNs.Feed)item.Tag!;
                if (!ReferenceEquals(account, destinationAccount) || copyRequested)
                {
                    await destinationAccount.CopyFeedAsync(feed, destination);
                    continue;
                }
                if (ReferenceEquals(item.ParentFolder, destination)) continue;
                await ViewModel.UndoManager.ExecuteAsync(new MoveFeedCommand(account, feed, item.ParentFolder, destination));
            }
        });

    // ---------- Public helpers consumed by MainWindow's MenuBar ----------

    public Task NewFeedAsync(string? url = null, string? name = null) => ViewModel.RunSidebarActionAsync("Add feed", () =>
    {
        var selected = ViewModel.SelectedSidebarItems.FirstOrDefault(item => AccountForSidebarItem(item) is not null);
        var account = AccountForSidebarItem(selected);
        var folder = selected?.Tag as AccountNs.Folder ?? selected?.ParentFolder;
        return ShowNewFeedDialogAsync(url, account, folder, name);
    });

    internal async Task<bool> SubscribeSharedFeedAsync(string url)
    {
        if (_shuttingDown) throw new InvalidOperationException("The subscription window is closing.");
        var request = await WinNewsWire.Dialogs.AddFeedDialog.ShowAsync(XamlRoot, WinNewsWire.Core.SharedFeedUrl.Parse(url));
        if (request is null) return false;
        if (_shuttingDown) throw new InvalidOperationException("The subscription window is closing.");
        await ViewModel.AddFeedWithResultAsync(request);
        return true;
    }

    private async Task ShowNewFeedDialogAsync(string? url = null, AccountNs.Account? account = null, AccountNs.Folder? folder = null,
        string? name = null)
    {
        if (_shuttingDown) return;
        var request = await WinNewsWire.Dialogs.AddFeedDialog.ShowAsync(XamlRoot, url, account, folder, name);
        if (request is not null) await ViewModel.AddFeedCommand.ExecuteAsync(request);
    }

    public Task NewFolderAsync() => ViewModel.RunSidebarActionAsync("Create folder", async () =>
        await WinNewsWire.Dialogs.AddFolderDialog.ShowAsync(XamlRoot));

    public async Task GoToSmartFeedAsync(WinNewsWire.AppShared.SmartFeeds.IPseudoFeed feed)
    {
        if (!WinNewsWire.AppShared.SmartFeeds.SmartFeedsController.Shared.IsShown(feed))
        {
            ViewModel.StatusMessage = AppStrings.Get("SmartFeedHidden");
            return;
        }
        await ViewModel.RunSidebarActionAsync("Select timeline", async () =>
        {
            var item = ViewModel.SidebarItems.SelectMany(section => section.Children)
                .FirstOrDefault(item => item.Tag is WinNewsWire.AppShared.SmartFeeds.IPseudoFeed candidate && candidate.SidebarItemID == feed.SidebarItemID)
                ?? throw new InvalidOperationException("The requested smart feed is no longer available.");
            ViewModel.SetSelectedSidebarItems([item]);
            ViewModel.SearchQuery = "";
            await ViewModel.FilterArticlesAsync();
        });
        if (!_shuttingDown && ViewModel.IsSidebarVisible && ViewModel.SelectedSidebarItem is { } selected)
        {
            SidebarListView.ScrollIntoView(selected);
            SidebarListView.Focus(FocusState.Programmatic);
        }
    }

    public async Task ToggleSelectedReadAsync() => await ViewModel.MarkSelectedReadCommand.ExecuteAsync(null);
    public async Task ToggleSelectedStarAsync() => await ViewModel.ToggleStarredCommand.ExecuteAsync(null);
    public void SelectNext() => SelectAdjacentArticle(next: true);
    public void SelectPrev() => SelectAdjacentArticle(next: false);

    public void OpenSelectedInBrowser(bool invertPreference = false)
    {
        _ = OpenSelectedLinksAsync(invertPreference);
    }

    private async Task OpenSelectedLinksAsync(bool invertPreference = false)
    {
        if (DisplayedWebPageUri is { } uri)
        {
            await OpenLinkInBrowserAsync(uri.AbsoluteUri, invertPreference);
            return;
        }
        foreach (var article in ViewModel.SelectedArticles.ToList())
            if (article.Link.Length > 0) await OpenLinkInBrowserAsync(article.Link, invertPreference);
    }

    private async Task OpenLinkInBrowserAsync(string link, bool invertPreference = false)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.PreparedForOpeningInBrowser() is null)
        {
            ViewModel.StatusMessage = "This link cannot be opened. Only web, email, magnet, feed, and reader application links are supported.";
            return;
        }
        try
        {
            var owner = App.WindowForXamlRoot(XamlRoot)
                ?? throw new InvalidOperationException("The link's main window is unavailable.");
            if (await Browser.OpenAsync(uri, WinRT.Interop.WindowNative.GetWindowHandle(owner), invertPreference))
                ViewModel.StatusMessage = "The selected browser is unavailable; the system default was used.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            ViewModel.StatusMessage = $"Could not open link: {ex.Message}";
            await WinNewsWire.AppRuntime.AppService.Shared.LogAsync("Open link", ex);
        }
    }

    private IEnumerable<string> SelectedArticleLinks(bool external)
        => ViewModel.SelectedArticles.Select(article => external ? article.ExternalLink : article.Link).Where(link => link.Length > 0);

    public bool CanCopySelectedLink(bool external = false) => SelectedArticleLinks(external).Any();

    public void CopySelectedLink(bool external = false)
    {
        var links = SelectedArticleLinks(external).ToList();
        if (links.Count == 0)
        {
            ViewModel.StatusMessage = external ? "No selected article has an external URL." : "No selected article has a URL.";
            return;
        }
        CopyToClipboard(string.Join(Environment.NewLine, links));
    }

    public void ShareSelected() => ShareSelectedArticle();

    public Task ShowInspectorAsync() => ShowInspectorForAsync(ViewModel.SelectedSidebarItem);

    private Task ShowInspectorForAsync(SidebarItem? item) => ViewModel.RunSidebarActionAsync("Update info", () =>
        WinNewsWire.Dialogs.InspectorDialog.ShowAsync(XamlRoot, item));

    public async Task RenameSelectedAsync()
    {
        // Inline rename: locate the outline row for the selected sidebar item and
        // swap its content for an in-line editor (matches Mac NNW double-click-to-
        // rename behavior). Smart feeds, section headers, and any other unrenamable
        // node type fall through to nothing.
        var item = ViewModel.SelectedSidebarItem;
        if (item is null) return;
        BeginInlineRenameForItem(item);
        await Task.CompletedTask;
    }

    /// <summary>Returns true if the given sidebar item supports renaming — feeds,
    /// folders, and accounts are renamable; smart feeds, section headers, and null
    /// items are not. Used by both the menu bar (to gray out Feed > Rename…) and the
    /// inline rename entry points.</summary>
    public static bool CanRenameSidebarItem(WinNewsWire.Models.SidebarItem? item)
    {
        var account = AccountForSidebarItem(item);
        if (account is null || !account.IsActive) return false;
        return item?.Tag switch
        {
            AccountNs.Feed => !account.Type.Has(AccountBehavior.DisallowFeedRename),
            AccountNs.Folder => !account.Type.Has(AccountBehavior.DisallowFolderManagement),
            AccountNs.Account => true,
            _ => false,
        };
    }

    private static AccountNs.Account? AccountForSidebarItem(SidebarItem? item)
        => item?.Tag is AccountNs.Account account ? account
            : WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts.FirstOrDefault(a =>
                a.AccountID == (item?.Tag switch
                {
                    AccountNs.Feed feed => feed.AccountID,
                    AccountNs.Folder folder => folder.AccountID,
                    _ => null,
                }));

    private void BeginInlineRenameForItem(WinNewsWire.Models.SidebarItem item)
    {
        if (!CanRenameSidebarItem(item)) return;
        // Cancel any other in-flight rename so only one row is in edit mode at a time.
        foreach (var section in ViewModel.SidebarItems)
        {
            section.IsRenaming = false;
            foreach (var child in section.Children)
            {
                child.IsRenaming = false;
                foreach (var grand in child.Children) grand.IsRenaming = false;
            }
        }
        item.EditableTitle = item.Title;
        item.IsRenaming = true;
        // Accelerator suspension happens when the TextBox actually gains focus
        // (SidebarRenameBox_GotFocus) and resumes when it loses focus, so the
        // accelerators only stay disabled while the user is genuinely typing.
        // The template's TextBox watches IsRenaming and becomes visible; its
        // Loaded handler (SidebarRenameBox_Loaded) then focuses + selects.
    }

    // ---------- Inline rename template event handlers ----------

    private void SidebarRenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        tb.SelectAll();
        tb.Focus(FocusState.Programmatic);

        // The built-in WinUI TextBox template includes a clear ("X") button that
        // appears whenever the field has text. In a narrow column this eats the
        // typing area, so we walk the template and collapse it.
        var deleteBtn = FindDescendantByName(tb, "DeleteButton") as UIElement;
        if (deleteBtn is not null) deleteBtn.Visibility = Visibility.Collapsed;
    }

    private void SidebarRenameBox_GotFocus(object sender, RoutedEventArgs e)
        => SuspendSingleKeyAccelerators();

    private void SidebarRenameBox_LostFocus(object sender, RoutedEventArgs e)
        => ResumeSingleKeyAccelerators();

    private static DependencyObject? FindDescendantByName(DependencyObject root, string name)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return child;
            var inner = FindDescendantByName(child, name);
            if (inner is not null) return inner;
        }
        return null;
    }

    private async void SidebarRenameBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                await CommitRenameAsync(GetSidebarItemFor(tb), save: true);
                return;
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                await CommitRenameAsync(GetSidebarItemFor(tb), save: false);
                return;

            // Caret-movement and selection keys that the TextBox handles
            // internally. Mark Handled so the routed event doesn't bubble
            // to the outline list, which would otherwise steal Home/End/arrow
            // keys for row navigation and yank focus out of the field.
            case Windows.System.VirtualKey.Home:
            case Windows.System.VirtualKey.End:
            case Windows.System.VirtualKey.Left:
            case Windows.System.VirtualKey.Right:
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Down:
            case Windows.System.VirtualKey.PageUp:
            case Windows.System.VirtualKey.PageDown:
            case Windows.System.VirtualKey.Space:
                e.Handled = true;
                return;
        }
    }

    private async void SidebarRenameAccept_Click(object sender, RoutedEventArgs e)
        => await CommitRenameAsync(GetSidebarItemFor(sender as DependencyObject), save: true);

    private async void SidebarRenameCancel_Click(object sender, RoutedEventArgs e)
        => await CommitRenameAsync(GetSidebarItemFor(sender as DependencyObject), save: false);

    /// <summary>Walks the visual tree from an event sender (inside the row template)
    /// up to the outline row and returns the SidebarItem it represents.</summary>
    private static WinNewsWire.Models.SidebarItem? GetSidebarItemFor(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement fe)
            {
                if (fe.DataContext is WinNewsWire.Models.SidebarItem direct) return direct;
            }
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private async Task CommitRenameAsync(WinNewsWire.Models.SidebarItem? item, bool save)
    {
        if (item is null || !item.IsRenaming) return;
        var newName = (item.EditableTitle ?? string.Empty).Trim();
        item.IsRenaming = false;
        item.EditableTitle = string.Empty;
        ResumeSingleKeyAccelerators();

        if (save && !string.IsNullOrEmpty(newName) && newName != item.Title)
        {
            await ViewModel.RunSidebarActionAsync("Rename", async () =>
            {
                var account = AccountForSidebarItem(item)
                    ?? throw new InvalidOperationException("The account is no longer available.");
                switch (item.Tag)
                {
                    case WinNewsWire.Account.Feed feed:
                        await account.RenameFeedAsync(feed, newName);
                        break;
                    case WinNewsWire.Account.Folder folder:
                        await account.RenameFolderAsync(folder, newName);
                        break;
                    case WinNewsWire.Account.Account acc:
                        acc.Name = newName;
                        WinNewsWire.AppRuntime.AppService.Shared.Accounts.SaveAccountMeta(acc);
                        break;
                }
            });
        }
    }

    public Task DeleteSelectedAsync() => DeleteSidebarItemsAsync(ViewModel.SelectedSidebarItems.ToArray());

    public async Task MarkAllReadInSelectedAsync()
    {
        if (ViewModel.SelectedSidebarItems.Count > 0)
            await ViewModel.MarkSidebarItemsReadAsync(ViewModel.SelectedSidebarItems);
        else await ViewModel.MarkAllReadCommand.ExecuteAsync(null);
    }

    // ---------- Inline sidebar rename ----------
    //
    // Inline rename is driven by SidebarItem.IsRenaming + .EditableTitle. Setting
    // those properties causes the row template (SidebarRowTemplate in
    // MainContent.xaml) to swap its read-only label for a TextBox + accept/cancel
    // buttons. The template-event handlers (SidebarRenameBox_*) live just above
    // this region. Programmatic entry points: BeginInlineRenameForItem (from
    // right-click) and RenameSelectedAsync (from the Feed menu).
}
