using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.Models;
using WinNewsWire.ViewModels;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private sealed class ReadingLocation
    {
        public required MainWindowState State { get; init; }
        public required ArticleIdentityKey[] Articles { get; init; }
        public required string SearchQuery { get; init; }
        public double ScrollY { get; set; }
        public WebPageSession? WebPage { get; init; }
        public Task Capture { get; set; } = Task.CompletedTask;
    }

    private sealed record BrowserPage(int? EntryID, string Url);

    private sealed class WebPageSession
    {
        public List<BrowserPage> Pages { get; } = [];
        public int Revision { get; set; }
        public ulong NavigationID { get; set; }
        public bool IsLoading { get; set; }
        public bool DocumentCommitted { get; set; }
        public int? RestoringIndex { get; set; }
        public int? ExpectedEntryID { get; set; }
        public int? PreviousEntryID { get; set; }
        public TaskCompletionSource? Traversal { get; set; }
    }

    private readonly List<ReadingLocation> _backHistory = [];
    private WebPageSession? _webPageSession;
    private Task? _webPageInitialization;
    private Task _backTask = Task.CompletedTask;
    private bool _showingWebPage;
    private bool _webPageVisibilityApplied;
    private int _webPageVisibilityVersion;
    private Uri? _pendingWebPagePresentation;
    private ulong _pendingWebPageNavigation;
    private bool _pendingWebPageFailed;
    private bool _goingBack;
    private bool _restoringNavigation;
    private int _browsingVersion;

    internal event EventHandler? NavigationHistoryChanged;
    public bool CanGoBack => !_shuttingDown && !_goingBack
        && (_backHistory.Count > 0 || _showingWebPage && _webPageSession?.Pages.Count > 1);
    public bool CanOpenSelectedInWebView => ViewModel.SelectedArticle is { } article
        && Uri.TryCreate(article.Link, UriKind.Absolute, out var uri) && IsWebAddress(uri);
    private WebView2 ActiveWebView => _showingWebPage ? WebPageView : ContentWebView;
    private ulong ActiveNavigationID => _showingWebPage ? _webPageSession?.NavigationID ?? 0 : _articleNavigationId;
    private bool ActiveDocumentReady => _showingWebPage
        ? _pendingWebPagePresentation is null && _webPageSession is { IsLoading: false, DocumentCommitted: true } : _findDocumentReady;
    internal bool HasDisplayedDocument => _showingWebPage ? _pendingWebPagePresentation is null : ViewModel.SelectedArticle is not null;
    private Uri? DisplayedWebPageUri => _showingWebPage
        ? _pendingWebPagePresentation ?? (Uri.TryCreate(WebPageView.CoreWebView2?.Source, UriKind.Absolute, out var uri) && IsWebAddress(uri) ? uri : null)
        : null;

#if PARITY_PROBE
    internal WebView2 ProbeWebPageView => WebPageView;
    internal bool ProbeShowingWebPage => _showingWebPage;
    internal Task ProbeBackCompletion => _backTask;
    internal int ProbeBackHistoryCount => _backHistory.Count;
    internal int ProbeWebPageHistoryCount => _webPageSession?.Pages.Count ?? 0;
    internal bool ProbeWebPageReady => _showingWebPage && ActiveDocumentReady;
    internal Task ProbeOpenWebPageAsync(string link) => OpenLinkInWebViewAsync(link);
