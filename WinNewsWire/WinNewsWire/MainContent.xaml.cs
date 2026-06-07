using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using WinNewsWire.Models;
using WinNewsWire.ViewModels;

namespace WinNewsWire;

public sealed partial class MainContent : UserControl
{
    public MainViewModel ViewModel { get; } = new();

    private bool _webViewReady;
    private bool _shuttingDown;
    private bool _isSidebarDragging;
    private bool _isArticleDragging;
    private double _dragStartX;
    private double _dragStartWidth;
    private double _lastSidebarWidth = 260;

    public MainContent()
    {
        InitializeComponent();
        InitializeWebViewAsync();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.SidebarRebuilt += () =>
        {
            if (_shuttingDown) return;
            if (DispatcherQueue.HasThreadAccess) BuildSidebarUI();
            else DispatcherQueue.TryEnqueue(() => { if (!_shuttingDown) BuildSidebarUI(); });
        };
        // Suspend single-key accelerators (j/k/r/m/s/u/l) while the inline
        // rename TextBox has focus so typing those letters doesn't trigger
        // sidebar actions. Ctrl-modified accelerators stay live so standard
        // text-editing shortcuts (Ctrl+A/C/V/X/Z) keep working in the field.
        Loaded += MainContent_Loaded;
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
            WinNewsWire.Input.MiddleClickAutoScroll.Attach(ArticleListView);
            WinNewsWire.Input.MiddleClickAutoScroll.Attach(SidebarTreeView);
            // RightTapped reliably tells us the visual that was right-clicked
            // (OriginalSource is stable across bubbling, unlike ContextRequested).
            // We pair it with Closed handlers so the captured target is cleared
            // when the flyout dismisses — necessary because RightTapped doesn't
            // fire on TreeView whitespace in WinUI 3.
            SidebarTreeView.RightTapped += SidebarTreeView_RightTapped;
            ArticleListView.RightTapped += ArticleListView_RightTapped;
            SidebarContextMenu.Closed += SidebarContextMenu_Closed;
            ArticleContextMenu.Closed += ArticleContextMenu_Closed;
            await ViewModel.InitializeAsync();
            BuildSidebarUI();
            ApplyUnifiedLayout();
        }
        catch (Exception ex)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinNewsWire", "crash.log");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.AppendAllText(path,
                    $"[{DateTime.Now:O}] MainContent_Loaded: {ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
            }
            catch { }
            throw;
        }
    }

    /// <summary>
    /// Closes the WebView2 control cleanly before app/window teardown.
    /// WinUI 3 apps otherwise throw winrt::hresult_error (0x8007139F,
    /// "The group or resource is not in the correct state...") during exit
    /// because the CoreWebView2 environment is still running when the UI
    /// thread unwinds. Calling Close() shuts it down on the UI thread first.
    /// </summary>
    public void Shutdown()
    {
        try
        {
            _shuttingDown = true;
            _webViewReady = false;
            // Detach ViewModel events first so any background activity during
            // AppService.Stop() can't dispatch back into XAML elements that
            // are about to be (or already) torn down. Otherwise we get a
            // RO_E_CLOSED (0x80000013) thrown on the UI thread during exit.
            try { ViewModel.PropertyChanged -= ViewModel_PropertyChanged; } catch { }
            ContentWebView?.Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 shutdown error: {ex.Message}");
        }
    }

    private const string IframeProxyBase = "https://winnewswire.local/iframe-proxy";

    private static readonly System.Text.RegularExpressions.Regex IframeRegex =
        new(@"<iframe\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""'][^>]*?>\s*(?:</iframe>)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex WidthAttrRegex =
        new(@"\bwidth\s*=\s*[""']?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex HeightAttrRegex =
        new(@"\bheight\s*=\s*[""']?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

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
            await ContentWebView.EnsureCoreWebView2Async();
            ContentWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            ContentWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            ContentWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Have the article webview honor the app's appearance preference so
            // the `@media (prefers-color-scheme: dark)` rules inside the article
            // stylesheet light up the right palette even when the user has chosen
            // a theme that differs from Windows.
            ApplyWebViewColorScheme();

            ContentWebView.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                _ = Windows.System.Launcher.LaunchUriAsync(new Uri(e.Uri));
            };

            // Intercept the per-iframe proxy URL. Embeds (YouTube, etc.) need
            // a real http(s):// parent origin or they fail (YouTube Error 153).
            // The article body itself stays on the fast NavigateToString path;
            // only each iframe is hosted inside a tiny proxy document served
            // from the synthetic https://winnewswire.local/ origin so YouTube
            // sees a valid parent.
            ContentWebView.CoreWebView2.AddWebResourceRequestedFilter(
                "https://winnewswire.local/*", CoreWebView2WebResourceContext.All);
            ContentWebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

            // When the WebView2 has focus it eats every keystroke; the
            // UserControl-level KeyboardAccelerators never fire. Inject a tiny
            // shim that listens for known j/k/r/m/s/l/u/space/Ctrl-combos at
            // document scope and proxies them back via WebMessage so they
            // continue to work while the reading pane is focused.
            await ContentWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(KeyboardForwarderScript);
            ContentWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            _webViewReady = true;

            if (!string.IsNullOrEmpty(ViewModel.ArticleHtml))
                LoadArticleHtml(ViewModel.ArticleHtml);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 init error: {ex.Message}");
        }
    }

    /// <summary>JavaScript injected into every article document so single-key
    /// shortcuts (j/k/r/m/s/l/u/space) and Ctrl-combos still work when the
    /// WebView2 has focus. The script suppresses the default browser action
    /// for the keys it claims and posts a JSON message back to the host.</summary>
    private const string KeyboardForwarderScript = @"
(function () {
    if (window.__wnwShortcutsInstalled) return;
    window.__wnwShortcutsInstalled = true;
    document.addEventListener('keydown', function (e) {
        // Skip when the user is typing in an input/textarea/contenteditable so
        // articles with comment boxes or search inputs keep working normally.
        var t = e.target;
        if (t) {
            var tag = (t.tagName || '').toLowerCase();
            if (tag === 'input' || tag === 'textarea' || tag === 'select') return;
            if (t.isContentEditable) return;
        }
        var key = (e.key || '').toLowerCase();
        var ctrl = e.ctrlKey || e.metaKey;
        var shift = e.shiftKey;
        var alt = e.altKey;
        var cmd = null;
        if (!ctrl && !alt && !shift) {
            switch (key) {
                case 'j': cmd = 'next'; break;
                case 'k': cmd = 'prev'; break;
                case 'r': cmd = 'refresh'; break;
                case 'm': case 'u': cmd = 'markRead'; break;
                case 's': case 'l': cmd = 'toggleStar'; break;
                case ' ': cmd = 'nextUnread'; break;
            }
        } else if (ctrl && !alt && !shift) {
            switch (key) {
                case 'arrowdown': cmd = 'next'; break;
                case 'arrowup':   cmd = 'prev'; break;
                case 'r': cmd = 'refresh'; break;
                case 'k': cmd = 'markAllRead'; break;
                case 'i': cmd = 'inspector'; break;
                case 'n': cmd = 'newFeed'; break;
                case 'l': cmd = 'toggleStar'; break;
                case 'b': cmd = 'openBrowser'; break;
            }
        } else if (ctrl && shift && !alt) {
            switch (key) {
                case 'b': cmd = 'toggleSidebar'; break;
                case 'w': cmd = 'toggleReaderMode'; break;
                case 'n': cmd = 'newFolder'; break;
            }
        }
        if (cmd) {
            e.preventDefault();
            e.stopPropagation();
            try { window.chrome.webview.postMessage(JSON.stringify({ type: 'shortcut', cmd: cmd })); }
            catch (_) { /* webview bridge unavailable; nothing to forward to */ }
        }
    }, true);
})();
";

    /// <summary>Routes shortcut commands forwarded by the in-page JS shim
    /// (<see cref="KeyboardForwarderScript"/>) to the same view-model commands
    /// the UserControl-level KeyboardAccelerators invoke when the rest of the
    /// app has focus.</summary>
    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            string? raw = null;
            try { raw = args.TryGetWebMessageAsString(); }
            catch { raw = args.WebMessageAsJson; }
            if (string.IsNullOrEmpty(raw)) return;

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "shortcut") return;
            if (!root.TryGetProperty("cmd", out var cmdEl)) return;

            var cmd = cmdEl.GetString();
            DispatchWebViewShortcut(cmd);
        }
        catch
        {
            // Malformed payload — ignore. The shim should always send valid JSON,
            // but a misbehaving page script could call postMessage too.
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
                    await WinNewsWire.Dialogs.InspectorDialog.ShowAsync(XamlRoot, ViewModel.SelectedSidebarItem);
                    break;
                case "newFeed":          await NewFeedAsync(); break;
                case "newFolder":        await NewFolderAsync(); break;
                case "toggleSidebar":    ViewModel.ToggleSidebarCommand.Execute(null); break;
                case "toggleReaderMode": ViewModel.ToggleReaderModeCommand.Execute(null); break;
                case "nextUnread":       SelectNextUnread(); break;
                case "openBrowser":      OpenSelectedInBrowser(); break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView shortcut '{cmd}' failed: {ex.Message}");
        }
    }

    private void LoadArticleHtml(string html)
    {
        // Fast path: NavigateToString the article body. Iframes are rewritten
        // to a placeholder showing a spinner + a deferred iframe that loads
        // through the synthetic-origin proxy. The page paints immediately
        // and the embed loads in the background.
        var prepared = PrepareArticleHtml(html);
        ContentWebView.NavigateToString(prepared);
    }

    private static string PrepareArticleHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        if (html.IndexOf("<iframe", StringComparison.OrdinalIgnoreCase) < 0) return html;

        var hasReplacements = false;
        var rewritten = IframeRegex.Replace(html, m =>
        {
            hasReplacements = true;
            var src = m.Groups[1].Value;
            var tag = m.Value;

            var wm = WidthAttrRegex.Match(tag);
            var hm = HeightAttrRegex.Match(tag);
            string boxStyle;
            if (wm.Success && hm.Success && int.TryParse(wm.Groups[1].Value, out var w) && int.TryParse(hm.Groups[1].Value, out var h) && w > 0 && h > 0)
                boxStyle = $"max-width:{w}px;aspect-ratio:{w}/{h};";
            else
                boxStyle = "max-width:100%;aspect-ratio:16/9;";

            var proxyUrl = IframeProxyBase + "?u=" + Uri.EscapeDataString(src);
            var safeProxy = System.Net.WebUtility.HtmlEncode(proxyUrl);

            return
                "<div class=\"wnw-iframe-wrap\" style=\"position:relative;" + boxStyle + "width:100%;background:#1a1a1a;margin:1em auto;\">"
                + "<div class=\"wnw-spinner\" style=\"position:absolute;inset:0;display:flex;align-items:center;justify-content:center;\">"
                + "<div style=\"width:32px;height:32px;border:3px solid #444;border-top-color:#bbb;border-radius:50%;animation:wnw-spin 1s linear infinite;\"></div>"
                + "</div>"
                + "<iframe src=\"" + safeProxy + "\" onload=\"this.previousElementSibling.style.display='none'\""
                + " style=\"position:absolute;inset:0;width:100%;height:100%;border:0;\""
                + " allow=\"accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share\" allowfullscreen></iframe>"
                + "</div>";
        });

        if (!hasReplacements) return html;

        var spinnerCss = "<style>@keyframes wnw-spin{to{transform:rotate(360deg);}}</style>";
        var headIdx = rewritten.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headIdx >= 0)
            rewritten = rewritten.Substring(0, headIdx) + spinnerCss + rewritten.Substring(headIdx);
        else
            rewritten = spinnerCss + rewritten;

        return rewritten;
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        try
        {
            var uri = args.Request.Uri;
            if (uri.IndexOf("/iframe-proxy", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            var qIdx = uri.IndexOf('?');
            if (qIdx < 0) return;
            string? origSrc = null;
            foreach (var part in uri.Substring(qIdx + 1).Split('&'))
            {
                if (part.StartsWith("u=", StringComparison.OrdinalIgnoreCase))
                {
                    try { origSrc = Uri.UnescapeDataString(part.Substring(2)); } catch { }
                    break;
                }
            }
            if (string.IsNullOrEmpty(origSrc)) return;
            var safeSrc = System.Net.WebUtility.HtmlEncode(origSrc);
            var pageHtml =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
                + "html,body{margin:0;padding:0;height:100%;background:#000;overflow:hidden;}"
                + "iframe{width:100%;height:100%;border:0;display:block;}"
                + "</style></head><body><iframe src=\"" + safeSrc + "\""
                + " allow=\"accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share\""
                + " allowfullscreen></iframe></body></html>";
            var bytes = System.Text.Encoding.UTF8.GetBytes(pageHtml);
            var ras = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(
                new System.IO.MemoryStream(bytes, writable: false));
            args.Response = ContentWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                ras, 200, "OK", "Content-Type: text/html; charset=utf-8");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebResourceRequested error: {ex.Message}");
        }
    }

    private FeedItem? _starWatchedArticle;

    private void UpdateStarButtonIcon()
    {
        StarButtonIcon.Glyph = ViewModel.SelectedArticle?.IsStarred == true ? "\uE735" : "\uE734";
    }

    private void SelectedArticle_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_shuttingDown) return;
        if (e.PropertyName == nameof(FeedItem.IsStarred))
            DispatcherQueue.TryEnqueue(() => { if (!_shuttingDown) UpdateStarButtonIcon(); });
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
        if (e.PropertyName == nameof(ViewModel.SelectedArticle))
        {
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
                if (_webViewReady && !string.IsNullOrEmpty(ViewModel.ArticleHtml))
                {
                    LoadArticleHtml(ViewModel.ArticleHtml);
                    EmptyContentState.Visibility = Visibility.Collapsed;
                    ContentWebView.Visibility = Visibility.Visible;
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
            // If the user toggled the sidebar manually (e.g. via the menu),
            // forget that we auto-collapsed it so we don't stomp their choice
            // the next time the width crosses the breakpoint.
            if (!_selfChangingSidebar) _autoCollapsedSidebar = false;
            DispatcherQueue.TryEnqueue(() => AnimateSidebar(ViewModel.IsSidebarVisible));
        }
        else if (e.PropertyName == nameof(ViewModel.IsUnifiedLayout))
        {
            DispatcherQueue.TryEnqueue(ApplyUnifiedLayout);
        }
    }

    private void ApplyUnifiedLayout()
    {
        var unified = ViewModel.IsUnifiedLayout;
        if (unified)
        {
            if (SidebarColumn.Width.Value > 0) _lastSidebarWidth = SidebarColumn.Width.Value;
            SidebarColumn.Width = new Microsoft.UI.Xaml.GridLength(0);
            SidebarColumn.MinWidth = 0;
            if (ArticleListColumn.Width.Value > 0) _lastArticleListWidth = ArticleListColumn.Width.Value;
            ArticleListColumn.Width = new Microsoft.UI.Xaml.GridLength(0);
            ArticleListColumn.MinWidth = 0;
        }
        else
        {
            SidebarColumn.Width = new Microsoft.UI.Xaml.GridLength(_lastSidebarWidth > 0 ? _lastSidebarWidth : 260);
            SidebarColumn.MinWidth = 200;
            ArticleListColumn.Width = new Microsoft.UI.Xaml.GridLength(_lastArticleListWidth > 0 ? _lastArticleListWidth : 380);
            ArticleListColumn.MinWidth = 280;
            // Re-evaluate responsive collapse after unified mode is turned off so
            // a narrow window doesn't suddenly show all three panels.
            _wasAboveSidebarBreakpoint = null;
            _wasAboveArticleListBreakpoint = null;
            ApplyResponsiveLayout(MainContentGrid.ActualWidth);
        }
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
        if (width <= 0) return;
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

        bool aboveList = width >= ArticleListBreakpointPx;
        if (_wasAboveArticleListBreakpoint != aboveList)
        {
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

    private void BuildSidebarUI()
    {
        // Preserve selection across rebuilds: clearing RootNodes drops the
        // TreeView's selection (no highlight) and BuildSidebar replaces every
        // SidebarItem instance, so ViewModel.SelectedSidebarItem also goes
        // stale. Capture a stable identity key, rebuild, then re-select the
        // matching new node and rebind the view-model reference.
        var prevSel = ViewModel.SelectedSidebarItem;
        object? prevTag = prevSel?.Tag;
        string? prevTitleKey = prevSel is not null && prevTag is null
            ? $"{prevSel.ItemType}|{prevSel.Title}"
            : null;

        SidebarTreeView.RootNodes.Clear();

        TreeViewNode? nodeToSelect = null;
        SidebarItem? itemToSelect = null;

        bool Matches(SidebarItem candidate)
        {
            if (prevSel is null) return false;
            if (prevTag is not null) return ReferenceEquals(candidate.Tag, prevTag);
            return candidate.Tag is null
                && prevTitleKey == $"{candidate.ItemType}|{candidate.Title}";
        }

        // Sections (e.g. "Smart Feeds", "On My PC") are rendered as flat title
        // headings, not as expandable nodes. To get a heading look without a
        // chevron, we add the section header itself as a leaf root node and
        // then add each of its former children as additional root nodes after
        // it. The DataTemplateSelector + ItemContainer override in
        // SidebarSectionHeader_Loaded make the heading non-interactive.
        foreach (var section in ViewModel.SidebarItems.ToList())
        {
            var sectionNode = new TreeViewNode { Content = section };
            SidebarTreeView.RootNodes.Add(sectionNode);
            if (nodeToSelect is null && Matches(section))
            {
                nodeToSelect = sectionNode;
                itemToSelect = section;
            }

            foreach (var child in section.Children.ToList())
            {
                var childNode = new TreeViewNode
                {
                    Content = child,
                    IsExpanded = child.IsExpanded
                };

                if (child.ItemType == SidebarItemType.Folder)
                {
                    foreach (var feedChild in child.Children.ToList())
                    {
                        var grandNode = new TreeViewNode { Content = feedChild };
                        childNode.Children.Add(grandNode);
                        if (nodeToSelect is null && Matches(feedChild))
                        {
                            nodeToSelect = grandNode;
                            itemToSelect = feedChild;
                        }
                    }
                }

                SidebarTreeView.RootNodes.Add(childNode);
                if (nodeToSelect is null && Matches(child))
                {
                    nodeToSelect = childNode;
                    itemToSelect = child;
                }
            }
        }

        if (nodeToSelect is not null && itemToSelect is not null)
        {
            SidebarTreeView.SelectedNode = nodeToSelect;
            if (!ReferenceEquals(itemToSelect, prevSel))
                ViewModel.SelectedSidebarItem = itemToSelect;
        }
    }

    private void AnimateSidebar(bool show)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(SidebarPanel).Compositor;
        var sidebarVisual = ElementCompositionPreview.GetElementVisual(SidebarPanel);
        var splitterVisual = ElementCompositionPreview.GetElementVisual(SidebarSplitter);

        var duration = TimeSpan.FromMilliseconds(250);
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        if (show)
        {
            // Restore column sizing first so layout has target width
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
            SidebarColumn.MinWidth = 200;
            SidebarColumn.MaxWidth = 400;
            SidebarColumn.Width = new GridLength(_lastSidebarWidth);

            // Remove left margin since sidebar provides spacing
            ContentContainer.Margin = new Thickness(0, 4, 4, 0);

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
            _lastSidebarWidth = SidebarColumn.ActualWidth;

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
                    SidebarColumn.Width = new GridLength(0);
                    SidebarColumn.MinWidth = 0;
                    SidebarColumn.MaxWidth = 0;
                    SidebarPanel.Visibility = Visibility.Collapsed;
                    SidebarSplitter.Visibility = Visibility.Collapsed;
                    // Add left margin to match right-side padding
                    ContentContainer.Margin = new Thickness(4, 4, 4, 0);
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
        _dragStartX = e.GetCurrentPoint(ArticleContentGrid).Position.X;
        _dragStartWidth = ArticleListColumn.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ArticleSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isArticleDragging) return;
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
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }

    private void Splitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSidebarDragging && !_isArticleDragging)
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    // --- Event handlers ---
    private void SidebarTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && node.Content is SidebarItem item)
        {
            // Section headings (Smart Feeds, On My PC, …) are titles and not
            // selectable. Belt-and-suspenders: even though the container is set
            // IsHitTestVisible=false on load, ignore any stray invocation.
            if (item.ItemType == SidebarItemType.SectionHeader) return;
            ViewModel.SelectedSidebarItem = item;
        }
    }

    private void ToggleSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleSidebarCommand.Execute(null);
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ReloadFeedsCommand.ExecuteAsync(null);
        BuildSidebarUI();
    }
    private async void NewFeed_Click(object sender, RoutedEventArgs e)
    {
        var req = await WinNewsWire.Dialogs.AddFeedDialog.ShowAsync(XamlRoot);
        if (req is null) return;
        await ViewModel.AddFeedCommand.ExecuteAsync(req);
        BuildSidebarUI();
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var accounts = WinNewsWire.AppRuntime.AppService.Shared.Accounts.ActiveAccounts.ToList();
        if (accounts.Count == 0) return;

        var dialog = new ContentDialog
        {
            Title = "New Folder",
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var nameBox = new TextBox { PlaceholderText = "Folder name", MinWidth = 360 };
        var accountCombo = new ComboBox { MinWidth = 360 };
        foreach (var a in accounts)
            accountCombo.Items.Add(new ComboBoxItem { Content = a.NameForDisplay, Tag = a });
        accountCombo.SelectedIndex = 0;

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Folder Name:" });
        stack.Children.Add(nameBox);
        stack.Children.Add(new TextBlock { Text = "Account:" });
        stack.Children.Add(accountCombo);
        dialog.Content = stack;

        AppWindows.WindowThemeHelper.Apply(dialog);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(nameBox.Text)
            && ((ComboBoxItem)accountCombo.SelectedItem!).Tag is WinNewsWire.Account.Account account)
        {
            account.AddFolder(nameBox.Text.Trim());
            ViewModel.BuildSidebar();
            BuildSidebarUI();
        }
    }

    private async void StarButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ToggleStarredCommand.ExecuteAsync(null);
    }

    private async void MarkAllReadButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.MarkAllReadCommand.ExecuteAsync(null);
        ViewModel.BuildSidebar();
        BuildSidebarUI();
    }

    private async void OpenInBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedArticle != null && !string.IsNullOrEmpty(ViewModel.SelectedArticle.Link))
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(ViewModel.SelectedArticle.Link));
        }
    }

    private void ShareButton_Click(object sender, RoutedEventArgs e) => ShareSelectedArticle();

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.SearchQuery = args.QueryText ?? string.Empty;
    }

    // ---------- Context menus ----------
    //
    // The TreeView (sidebar) and ListView (timeline) each have an empty MenuFlyout in
    // XAML; we rebuild its Items collection when the menu opens, mirroring NetNewsWire
    // Mac's SidebarViewController+ContextualMenus and TimelineViewController+ContextualMenus.

    private WinNewsWire.Models.SidebarItem? _sidebarRightClickedItem;
    private WinNewsWire.Models.FeedItem? _articleRightClickedItem;

    /// <summary>Walk the visual tree looking for a DataContext that matches <typeparamref name="T"/>.
    /// WinUI's TreeView wraps each item in a TreeViewItem whose DataContext is the
    /// <c>TreeViewNode</c>, so we also unwrap that to reach the model object. Same applies
    /// to anything else that exposes data via a property called <c>Content</c>.</summary>
    private static T? FindAncestorDataContext<T>(Microsoft.UI.Xaml.DependencyObject? element) where T : class
    {
        while (element is not null)
        {
            if (element is FrameworkElement fe)
            {
                if (fe.DataContext is T t) return t;
                if (fe.DataContext is Microsoft.UI.Xaml.Controls.TreeViewNode node && node.Content is T tc)
                    return tc;
            }
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void SidebarTreeView_RightTapped(object sender, RightTappedRoutedEventArgs e)
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

    private static MenuFlyoutItem MakeMenuItem(string text, string? glyph, RoutedEventHandler click, object? tag = null, bool isEnabled = true)
    {
        var mi = new MenuFlyoutItem { Text = text, IsEnabled = isEnabled, Tag = tag };
        if (!string.IsNullOrEmpty(glyph))
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
        if (item is null)
        {
            BuildSidebarMenuForNoSelection(menu);
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
        => (sender as MenuFlyoutItem)?.Tag as WinNewsWire.Models.SidebarItem ?? fallback;

    private async void CtxMarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item is null) return;
        await MarkSidebarItemReadAsync(item);
        ViewModel.UpdateSidebarUnreadCounts();
    }

    private async Task MarkSidebarItemReadAsync(WinNewsWire.Models.SidebarItem item)
    {
        switch (item.Tag)
        {
            case WinNewsWire.Account.Feed feed:
            {
                var account = WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
                    .FirstOrDefault(a => a.AccountID == feed.AccountID);
                if (account is null) return;
                var articles = await account.Database.FetchArticlesAsync(feed.FeedID);
                var unread = articles.Where(a => !a.Status.Read).Select(a => a.ArticleID).ToList();
                if (unread.Count > 0) await account.MarkAsync(unread, WinNewsWire.Articles.ArticleStatus.Key.Read, true);
                break;
            }
            case WinNewsWire.Account.Folder folder:
            {
                var account = WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
                    .FirstOrDefault(a => a.AccountID == folder.AccountID);
                if (account is null) return;
                var feedIds = folder.Feeds.Select(f => f.FeedID);
                var unread = await account.Database.FetchUnreadArticlesAsync(feedIds);
                if (unread.Count > 0)
                    await account.MarkAsync(unread.Select(a => a.ArticleID), WinNewsWire.Articles.ArticleStatus.Key.Read, true);
                break;
            }
            case WinNewsWire.Account.Account account:
            {
                var feedIds = account.FlattenedFeeds().Select(f => f.FeedID);
                var unread = await account.Database.FetchUnreadArticlesAsync(feedIds);
                if (unread.Count > 0)
                    await account.MarkAsync(unread.Select(a => a.ArticleID), WinNewsWire.Articles.ArticleStatus.Key.Read, true);
                break;
            }
        }
        await ViewModel.FilterArticlesAsync();
    }

    private async void CtxRefresh_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        var account = item?.Tag switch
        {
            WinNewsWire.Account.Account a => a,
            WinNewsWire.Account.Feed f => WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts.FirstOrDefault(x => x.AccountID == f.AccountID),
            WinNewsWire.Account.Folder fl => WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts.FirstOrDefault(x => x.AccountID == fl.AccountID),
            _ => null,
        };
        if (account is null) { await ViewModel.ReloadFeedsCommand.ExecuteAsync(null); }
        else { await account.RefreshAllAsync(); ViewModel.BuildSidebar(); }
        BuildSidebarUI();
    }

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
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item is null) return;
        var confirm = new ContentDialog
        {
            Title = "Delete?",
            Content = $"Are you sure you want to delete \u201C{item.Title}\u201D? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        AppWindows.WindowThemeHelper.Apply(confirm);
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        switch (item.Tag)
        {
            case WinNewsWire.Account.Feed feed:
                WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
                    .FirstOrDefault(a => a.AccountID == feed.AccountID)?.RemoveFeed(feed);
                break;
            case WinNewsWire.Account.Folder folder:
                WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
                    .FirstOrDefault(a => a.AccountID == folder.AccountID)?.RemoveFolder(folder);
                break;
        }
        ViewModel.BuildSidebar();
        BuildSidebarUI();
    }

    private async void CtxOpenHomePage_Click(object sender, RoutedEventArgs e)
    {
        var item = TargetSidebarItem(sender, _sidebarRightClickedItem ?? ViewModel.SelectedSidebarItem);
        if (item?.Tag is WinNewsWire.Account.Feed feed && !string.IsNullOrEmpty(feed.HomePageUrl)
            && Uri.TryCreate(feed.HomePageUrl, UriKind.Absolute, out var uri))
            await Windows.System.Launcher.LaunchUriAsync(uri);
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
        await WinNewsWire.Dialogs.InspectorDialog.ShowAsync(XamlRoot, item);
        ViewModel.BuildSidebar();
        BuildSidebarUI();
    }

    // ===== Article (timeline) context menu =====

    private void ArticleContextMenu_Opening(object sender, object e)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();

        // Match Mac NNW: clicking empty whitespace shows no menu at all. Otherwise
        // if the right-clicked row is part of the selection, act on the selection;
        // else act on the right-clicked row alone. ArticleListView is currently
        // single-select so this collapses to one article when a row is hit.
        var clicked = _articleRightClickedItem;
        if (clicked is null) return;

        var selected = ViewModel.SelectedArticle;
        var articles = new List<WinNewsWire.Models.FeedItem>();
        if (selected is not null && ReferenceEquals(clicked, selected))
            articles.Add(selected);
        else
            articles.Add(clicked);

        var anyUnread = articles.Any(a => !a.IsRead);
        var anyRead = articles.Any(a => a.IsRead);
        var anyUnstarred = articles.Any(a => !a.IsStarred);
        var anyStarred = articles.Any(a => a.IsStarred);

        if (anyUnread)
            menu.Items.Add(MakeMenuItem("Mark as Read", "\uE73E", CtxMarkRead_Click, articles));
        if (anyRead)
            menu.Items.Add(MakeMenuItem("Mark as Unread", "\uE7C3", CtxMarkUnread_Click, articles));
        if (anyUnstarred)
            menu.Items.Add(MakeMenuItem("Mark as Starred", "\uE734", CtxMarkStarred_Click, articles));
        if (anyStarred)
            menu.Items.Add(MakeMenuItem("Mark as Unstarred", "\uE735", CtxMarkUnstarred_Click, articles));

        var first = articles.First();
        var last = articles.Last();
        var firstIdx = ViewModel.ArticleItems.IndexOf(first);
        var lastIdx = ViewModel.ArticleItems.IndexOf(last);

        if (firstIdx > 0 && ViewModel.ArticleItems.Take(firstIdx).Any(a => !a.IsRead))
            menu.Items.Add(MakeMenuItem("Mark Above as Read", "\uE74A", CtxMarkAboveRead_Click, articles));
        if (lastIdx >= 0 && lastIdx < ViewModel.ArticleItems.Count - 1
            && ViewModel.ArticleItems.Skip(lastIdx + 1).Any(a => !a.IsRead))
            menu.Items.Add(MakeMenuItem("Mark Below as Read", "\uE74B", CtxMarkBelowRead_Click, articles));

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

    private void CtxSelectFeedInSidebar_Click(object sender, RoutedEventArgs e)
    {
        var a = ArticleFrom(sender, ViewModel.SelectedArticle);
        if (a is null) return;
        var sidebarItem = ViewModel.FindSidebarItemForFeed(a.AccountID, a.FeedId);
        if (sidebarItem is not null)
        {
            ViewModel.SelectedSidebarItem = sidebarItem;
            // Re-sync the TreeView's selected node to match.
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
        if (a is { Link: { Length: > 0 } link } && Uri.TryCreate(link, UriKind.Absolute, out var uri))
            await Windows.System.Launcher.LaunchUriAsync(uri);
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

    private void CtxArticleShare_Click(object sender, RoutedEventArgs e) => ShareSelectedArticle();

    private void ShareSelectedArticle()
    {
        var a = ViewModel.SelectedArticle;
        if (a is null || App.MainWindow is null) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var dtm = Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop
                .GetForWindow(hwnd);

            void OnRequest(Windows.ApplicationModel.DataTransfer.DataTransferManager s,
                           Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs args)
            {
                var req = args.Request;
                req.Data.Properties.Title = a.Title ?? "Article";
                req.Data.SetText(a.Title ?? string.Empty);
                if (Uri.TryCreate(a.Link, UriKind.Absolute, out var u))
                    req.Data.SetWebLink(u);
                dtm.DataRequested -= OnRequest;
            }
            dtm.DataRequested += OnRequest;

            Windows.ApplicationModel.DataTransfer.DataTransferManagerInterop
                .ShowShareUIForWindow(hwnd);
        }
        catch { /* sharing is best-effort */ }
    }

    private static void CopyToClipboard(string text)
    {
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
    }

    // ---------- Keyboard accelerators ----------

    private void AccelNext_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { ViewModel.NextArticleCommand.Execute(null); e.Handled = true; }

    private void AccelPrev_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { ViewModel.PreviousArticleCommand.Execute(null); e.Handled = true; }

    private async void AccelRefresh_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ViewModel.ReloadFeedsCommand.ExecuteAsync(null); BuildSidebarUI(); }

    private async void AccelMarkRead_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ViewModel.MarkSelectedReadCommand.ExecuteAsync(null); }

    private async void AccelToggleStar_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ViewModel.ToggleStarredCommand.ExecuteAsync(null); }

    private async void AccelMarkAllRead_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await ViewModel.MarkAllReadCommand.ExecuteAsync(null); ViewModel.BuildSidebar(); BuildSidebarUI(); }

    private async void AccelInspector_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; await WinNewsWire.Dialogs.InspectorDialog.ShowAsync(XamlRoot, ViewModel.SelectedSidebarItem); }

    private async void AccelNewFeed_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; NewFeed_Click(s, new RoutedEventArgs()); await Task.CompletedTask; }

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

    /// <summary>Make a section heading row (Smart Feeds, On My PC, …) behave like
    /// a static title: no hover, no selection, no drag, no tab focus. We walk
    /// up to the owning <see cref="TreeViewItem"/> and disable hit-testing so
    /// the entire row — including the area where a chevron would have been —
    /// becomes inert.</summary>
    private void SidebarSectionHeader_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        DependencyObject? cur = fe;
        while (cur is not null and not TreeViewItem)
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
        if (cur is TreeViewItem tvi)
        {
            tvi.IsHitTestVisible = false;
            tvi.IsTabStop = false;
            tvi.CanDrag = false;
            tvi.AllowDrop = false;
        }
    }

    private static void UpdateTrimmedTooltip(TextBlock tb)
    {
        ToolTipService.SetToolTip(tb, tb.IsTextTrimmed ? tb.Text : null);
    }

    private void SidebarTreeView_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        if (args.Items is null || args.Items.Count == 0) return;
        foreach (var dragged in args.Items.OfType<WinNewsWire.Models.SidebarItem>())
        {
            if (dragged.Tag is not WinNewsWire.Account.Feed feed) continue;

            // Resolve the destination folder. When dropped on a folder node, NewParentItem
            // contains that SidebarItem; when dropped at the root (or onto a feed within
            // the same account), NewParentItem is the account header (SectionHeader) — in
            // that case we want the feed to be top-level (no folder).
            WinNewsWire.Account.Folder? destFolder = null;
            if (args.NewParentItem is WinNewsWire.Models.SidebarItem parent)
            {
                destFolder = parent.Tag as WinNewsWire.Account.Folder;
            }

            var account = WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
                .FirstOrDefault(a => a.AccountID == feed.AccountID);
            if (account is null) continue;

            try { account.MoveFeed(feed, destFolder); } catch { }
        }
        ViewModel.BuildSidebar();
        BuildSidebarUI();
    }

    // ---------- Public helpers consumed by MainWindow's MenuBar ----------

    public async Task NewFeedAsync() => NewFeed_Click(this, new RoutedEventArgs());
    public async Task NewFolderAsync() { NewFolder_Click(this, new RoutedEventArgs()); await Task.CompletedTask; }

    public async Task ToggleSelectedReadAsync() => await ViewModel.MarkSelectedReadCommand.ExecuteAsync(null);
    public async Task ToggleSelectedStarAsync() => await ViewModel.ToggleStarredCommand.ExecuteAsync(null);
    public void SelectNext() => ViewModel.NextArticleCommand.Execute(null);
    public void SelectPrev() => ViewModel.PreviousArticleCommand.Execute(null);

    public void SelectNextUnread()
    {
        var items = ViewModel.ArticleItems;
        if (items.Count == 0) return;
        int start = ViewModel.SelectedArticle is null ? 0 : items.IndexOf(ViewModel.SelectedArticle) + 1;
        for (int i = start; i < items.Count; i++) if (!items[i].IsRead) { ViewModel.SelectedArticle = items[i]; return; }
        for (int i = 0; i < start; i++) if (!items[i].IsRead) { ViewModel.SelectedArticle = items[i]; return; }
    }

    public void OpenSelectedInBrowser()
    {
        if (ViewModel.SelectedArticle is { Link: { Length: > 0 } link })
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(link));
    }

    public void CopySelectedLink()
    {
        if (ViewModel.SelectedArticle is { Link: { Length: > 0 } link }) CopyToClipboard(link);
    }

    public void ShareSelected() => ShareSelectedArticle();

    public async Task ShowInspectorAsync()
    {
        await WinNewsWire.Dialogs.InspectorDialog.ShowAsync(XamlRoot, ViewModel.SelectedSidebarItem);
        ViewModel.BuildSidebar();
        BuildSidebarUI();
    }

    public async Task RenameSelectedAsync()
    {
        // Inline rename: locate the TreeViewItem for the selected sidebar item and
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
        if (item is null) return false;
        return item.Tag is WinNewsWire.Account.Feed
            or WinNewsWire.Account.Folder
            or WinNewsWire.Account.Account;
    }

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

    private void SidebarRenameBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                CommitRename(GetSidebarItemFor(tb), save: true);
                e.Handled = true;
                return;
            case Windows.System.VirtualKey.Escape:
                CommitRename(GetSidebarItemFor(tb), save: false);
                e.Handled = true;
                return;

            // Caret-movement and selection keys that the TextBox handles
            // internally. Mark Handled so the routed event doesn't bubble
            // to the TreeView, which would otherwise steal Home/End/arrow
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

    private void SidebarRenameAccept_Click(object sender, RoutedEventArgs e)
        => CommitRename(GetSidebarItemFor(sender as DependencyObject), save: true);

    private void SidebarRenameCancel_Click(object sender, RoutedEventArgs e)
        => CommitRename(GetSidebarItemFor(sender as DependencyObject), save: false);

    /// <summary>Walks the visual tree from an event sender (inside the row template)
    /// up to the TreeViewItem and returns the SidebarItem it represents. The
    /// template's DataContext is the wrapping TreeViewNode, so we unwrap one level.</summary>
    private static WinNewsWire.Models.SidebarItem? GetSidebarItemFor(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement fe)
            {
                if (fe.DataContext is WinNewsWire.Models.SidebarItem direct) return direct;
                if (fe.DataContext is Microsoft.UI.Xaml.Controls.TreeViewNode node
                    && node.Content is WinNewsWire.Models.SidebarItem fromNode)
                    return fromNode;
            }
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void CommitRename(WinNewsWire.Models.SidebarItem? item, bool save)
    {
        if (item is null || !item.IsRenaming) return;

        if (save)
        {
            var newName = (item.EditableTitle ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(newName) && newName != item.Title)
            {
                switch (item.Tag)
                {
                    case WinNewsWire.Account.Feed feed:
                        feed.EditedName = newName;
                        WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
                            .FirstOrDefault(a => a.AccountID == feed.AccountID)?.SaveChanges();
                        break;
                    case WinNewsWire.Account.Folder folder:
                        folder.Name = newName;
                        WinNewsWire.AppRuntime.AppService.Shared.Accounts.Accounts
                            .FirstOrDefault(a => a.AccountID == folder.AccountID)?.SaveChanges();
                        break;
                    case WinNewsWire.Account.Account acc:
                        acc.Name = newName;
                        WinNewsWire.AppRuntime.AppService.Shared.Accounts.SaveAccountMeta(acc);
                        break;
                }
                item.Title = newName;
            }
        }

        item.IsRenaming = false;
        item.EditableTitle = string.Empty;
        // Accelerators are restored by LostFocus on the TextBox. We still call
        // Resume here as a safety net in case the TextBox was committed without
        // ever losing focus (e.g. programmatic commit while focus is elsewhere).
        ResumeSingleKeyAccelerators();
    }

    public async Task DeleteSelectedAsync()
    {
        if (ViewModel.SelectedSidebarItem is null) return;
        // Reuse the existing confirmation flow.
        CtxDelete_Click(this, new RoutedEventArgs());
        await Task.CompletedTask;
    }

    public async Task MarkAllReadInSelectedAsync()
    {
        await ViewModel.MarkAllReadCommand.ExecuteAsync(null);
        ViewModel.BuildSidebar();
        BuildSidebarUI();
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