#endif

    private static bool IsWebAddress(Uri uri) => uri.Scheme is "http" or "https";
    private void NotifyNavigationHistory() => NavigationHistoryChanged?.Invoke(this, EventArgs.Empty);

    private void SelectionNavigationStarting(object? sender, EventArgs args)
    {
        if (_restoringNavigation || _restoringLayout || _shuttingDown) return;
        ++_browsingVersion;
        if (_initialization.Task.IsCompletedSuccessfully) RememberCurrentLocation();
        ShowWebPage(false);
    }

    private ReadingLocation CaptureCurrentLocation()
    {
        var location = new ReadingLocation
        {
            State = new()
            {
                Sidebar = new()
                {
                    SelectedItems = ViewModel.SelectedSidebarItems.Select(item => item.Identity!.Value).ToArray(),
                    IsReadFiltered = ViewModel.IsSidebarReadFiltered
                },
                Timeline = ViewModel.CaptureTimelineState(),
                Detail = new(ViewModel.IsReaderMode)
            },
            Articles = ViewModel.SelectedArticles.Select(MainViewModel.Identity).ToArray(),
            SearchQuery = ViewModel.SearchQuery,
            ScrollY = ViewModel.SelectedArticle is { } article && MainViewModel.Identity(article) == _renderedArticleIdentity
                ? _articleScrollPosition : 0,
            WebPage = _showingWebPage ? _webPageSession : null
        };
        location.Capture = CaptureLocationAsync(location);
        TrackBrowserTask(location.Capture);
        return location;
    }

    private void RememberCurrentLocation()
    {
        _backHistory.Add(CaptureCurrentLocation());
        NotifyNavigationHistory();
    }

    private async Task CaptureLocationAsync(ReadingLocation location)
    {
        try
        {
            if (location.WebPage is { } session) await RefreshWebHistoryAsync(session);
            if (!_webViewReady || location.State.Timeline.SelectedArticle != _renderedArticleIdentity) return;
            ulong navigation = _articleNavigationId;
            using var position = JsonDocument.Parse(await ContentWebView.ExecuteScriptAsync(
                "({navigation:window.__wnwNavigationId,offset:window.scrollY})"));
            var value = position.RootElement;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("navigation", out var saved)
                && saved.ValueKind == JsonValueKind.String && saved.GetString() == navigation.ToString(CultureInfo.InvariantCulture)
                && value.TryGetProperty("offset", out var offset) && offset.TryGetDouble(out double y) && double.IsFinite(y) && y >= 0)
                location.ScrollY = y;
        }
        catch (Exception error) when (IsBrowsingError(error))
        {
            App.LogException("Capture navigation position", error);
            if (!_shuttingDown) ViewModel.StatusMessage = "Could not save the reading position: " + error.Message;
        }
    }

    public Task OpenSelectedInWebViewAsync()
    {
        if (!CanOpenSelectedInWebView)
        {
            ViewModel.StatusMessage = "Select an article with a web address to open in Web View.";
            return Task.CompletedTask;
        }
        return OpenLinkInWebViewAsync(ViewModel.SelectedArticle!.Link);
    }

    private async Task OpenLinkInWebViewAsync(string link)
    {
        if (_shuttingDown) return;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || !IsWebAddress(uri))
        {
            if (uri?.Scheme == "mailto") await OpenLinkInBrowserAsync(link);
            else ViewModel.StatusMessage = "This link cannot be opened in Web View. Only web addresses are supported.";
            return;
        }
        int version = ++_browsingVersion;
        try
        {
            await _initialization.Task;
            if (_webPageInitialization is null)
            {
                _webPageInitialization = InitializeWebPageAsync();
                TrackBrowserTask(_webPageInitialization);
            }
            await _webPageInitialization;
            if (_shuttingDown || version != _browsingVersion) return;
            if (!_showingWebPage)
            {
                var previous = CaptureCurrentLocation();
                await previous.Capture;
                if (_shuttingDown || version != _browsingVersion) return;
                _backHistory.Add(previous);
                _webPageSession = new();
                _webPageSession.Pages.Add(new(null, uri.AbsoluteUri));
                _pendingWebPagePresentation = uri;
                _pendingWebPageFailed = false;
                _pendingWebPageNavigation = 0;
                ShowWebPage(true);
            }
            else if (_pendingWebPagePresentation is not null)
            {
                _pendingWebPagePresentation = uri;
                _pendingWebPageFailed = false;
                _pendingWebPageNavigation = 0;
            }
            if (_pendingWebPagePresentation is not null && _webPageSession is { } loading)
            {
                loading.NavigationID = 0;
                loading.IsLoading = true;
                loading.DocumentCommitted = false;
            }
            WebPageError.IsOpen = false;
            UpdateWebPageStatus();
            WebPageView.CoreWebView2.Navigate(uri.AbsoluteUri);
            if (_pendingWebPagePresentation is null) WebPageView.Focus(FocusState.Programmatic);
            else WebPageLoadingFocusTarget.Focus(FocusState.Programmatic);
            NotifyNavigationHistory();
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception error) when (IsBrowsingError(error)) { ReportBrowsingError("Open in Web View", error); }
    }

    private async Task InitializeWebPageAsync()
    {
        if (ContentWebView.CoreWebView2 is { } reader)
            await WebPageView.EnsureCoreWebView2Async(reader.Environment);
        else
        {
            await WebPageView.EnsureCoreWebView2Async(await AppRuntime.WebViewEnvironment.GetAsync());
        }
        if (_shuttingDown) return;
        var core = WebPageView.CoreWebView2;
        await AppRuntime.WebViewScrolling.InstallAsync(core);
        if (_shuttingDown) return;
        // Publisher pages have their own browser; they never receive the article reader's host bindings or theme mappings.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationStarting += WebPageNavigationStarting;
        core.SourceChanged += (_, _) =>
        {
            if (_webPageSession is not { } session || !_showingWebPage || _shuttingDown) return;
            if (_pendingWebPagePresentation is not null && _pendingWebPageNavigation == 0) return;
            session.DocumentCommitted = true;
            TrackBrowserTask(RefreshWebHistoryAsync(session));
        };
        core.HistoryChanged += (_, _) =>
        {
            if (_webPageSession is { } session && _showingWebPage && !_shuttingDown)
                TrackBrowserTask(RefreshWebHistoryAsync(session));
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (_webPageSession is not { } session || args.NavigationId != session.NavigationID || _shuttingDown) return;
            session.IsLoading = false;
            session.DocumentCommitted = true;
            if (args.IsSuccess && args.NavigationId == _pendingWebPageNavigation) RevealWebPage();
            UpdateWebPageStatus();
            TrackBrowserTask(RefreshWebHistoryAsync(session));
            if (!args.IsSuccess && (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled
                || _showingWebPage && _pendingWebPagePresentation is not null))
                ReportBrowsingError("Load web page", new InvalidOperationException(args.WebErrorStatus.ToString()));
            if (IsArticleFindVisible) TrackBrowserTask(QueueArticleFindAsync(ArticleFindOperation.Start, reset: true));
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (_showingWebPage && args.IsUserInitiated) TrackBrowserTask(OpenLinkInWebViewAsync(args.Uri));
            else if (!_shuttingDown) ViewModel.StatusMessage = "An automatic webpage popup was blocked.";
        };
    }

    private void WebPageNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_shuttingDown || !_showingWebPage || _webPageSession is not { } session)
        {
            args.Cancel = true;
            return;
        }
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !IsWebAddress(uri))
        {
            args.Cancel = true;
            if (args.IsUserInitiated) TrackBrowserTask(OpenLinkInWebViewAsync(args.Uri));
            else ViewModel.StatusMessage = "An unsupported automatic webpage navigation was blocked.";
            return;
        }
        session.NavigationID = args.NavigationId;
        session.IsLoading = true;
        session.DocumentCommitted = false;
        if (_pendingWebPagePresentation is not null)
        {
            _pendingWebPagePresentation = uri;
            _pendingWebPageFailed = false;
            _pendingWebPageNavigation = args.NavigationId;
        }
        WebPageError.IsOpen = false;
        StopArticleFind();
        UpdateWebPageStatus();
    }

    private async Task<(BrowserPage[] Pages, int Index)> ReadBrowserHistoryAsync()
    {
        using var history = JsonDocument.Parse(await WebPageView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}"));
        var root = history.RootElement;
        return (root.GetProperty("entries").EnumerateArray().Select(entry => new BrowserPage(
            entry.GetProperty("id").GetInt32(), entry.GetProperty("url").GetString() ?? "")).ToArray(),
            root.GetProperty("currentIndex").GetInt32());
    }

    private async Task RefreshWebHistoryAsync(WebPageSession session)
    {
        if (_shuttingDown || !session.DocumentCommitted || WebPageView.CoreWebView2 is null) return;
        int revision = ++session.Revision;
        var history = await ReadBrowserHistoryAsync();
        if (_shuttingDown || revision != session.Revision || !ReferenceEquals(session, _webPageSession)
            || history.Index < 0 || history.Index >= history.Pages.Length) return;
        var current = history.Pages[history.Index];
        if (!Uri.TryCreate(current.Url, UriKind.Absolute, out var uri) || !IsWebAddress(uri)) return;
        if (session.RestoringIndex is { } restoring)
        {
            bool reached = session.ExpectedEntryID is { } expected ? current.EntryID == expected
                : current.EntryID != session.PreviousEntryID && current.Url == session.Pages[restoring].Url;
            if (!reached) return;
            session.Pages.RemoveRange(restoring + 1, session.Pages.Count - restoring - 1);
            session.Pages[restoring] = current;
            session.RestoringIndex = null;
            session.Traversal?.TrySetResult();
        }
        else
        {
            int existing = session.Pages.FindIndex(page => page.EntryID == current.EntryID);
            int first = Array.FindIndex(history.Pages, page => page.EntryID == session.Pages[0].EntryID);
            if (existing >= 0)
            {
                session.Pages.RemoveRange(existing + 1, session.Pages.Count - existing - 1);
                session.Pages[existing] = current;
            }
            else if (session.Pages[^1].EntryID is null) session.Pages[^1] = current;
            else if (first >= 0 && history.Index >= first)
            {
                session.Pages.Clear();
                session.Pages.AddRange(history.Pages.Skip(first).Take(history.Index - first + 1));
            }
            else session.Pages.Add(current);
        }
        UpdateWebPageStatus();
        NotifyNavigationHistory();
    }

    private async Task RestoreWebPageAsync(WebPageSession session, int index)
    {
        _webPageSession = session;
        int version = _browsingVersion;
        var page = session.Pages[index];
        if (!_showingWebPage)
        {
            _pendingWebPagePresentation = new Uri(page.Url);
            _pendingWebPageFailed = false;
            _pendingWebPageNavigation = 0;
        }
        ShowWebPage(true);
        var history = await ReadBrowserHistoryAsync();
        if (_shuttingDown || !_showingWebPage || version != _browsingVersion || !ReferenceEquals(session, _webPageSession)) return;
        if (history.Index >= 0 && history.Index < history.Pages.Length && history.Pages[history.Index].EntryID == page.EntryID)
        {
            session.Pages.RemoveRange(index + 1, session.Pages.Count - index - 1);
            session.IsLoading = false;
            session.DocumentCommitted = true;
            WebPageError.IsOpen = false;
            RevealWebPage();
            UpdateWebPageStatus();
            return;
        }
        session.RestoringIndex = index;
        session.ExpectedEntryID = history.Pages.Any(item => item.EntryID == page.EntryID) ? page.EntryID : null;
        session.PreviousEntryID = history.Index >= 0 && history.Index < history.Pages.Length ? history.Pages[history.Index].EntryID : null;
        session.Traversal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (session.ExpectedEntryID is { } id)
                await WebPageView.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.navigateToHistoryEntry", JsonSerializer.Serialize(new { entryId = id }));
            else WebPageView.CoreWebView2.Navigate(page.Url);
            await session.Traversal.Task.WaitAsync(TimeSpan.FromSeconds(30), _navigationCancellation.Token);
            if (!session.IsLoading && version == _browsingVersion && ReferenceEquals(session, _webPageSession)) RevealWebPage();
        }
        finally
        {
            session.RestoringIndex = null;
            session.Traversal = null;
        }
    }

    private void ShowWebPage(bool visible)
    {
        if (_webPageVisibilityApplied && _showingWebPage == visible) return;
        _webPageVisibilityApplied = true;
        int visibilityVersion = ++_webPageVisibilityVersion;
        if (_showingWebPage != visible)
        {
            StopArticleFind(detach: true);
            ArticleFindBar.Visibility = Visibility.Collapsed;
            _ = AppRuntime.AppService.Shared.Speech.StopAsync(this);
        }
        _showingWebPage = visible;
        if (visible) WebPageView.CoreWebView2?.Resume();
        WebPageView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ContentWebView.Visibility = !visible && ViewModel.ArticleHtml.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyContentState.Visibility = !visible && ViewModel.ArticleHtml.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArticleDetailStatus.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        WebPageStatus.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            _pendingWebPagePresentation = null;
            _pendingWebPageNavigation = 0;
            _pendingWebPageFailed = false;
            if (WebPageView.CoreWebView2 is { } core)
            {
                core.Stop();
                TrackBrowserTask(SuspendHiddenWebPageAsync(core, visibilityVersion));
            }
            WebPageError.IsOpen = false;
        }
        UpdateWebPageStatus();
        UpdateToolbarState();
        NotifyNavigationHistory();
    }

    private async Task SuspendHiddenWebPageAsync(CoreWebView2 core, int visibilityVersion)
    {
        // WinUI delays hiding the native controller by 200 ms to avoid a background flash.
        try { await Task.Delay(250, _navigationCancellation.Token); }
        catch (OperationCanceledException) when (_shuttingDown) { return; }
        if (_shuttingDown || _showingWebPage || visibilityVersion != _webPageVisibilityVersion) return;
        bool suspended = await core.TrySuspendAsync();
        if (_shuttingDown) return;
        if (_showingWebPage)
        {
            if (suspended) core.Resume();
        }
        else if (!suspended && visibilityVersion == _webPageVisibilityVersion)
            App.LogException("Suspend hidden webpage", null, "WebView2 could not suspend the inactive webpage.");
    }

    private void UpdateWebPageStatus()
    {
        bool covered = _showingWebPage && _pendingWebPagePresentation is not null;
        // WinUI delays native visibility changes; opacity also blocks the cached page during that interval.
        WebPageView.Opacity = _showingWebPage && !covered ? 1 : 0;
        WebPageLoadingState.Visibility = covered ? Visibility.Visible : Visibility.Collapsed;
        WebPageLoadingText.Text = AppShared.AppStrings.Get(_pendingWebPageFailed ? "WebPageLoadFailed" : "WebPageLoading");
        WebPageView.IsHitTestVisible = !covered;
        WebPageView.IsTabStop = !covered;
        WebPageProgress.Visibility = _showingWebPage && (_webPageSession?.IsLoading == true || covered && !_pendingWebPageFailed)
            ? Visibility.Visible : Visibility.Collapsed;
        WebPageStatus.Text = DisplayedWebPageUri?.AbsoluteUri ?? _webPageSession?.Pages.LastOrDefault()?.Url ?? "";
        ToolTipService.SetToolTip(WebPageStatus, WebPageStatus.Text);
    }

    private void RevealWebPage()
    {
        if (_pendingWebPagePresentation is null) return;
        bool focus = _showingWebPage && ReferenceEquals(
            Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot), WebPageLoadingFocusTarget);
        _pendingWebPagePresentation = null;
        _pendingWebPageNavigation = 0;
        _pendingWebPageFailed = false;
        UpdateWebPageStatus();
        if (focus) WebPageView.Focus(FocusState.Programmatic);
    }

    public Task GoBackAsync()
    {
        if (_goingBack) return _backTask;
        if (!CanGoBack) return Task.CompletedTask;
        _goingBack = true;
        NotifyNavigationHistory();
        return _backTask = GoBackCoreAsync();
    }

    private async Task GoBackCoreAsync()
    {
        await Task.Yield();
        int version = ++_browsingVersion;
        try
        {
            if (_showingWebPage && _webPageSession is { } session)
            {
                await RefreshWebHistoryAsync(session);
                if (_shuttingDown || version != _browsingVersion) return;
                if (session.Pages.Count > 1)
                {
                    await RestoreWebPageAsync(session, session.Pages.Count - 2);
                    return;
                }
            }
            if (_backHistory.Count == 0) return;
            var previous = _backHistory[^1];
            _backHistory.RemoveAt(_backHistory.Count - 1);
            await previous.Capture;
            if (_shuttingDown || version != _browsingVersion) return;
            var available = ViewModel.SidebarItems.SelectMany(section => new[] { section }.Concat(section.Children)
                .Concat(section.Children.SelectMany(child => child.Children))).Select(item => item.Identity).ToHashSet();
            if (previous.State.Sidebar.SelectedItems.Any(key => !available.Contains(key)))
                throw new InvalidOperationException("The previously viewed feed or folder is no longer available.");

            bool sameTimeline = ViewModel.SelectedSidebarItems.Select(item => item.Identity!.Value).ToHashSet()
                .SetEquals(previous.State.Sidebar.SelectedItems) && ViewModel.SearchQuery == previous.SearchQuery
                && ViewModel.SortParameters == previous.State.Timeline.SortParameters
                && ViewModel.IsSidebarReadFiltered == previous.State.Sidebar.IsReadFiltered
                && ViewModel.CaptureTimelineState().ReadFilters.OrderBy(item => item.Key)
                    .SequenceEqual(previous.State.Timeline.ReadFilters.OrderBy(item => item.Key));
            var selected = ViewModel.ArticleItems.Where(item => previous.Articles.Contains(MainViewModel.Identity(item))).ToArray();
            Task restoring;
            _restoringNavigation = true;
            try
            {
                ShowWebPage(false);
                if (previous.State.Timeline.SelectedArticle is { } identity)
                    _pendingRestoredScroll = (identity, previous.ScrollY, previous.State.Detail.IsReaderMode);
                if (sameTimeline && selected.Length == previous.Articles.Length)
                {
                    ViewModel.SetSelectedArticles(selected);
                    ViewModel.IsReaderMode = previous.State.Detail.IsReaderMode;
                    restoring = Task.CompletedTask;
                }
                else restoring = ViewModel.RestoreNavigationAsync(previous.State, previous.SearchQuery, previous.Articles.ToHashSet());
            }
            finally { _restoringNavigation = false; }
            await restoring;
            if (_shuttingDown || version != _browsingVersion) return;
            if (!sameTimeline) BuildSidebarUI();
            SyncArticleSelection();
            if (previous.WebPage is { } web)
                await RestoreWebPageAsync(web, web.Pages.Count - 1);
            else if (_webViewReady && ViewModel.SelectedArticle is { } article && MainViewModel.Identity(article) == _renderedArticleIdentity
                && _renderedArticleHtml == ViewModel.ArticleHtml && _pendingArticleDocumentUri is null)
            {
                _pendingRestoredScroll = null;
                _articleScrollPosition = previous.ScrollY;
                await ContentWebView.ExecuteScriptAsync("window.scrollTo(0," + previous.ScrollY.ToString(CultureInfo.InvariantCulture) + ")");
            }
            else if (_webViewReady) LoadArticleHtml(ViewModel.ArticleHtml);
            if (previous.Articles.Length > 0 && ViewModel.SelectedArticles.Count == 0)
                ViewModel.StatusMessage = "The previously viewed article is no longer available.";
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception error) when (IsBrowsingError(error)) { ReportBrowsingError("Go back", error); }
        finally
        {
            _goingBack = false;
            if (!_shuttingDown) NotifyNavigationHistory();
        }
    }

    private static bool IsBrowsingError(Exception error) => error is System.Runtime.InteropServices.COMException
        or InvalidOperationException or ArgumentException or IOException or JsonException or TimeoutException
        or Microsoft.Data.Sqlite.SqliteException;

    private void ReportBrowsingError(string operation, Exception error)
    {
        App.LogException(operation, error);
        if (_shuttingDown) return;
        ViewModel.StatusMessage = operation + " failed: " + error.Message;
        WebPageError.Message = ViewModel.StatusMessage;
        WebPageError.IsOpen = _showingWebPage;
        if (_pendingWebPagePresentation is not null) _pendingWebPageFailed = true;
        UpdateWebPageStatus();
    }
}
