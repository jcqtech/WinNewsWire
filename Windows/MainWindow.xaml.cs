using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.Toolbar;
using WinRT.Interop;

namespace WinNewsWire;

public sealed partial class MainWindow : Window
{
#if PARITY_PROBE
    internal MainContent ProbeContent => MainContentControl;
    internal int ProbeTitleBarRegionChecks { get; private set; }
    internal Func<Windows.Storage.StorageFolder, System.Threading.Tasks.Task<bool>>? ProbeOpenFolder { get; set; }
#endif
    private AppWindow? _appWindow;
    private bool _closed;
    private XamlRoot? _titleBarRoot;
    private Windows.Graphics.RectInt32[] _titleBarInputRects = [];
    private bool _titleBarInputErrorReported;
    private bool _titleBarInputDirty = true;
    internal System.Threading.Tasks.Task ContentInitialization => MainContentControl.Initialization;
    internal System.Collections.Generic.IEnumerable<WinNewsWire.Articles.Article> TimelineArticles
        => MainContentControl.ViewModel.ArticleItems.Select(item => item.SourceArticle).OfType<WinNewsWire.Articles.Article>();

    public MainWindow(Models.MainWindowState? state = null)
    {
        InitializeComponent();
        InitializeMenuOverflow();
        MenuSpeech.Text = AppShared.AppStrings.Get("Speech");
        MenuStartSpeaking.Text = AppShared.AppStrings.Get("StartSpeaking");
        MenuStopSpeaking.Text = AppShared.AppStrings.Get("StopSpeaking");
        MainContentControl.AttachTitleBarControls(TitleBarSearchBox, TitleBarSidebarHost);
        MainContentControl.ToolbarConfigurationChanged += (_, _) => SyncToolbarMenu();
        MainContentControl.NavigationHistoryChanged += (_, _) => NavigateBackButton.IsEnabled = MainContentControl.CanGoBack;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NavigateBackButton, AppStrings.Get("NavigateBack"));
        ToolTipService.SetToolTip(NavigateBackButton, AppStrings.Get("NavigateBack"));
        MenuOpenWebView.Text = ToolbarConfiguration.Label("openInWebView");
        SyncToolbarMenu();

        // VK_OEM_2 ('/') isn't a named VirtualKey enum member; WinUI's flyout
        // accelerator-text renderer throws when it encounters unknown keys,
        // which surfaces as 0xC000027B when the File menu opens. The XAML sets
        // KeyboardAcceleratorTextOverride so the displayed text bypasses that
        // lookup; we still register the accelerator here so Ctrl+/ works.
        var prefsAccelerator = new KeyboardAccelerator
        {
            Modifiers = VirtualKeyModifiers.Control,
            Key = (VirtualKey)191,
        };
        PreferencesMenuItem.KeyboardAccelerators.Add(prefsAccelerator);
        var fullScreenAccelerator = new KeyboardAccelerator { Key = VirtualKey.F11 };
        fullScreenAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            Menu_ToggleFullScreen(MenuFullScreen, new RoutedEventArgs());
        };
        ((UIElement)Content).KeyboardAccelerators.Add(fullScreenAccelerator);

        Title = "WinNewsWire";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        InitializeStatusBarSystemMessages(hWnd);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));

        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "wnw-flat.ico");
            // AppWindow.SetIcon loads .ico files via a path that does not always
            // preserve the alpha channel — small/large icons end up rendered with
            // an opaque (often blue) backplate in the taskbar instead of being
            // transparent. Use Win32 LoadImage + WM_SETICON instead, which
            // honors LR_LOADTRANSPARENT and 32 bpp BMP / PNG ico entries.
            WinNewsWire.AppWindows.WindowIconHelper.SetWindowIconFromIco(this, iconPath,
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "wnw.ico"));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.COMException)
        {
            App.LogException("Set main window icons", ex);
            MainContentControl.ViewModel.StatusMessage = "Could not set the window icons: " + ex.Message;
        }

        AppTitleBar.Loaded += TitleBar_Loaded;
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarLayout();
        AppTitleBar.LayoutUpdated += TitleBar_LayoutUpdated;
        foreach (var element in new FrameworkElement[] { NavigateBackButton, TitlePanel, TitleBarSearchBox, TitleBarSidebarHost })
            element.SizeChanged += (_, _) => _titleBarInputDirty = true;

        Activated += MainWindow_Activated;
        Core.AppDefaults.Shared.Changed += WindowDefaultsChanged;
        AppRuntime.AppService.Shared.Speech.StateChanged += SpeechStateChanged;
        Closed += MainWindow_Closed;

        // Restore the saved appearance preference once the content tree is up.
        ((FrameworkElement)Content).Loaded += (_, _) => ApplyRequestedTheme();

        // Feed changes clear search; Back can restore a previous search.
        MainContentControl.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainContentControl.ViewModel.SearchQuery) &&
                TitleBarSearchBox.Text != MainContentControl.ViewModel.SearchQuery)
            {
                TitleBarSearchBox.Text = MainContentControl.ViewModel.SearchQuery;
            }
        };
        InitializeRestoration(state);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        PopulateThemesMenu();
        PopulateWindowsMenu();
        SyncMenuToggles();
        SyncAppearanceMenu();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        StopMenuOverflowLayout();
        // Native Window events can retain their managed owner even after Close.
        Activated -= MainWindow_Activated;
        Closed -= MainWindow_Closed;
        AppTitleBar.LayoutUpdated -= TitleBar_LayoutUpdated;
        Core.AppDefaults.Shared.Changed -= WindowDefaultsChanged;
        AppRuntime.AppService.Shared.Speech.StateChanged -= SpeechStateChanged;
        if (_titleBarRoot is not null) _titleBarRoot.Changed -= TitleBarRootChanged;
    }

    internal async System.Threading.Tasks.Task ShutdownContentAsync()
    {
        try { await MainContentControl.ShutdownAsync(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            App.LogException("Main window content shutdown", error);
        }
    }

    internal void CloseAfterShutdown()
    {
        _closed = true;
        StopStatusBarSystemMessages();
        try
        {
            // Release native title-bar resources while the HWND still exists.
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, []);
            SetTitleBar(null);
            ExtendsContentIntoTitleBar = false;
            AppWindow.TitleBar.ResetToDefault();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            App.LogException("Release native title bar", error);
        }
        Close();
    }

    private void TitleBar_Loaded(object sender, RoutedEventArgs args)
    {
        if (_titleBarRoot is not null) _titleBarRoot.Changed -= TitleBarRootChanged;
        _titleBarRoot = AppTitleBar.XamlRoot;
        _titleBarRoot.Changed += TitleBarRootChanged;
        UpdateTitleBarLayout();
    }

    private void TitleBarRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdateMinimumWindowWidth();
        UpdateTitleBarLayout();
        QueueMenuOverflowLayout();
    }

    private void TitleBar_LayoutUpdated(object? sender, object args)
    {
        if (_titleBarInputDirty) UpdateTitleBarPassthrough();
    }

    private void UpdateTitleBarLayout()
    {
        if (_closed || _appWindow is null || AppTitleBar.XamlRoot is not { } root) return;
        double scale = root.RasterizationScale;
        var titleBar = _appWindow.TitleBar;
        double left = titleBar.LeftInset / scale;
        double right = titleBar.RightInset / scale;
        if (IsFullScreen) left = right = 0;
        else if (left == 0 && right == 0) right = 138;
        AppTitleBar.Padding = new Thickness(Math.Max(8, left + 8), 4, Math.Max(16, right + 8), 4);
        bool showTitle = AppTitleBar.ActualWidth >= 700;
        TitlePanel.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
        TitleColumn.Width = showTitle ? GridLength.Auto : new GridLength(0);
        _titleBarInputDirty = true;
    }

    private void UpdateTitleBarPassthrough()
    {
        if (_closed || _appWindow is null || AppTitleBar.XamlRoot is not { } root) return;
        _titleBarInputDirty = false;
#if PARITY_PROBE
        ProbeTitleBarRegionChecks++;
#endif
        var content = (UIElement)Content;
        var rectangles = new FrameworkElement[] { NavigateBackButton, TitleBarSearchBox, TitleBarSidebarHost }
            .Where(element => element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0)
            .Select(element => TitleBarInputRect(element.TransformToVisual(content)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight)), root.RasterizationScale)).ToArray();
        if (_titleBarInputRects.SequenceEqual(rectangles)) return;
        try
        {
            InputNonClientPointerSource.GetForWindowId(_appWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rectangles);
            _titleBarInputRects = rectangles;
            _titleBarInputErrorReported = false;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            if (_titleBarInputErrorReported) return;
            _titleBarInputErrorReported = true;
            App.LogException("Title bar input regions", error);
            MainContentControl.ViewModel.StatusMessage = "Could not configure title-bar input: " + error.Message;
        }
    }

    internal static Windows.Graphics.RectInt32 TitleBarInputRect(Windows.Foundation.Rect bounds, double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        int left = (int)Math.Floor(bounds.Left * scale), top = (int)Math.Floor(bounds.Top * scale);
        return new(left, top, (int)Math.Ceiling(bounds.Right * scale) - left, (int)Math.Ceiling(bounds.Bottom * scale) - top);
    }

    private void TitleBarSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            MainContentControl.ViewModel.SearchQuery = sender.Text;
    }

    private void TitleBarSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        MainContentControl.ViewModel.SearchQuery = args.QueryText ?? string.Empty;
    }

    private async void NavigateBackButton_Click(object sender, RoutedEventArgs args)
        => await MainContentControl.GoBackAsync();

    // --- Classic handlers (reused from the pre-MenuBar flyout) ---

    private void Preferences_Click(object sender, RoutedEventArgs e)
        => new AppWindows.PreferencesWindow().Activate();

    private void About_Click(object sender, RoutedEventArgs e)
        => new AppWindows.AboutWindow().Activate();

    private void ErrorLog_Click(object sender, RoutedEventArgs e)
        => new AppWindows.ErrorLogWindow().Activate();

    private void ActivityLog_Click(object sender, RoutedEventArgs e)
        => new AppWindows.ActivityLogWindow().Activate();
    private void CurrentActivity_Click(object sender, RoutedEventArgs e) => AppWindows.CurrentActivityWindow.Show();

    private void AccountStats_Click(object sender, RoutedEventArgs e) => AppWindows.AccountStatsWindow.Show();

    private void KeyboardShortcuts_Click(object sender, RoutedEventArgs e)
        => new AppWindows.KeyboardShortcutsWindow().Activate();

    private async void ImportOpml_Click(object sender, RoutedEventArgs e)
        => await Dialogs.OpmlCommands.ImportAsync(WindowNative.GetWindowHandle(this), MainContentControl.XamlRoot);

    private async void ImportNnw3_Click(object sender, RoutedEventArgs e)
        => await Dialogs.OpmlCommands.ImportNnw3Async(WindowNative.GetWindowHandle(this), MainContentControl.XamlRoot);

    private async void ExportOpml_Click(object sender, RoutedEventArgs e)
        => await Dialogs.OpmlCommands.ExportAsync(WindowNative.GetWindowHandle(this), MainContentControl.XamlRoot);

    // --- MenuBar handlers (File / Edit / View / Article / Feed / Window / Help) ---

    private async void Menu_NewFeed(object sender, RoutedEventArgs e) => await MainContentControl.NewFeedAsync();
    private async void Menu_NewFolder(object sender, RoutedEventArgs e) => await MainContentControl.NewFolderAsync();
    private void Dinosaurs_Click(object sender, RoutedEventArgs e) => AppWindows.DinosaursWindow.Show();
    private void Menu_NewWindow(object sender, RoutedEventArgs e)
    {
        if (!App.IsShuttingDown) App.OpenMainWindow();
    }
    private async void Menu_CloseWindow(object sender, RoutedEventArgs e) => await App.CloseMainWindowAsync(this);
    private async void Menu_Quit(object sender, RoutedEventArgs e) => await App.QuitAsync();

    private void PopulateWindowsMenu()
    {
        OpenWindowsMenu.Items.Clear();
        int index = 0;
        foreach (var window in App.MainWindows)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = $"{++index}: {window.MainContentControl.ViewModel.ArticleListTitle}",
                IsChecked = ReferenceEquals(window, this)
            };
            item.Click += (_, _) => window.Activate();
            OpenWindowsMenu.Items.Add(item);
        }
        RefreshMenuOverflowContents();
    }

    private void Menu_BringAllToFront(object sender, RoutedEventArgs e)
    {
        foreach (var window in App.MainWindows.Where(window => !ReferenceEquals(window, this))) window.Activate();
        foreach (var window in AppWindows.WindowThemeHelper.OpenSecondaryWindows) window.Activate();
        Activate();
    }

    private void WindowDefaultsChanged(object? sender, string key)
    {
        if (_closed) return;
        if (key == Core.AppDefaults.Key.AppearanceMode)
        {
            if (DispatcherQueue.HasThreadAccess) ApplyRequestedTheme();
            else DispatcherQueue.TryEnqueue(ApplyRequestedTheme);
        }
        else if (key is Core.AppDefaults.Key.OpenInBrowserInBackground or Core.AppDefaults.Key.ShowStatusBar
            or Core.AppDefaults.Key.ShowStatusBarThemeMenu)
        {
            if (DispatcherQueue.HasThreadAccess) SyncMenuToggles();
            else DispatcherQueue.TryEnqueue(SyncMenuToggles);
        }
    }

    private async void Menu_Undo(object sender, RoutedEventArgs e)
        => await MainContentControl.ViewModel.UndoAsync();
    private async void Menu_Redo(object sender, RoutedEventArgs e)
        => await MainContentControl.ViewModel.RedoAsync();
    private void Menu_Cut(object sender, RoutedEventArgs e) => RouteClipboardCommand(ClipboardCmd.Cut);
    private void Menu_Copy(object sender, RoutedEventArgs e) => RouteClipboardCommand(ClipboardCmd.Copy);
    private void Menu_Paste(object sender, RoutedEventArgs e) => RouteClipboardCommand(ClipboardCmd.Paste);
    private void Menu_SelectAll(object sender, RoutedEventArgs e) => RouteClipboardCommand(ClipboardCmd.SelectAll);

    private enum ClipboardCmd { Cut, Copy, Paste, SelectAll }

    /// <summary>Route the Edit-menu clipboard command to whichever text-bearing control
    /// currently has focus. WinUI doesn't promote these commands automatically the way
    /// WPF's <c>ApplicationCommands.Cut</c> does, so we walk to the focused element and
    /// invoke the appropriate method directly. Falls back to a no-op when focus is on a
    /// non-text control (so the menu item still works as a discoverability hook).</summary>
    private void RouteClipboardCommand(ClipboardCmd cmd)
    {
        if (Content is not FrameworkElement root) return;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root.XamlRoot);
        switch (focused)
        {
            case TextBox tb:
                switch (cmd)
                {
                    case ClipboardCmd.Cut: tb.CutSelectionToClipboard(); break;
                    case ClipboardCmd.Copy: tb.CopySelectionToClipboard(); break;
                    case ClipboardCmd.Paste: tb.PasteFromClipboard(); break;
                    case ClipboardCmd.SelectAll: tb.SelectAll(); break;
                }
                break;
            case RichEditBox reb:
                switch (cmd)
                {
                    case ClipboardCmd.Cut: reb.Document.Selection.Cut(); break;
                    case ClipboardCmd.Copy: reb.Document.Selection.Copy(); break;
                    case ClipboardCmd.Paste: reb.Document.Selection.Paste(0); break;
                    case ClipboardCmd.SelectAll:
                        reb.Document.Selection.SetRange(0, int.MaxValue); break;
                }
                break;
            case PasswordBox pb:
                if (cmd == ClipboardCmd.Paste) pb.PasteFromClipboard();
                break;
            case AutoSuggestBox asb:
                // AutoSuggestBox wraps an inner TextBox; the focused element when
                // typing is the TextBox itself so the TextBox arm above usually
                // handles it. If somehow the ASB is the focused element, fall back.
                if (cmd == ClipboardCmd.SelectAll)
                    asb.Text = asb.Text; // no-op; selection lives on inner TextBox
                break;
            case WebView2 wv:
                // Forward to the WebView2's underlying webpage so reader-view text
                // selection can be copied. ExecuteScriptAsync is fire-and-forget.
                if (cmd == ClipboardCmd.Copy)
                    _ = wv.ExecuteScriptAsync("document.execCommand('copy')");
                else if (cmd == ClipboardCmd.SelectAll)
                    _ = wv.ExecuteScriptAsync("document.execCommand('selectAll')");
                break;
            default:
                if (cmd == ClipboardCmd.SelectAll) MainContentControl.TrySelectAllArticles();
                else if (cmd == ClipboardCmd.Copy) MainContentControl.TryCopySelectedArticles();
                break;
        }
    }

    internal void FocusArticleSearch() => MainContentControl.FocusToolbarSearch();
    private void Menu_ArticleSearch(object sender, RoutedEventArgs e) => FocusArticleSearch();
    private async void Menu_Find(object sender, RoutedEventArgs e) => await MainContentControl.ShowArticleFindAsync();
    private async void Menu_FindNext(object sender, RoutedEventArgs e) => await MainContentControl.FindNextInArticleAsync();
    private async void Menu_FindPrevious(object sender, RoutedEventArgs e) => await MainContentControl.FindNextInArticleAsync(backwards: true);
    private async void Menu_UseSelectionFind(object sender, RoutedEventArgs e) => await MainContentControl.UseArticleSelectionForFindAsync();
    private async void Menu_JumpToSelection(object sender, RoutedEventArgs e) => await MainContentControl.JumpToArticleSelectionAsync();
    private async void Menu_StartSpeaking(object sender, RoutedEventArgs e) => await MainContentControl.StartSpeakingAsync();
    private async void Menu_StopSpeaking(object sender, RoutedEventArgs e) => await AppRuntime.AppService.Shared.Speech.StopAsync();
    private void SpeechStateChanged(object? sender, EventArgs args)
    {
        if (!_closed) SyncMenuToggles();
    }

    private void Menu_ToggleUnified(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.IsUnifiedLayout = MenuUnifiedLayout.IsChecked;
    private void Menu_ToggleToolbar(object sender, RoutedEventArgs e) => MainContentControl.ToggleToolbar();
    private async void Menu_CustomizeToolbar(object sender, RoutedEventArgs e) => await MainContentControl.CustomizeToolbarAsync();
    private void Menu_ToolbarDisplay(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string value } && Enum.TryParse<ToolbarDisplayMode>(value, out var mode))
            MainContentControl.SaveToolbarConfiguration(MainContentControl.CurrentToolbarConfiguration.WithDisplayMode(mode));
        SyncToolbarMenu();
    }

    private void SyncToolbarMenu()
    {
        var configuration = MainContentControl.CurrentToolbarConfiguration;
        MenuShowToolbar.Text = AppStrings.Get(configuration.IsVisible ? "HideToolbar" : "ShowToolbar");
        MenuCustomizeToolbar.Text = AppStrings.Get("CustomizeToolbar");
        MenuToolbarDisplay.Text = AppStrings.Get("ToolbarDisplayMode");
        foreach (var item in new[] { MenuToolbarIcons, MenuToolbarIconsAndText, MenuToolbarText })
        {
            var mode = Enum.Parse<ToolbarDisplayMode>((string)item.Tag);
            item.Text = AppStrings.Get("ToolbarDisplay_" + mode);
            item.IsChecked = configuration.DisplayMode == mode;
        }
    }
    private void Menu_ToggleFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    internal void ToggleFullScreen()
    {
        try { SetFullScreen(!IsFullScreen); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ReportStateError("Change full-screen presentation", error);
            SyncMenuToggles();
        }
    }
    private void Menu_ToggleColumnLayout(object sender, RoutedEventArgs e)
    {
        try
        {
            Core.AppDefaults.Shared.UseColumnLayout = MenuColumnLayout.IsChecked;
            MainContentControl.ViewModel.IsUnifiedLayout = false;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            ReportStateError("Save timeline layout", error);
            SyncMenuToggles();
        }
    }
    private void Menu_ToggleSidebar(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.IsSidebarVisible = MenuShowSidebar.IsChecked;

    private void Menu_ToggleLargeThumbnails(object sender, RoutedEventArgs e)
    {
        try { Core.AppDefaults.Shared.TimelineShowsLargeThumbnails = MenuLargeThumbnails.IsChecked; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            ReportStateError("Save timeline thumbnail mode", error);
            SyncMenuToggles();
        }
    }

    private void Menu_ToggleStatusBar(object sender, RoutedEventArgs e)
    {
        MainContentControl.SetStatusBarVisible(MenuShowStatusBar.IsChecked);
        SyncMenuToggles();
    }

    private void Menu_ToggleStatusBarThemes(object sender, RoutedEventArgs e)
    {
        MainContentControl.SetStatusBarThemeMenuVisible(MenuShowStatusBarThemes.IsChecked);
        SyncMenuToggles();
    }
    private void Menu_ToggleReaderMode(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.IsReaderMode = MenuReaderMode.IsChecked;
    private async void Menu_Refresh(object sender, RoutedEventArgs e)
        => await MainContentControl.ViewModel.ReloadFeedsCommand.ExecuteAsync(null);

    private void Menu_SortBy(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string key })
        {
            var sortKey = Enum.Parse<ArticleSortKey>(key);
            SetSortParameters(new(sortKey, sortKey == MainContentControl.ViewModel.SortParameters.Key
                ? MainContentControl.ViewModel.SortParameters.Descending : !sortKey.SortsAscendingFirst()));
        }
        SyncMenuToggles();
    }

    private void Menu_SortDirection(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: string direction })
            SetSortParameters(new(MainContentControl.ViewModel.SortParameters.Key, direction != "ascending"));
        SyncMenuToggles();
    }

    private void SetSortParameters(ArticleSortParameters parameters)
    {
        try { MainContentControl.ViewModel.SetSortParameters(parameters); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ReportStateError("Save sorting preference", ex);
        }
    }

    private async void Menu_ToggleReadFilter(object sender, RoutedEventArgs e)
    {
        await MainContentControl.ToggleToolbarReadFilterAsync();
        SyncMenuToggles();
    }

    private void Menu_ToggleSidebarReadFilter(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.IsSidebarReadFiltered = !MainContentControl.ViewModel.IsSidebarReadFiltered;

    private void Menu_SearchAllAccounts(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.SearchAllAccounts = MenuSearchAllAccounts.IsChecked;

    private async void Menu_InstallTheme(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".nnwtheme");
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await MainContentControl.ViewModel.RunSidebarActionAsync("Install article theme", async () =>
        {
            await InstallThemeAsync(file.Path, null);
        });
    }

    private async void Menu_InstallThemeFromUrl(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { Header = "Theme archive URL", PlaceholderText = "https://example.com/theme.zip", MinWidth = 360 };
        var dialog = new ContentDialog
        {
            Title = "Install Article Theme",
            Content = input,
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = MainContentControl.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await MainContentControl.ViewModel.RunSidebarActionAsync("Download article theme", async () =>
        {
            if (!Uri.TryCreate(input.Text.Trim(), UriKind.Absolute, out var url))
                throw new ArgumentException("Enter an absolute HTTP or HTTPS theme URL.");
            await InstallThemeAsync(null, url);
        });
    }

    private void Menu_ToggleRead(object sender, RoutedEventArgs e)
        => _ = MainContentControl.ToggleSelectedReadAsync();
    private void Menu_ToggleStar(object sender, RoutedEventArgs e)
        => _ = MainContentControl.ToggleSelectedStarAsync();
    private async void Menu_MarkAboveRead(object sender, RoutedEventArgs e)
    {
        if (MainContentControl.ViewModel.SelectedArticles.FirstOrDefault() is { } article)
            await MainContentControl.ViewModel.MarkAboveAsReadAsync(article);
    }
    private async void Menu_MarkBelowRead(object sender, RoutedEventArgs e)
    {
        if (MainContentControl.ViewModel.SelectedArticles.LastOrDefault() is { } article)
            await MainContentControl.ViewModel.MarkBelowAsReadAsync(article);
    }
    private async void Menu_MarkOlderRead(object sender, RoutedEventArgs e)
        => await MainContentControl.ViewModel.MarkOlderArticlesReadCommand.ExecuteAsync(null);
    private void Menu_CleanUpRead(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.CleanUpReadArticlesCommand.Execute(null);
    private async void Menu_NextUnread(object sender, RoutedEventArgs e)
        => await MainContentControl.SelectNextUnreadAsync();
    private async void Menu_GoToday(object sender, RoutedEventArgs e)
        => await MainContentControl.GoToSmartFeedAsync(AppShared.SmartFeeds.SmartFeedsController.Shared.TodayFeed);
    private async void Menu_GoUnread(object sender, RoutedEventArgs e)
        => await MainContentControl.GoToSmartFeedAsync(AppShared.SmartFeeds.SmartFeedsController.Shared.UnreadFeed);
    private async void Menu_GoStarred(object sender, RoutedEventArgs e)
        => await MainContentControl.GoToSmartFeedAsync(AppShared.SmartFeeds.SmartFeedsController.Shared.StarredFeed);
    private void Menu_Next(object sender, RoutedEventArgs e)
        => MainContentControl.SelectNext();
    private void Menu_Prev(object sender, RoutedEventArgs e)
        => MainContentControl.SelectPrev();
    private void Menu_OpenBrowser(object sender, RoutedEventArgs e)
        => MainContentControl.OpenSelectedInBrowser();
    private async void Menu_OpenWebView(object sender, RoutedEventArgs e)
        => await MainContentControl.OpenSelectedInWebViewAsync();
    private void Menu_OpenBrowserInverted(object sender, RoutedEventArgs e)
        => MainContentControl.OpenSelectedInBrowser(invertPreference: true);
    private void Menu_CopyLink(object sender, RoutedEventArgs e)
        => MainContentControl.CopySelectedLink();
    private void Menu_CopyExternalLink(object sender, RoutedEventArgs e)
        => MainContentControl.CopySelectedLink(external: true);
    private void Menu_Share(object sender, RoutedEventArgs e)
        => MainContentControl.ShareSelected();

    private async void Menu_GetInfo(object sender, RoutedEventArgs e)
        => await MainContentControl.ShowInspectorAsync();
    private async void Menu_Rename(object sender, RoutedEventArgs e)
        => await MainContentControl.RenameSelectedAsync();
    private async void Menu_Delete(object sender, RoutedEventArgs e)
        => await MainContentControl.DeleteSelectedAsync();
    private async void Menu_MarkAllRead(object sender, RoutedEventArgs e)
        => await MainContentControl.MarkAllReadInSelectedAsync();

    private void Menu_Minimize(object sender, RoutedEventArgs e)
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        (appWindow.Presenter as OverlappedPresenter)?.Minimize();
    }

    private void Menu_Zoom(object sender, RoutedEventArgs e)
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        (appWindow.Presenter as OverlappedPresenter)?.Maximize();
    }

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        // Capture a screenshot of the main window's content tree before the
        // feedback form opens, so the user sees the state they were reacting
        // to — not the empty feedback dialog. WebView2 surfaces don't render
        // into RenderTargetBitmap, so the article body shows as a blank
        // rectangle; that's an accepted trade-off for staying inside the
        // managed UI thread without spinning up a Win32 GDI capture.
        Windows.Graphics.Imaging.SoftwareBitmap? screenshot = null;
        try
        {
            if (Content is FrameworkElement root)
            {
                var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await rtb.RenderAsync(root);
                var buffer = await rtb.GetPixelsAsync();
                screenshot = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
                    buffer,
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    rtb.PixelWidth,
                    rtb.PixelHeight,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Screenshot failed: {ex.Message}"); }

        string debug;
        try { debug = AppWindows.FeedbackDebugCollector.Collect(); }
        catch (Exception ex) { debug = $"<debug-collection failed: {ex.Message}>"; }

        new AppWindows.FeedbackWindow(screenshot, debug).Activate();
    }

    private async void Menu_Website(object sender, RoutedEventArgs e)
        => await OpenHelpUriAsync(AppShared.HelpUrl.Website);

    private async void Menu_OpenSupportFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Core.AppConfig.DataDirectory);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Core.AppConfig.DataDirectory);
            bool opened;
#if PARITY_PROBE
            if (ProbeOpenFolder is { } open) opened = await open(folder);
            else
#endif
                opened = await Windows.System.Launcher.LaunchFolderAsync(folder);
            if (!opened) throw new InvalidOperationException("Windows could not open the application support folder.");
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            MainContentControl.ViewModel.StatusMessage = $"Could not open the application support folder: {error.Message}";
            await AppRuntime.AppService.Shared.LogAsync("Open application support folder", error);
        }
    }

    private async void Menu_HowToSupport(object sender, RoutedEventArgs e)
        => await OpenHelpUriAsync(AppShared.HelpUrl.HowToSupport);

    private async void Menu_ReleaseNotes(object sender, RoutedEventArgs e)
        => await OpenHelpUriAsync(AppShared.HelpUrl.ReleaseNotes);

    private async void Menu_HelpHome(object sender, RoutedEventArgs e)
        => await OpenHelpUriAsync(AppShared.HelpUrl.HelpHome);

    private async void Menu_GithubRepo(object sender, RoutedEventArgs e)
        => await OpenHelpUriAsync(AppShared.HelpUrl.GithubRepo);

    private async void Menu_BugTracker(object sender, RoutedEventArgs e)
        => await OpenHelpUriAsync(AppShared.HelpUrl.BugTracker);

    private async void Menu_PrivacyPolicy(object sender, RoutedEventArgs e)
        => await OpenHelpUriAsync(AppShared.HelpUrl.PrivacyPolicy);

    private async System.Threading.Tasks.Task OpenHelpUriAsync(string url)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(url)))
                throw new InvalidOperationException("Windows could not open the link.");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException)
        {
            MainContentControl.ViewModel.StatusMessage = $"Could not open the help link: {error.Message}";
            await AppRuntime.AppService.Shared.LogAsync("Open help link", error);
        }
    }

    // --- Theme submenu population + menu toggle sync ---

    private void PopulateThemesMenu()
    {
        if (MenuThemes is null) return;
        MainContentControl.PopulateArticleThemeItems(MenuThemes.Items);
        RefreshMenuOverflowContents();
    }

    private void Menu_AppearanceSystem(object sender, RoutedEventArgs e)
        => ApplyAppearance(WinNewsWire.Core.AppDefaults.Appearance.System);
    private void Menu_AppearanceLight(object sender, RoutedEventArgs e)
        => ApplyAppearance(WinNewsWire.Core.AppDefaults.Appearance.Light);
    private void Menu_AppearanceDark(object sender, RoutedEventArgs e)
        => ApplyAppearance(WinNewsWire.Core.AppDefaults.Appearance.Dark);

    /// <summary>Persist the user's appearance choice, push it onto the WinUI root,
    /// and tell <see cref="MainContent"/> to re-render the visible article so the
    /// WebView2 picks up the new <c>prefers-color-scheme</c>.</summary>
    private void ApplyAppearance(WinNewsWire.Core.AppDefaults.Appearance mode)
    {
        WinNewsWire.Core.AppDefaults.Shared.AppearanceMode = mode;
        ApplyRequestedTheme();
        SyncAppearanceMenu();
        MainContentControl?.ApplyWebViewColorScheme();
        MainContentControl?.ViewModel?.RefreshArticleHtml();
    }

    private void ApplyRequestedTheme()
    {
        if (_closed) return;
        // RequestedTheme cascades from the root FrameworkElement to every child,
        // so setting it on Window.Content recolors the whole UI (menu bar, sidebar,
        // article list, dialogs) without re-instantiating anything.
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = WinNewsWire.Core.AppDefaults.Shared.AppearanceMode switch
            {
                WinNewsWire.Core.AppDefaults.Appearance.Light => ElementTheme.Light,
                WinNewsWire.Core.AppDefaults.Appearance.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            // ActualTheme only resolves once the element is in the tree, so we
            // listen for changes to recolor caption buttons whenever Windows'
            // system theme flips (relevant when AppearanceMode == System).
            if (!_actualThemeChangedHooked)
            {
                root.ActualThemeChanged += (_, _) => ApplyCaptionButtonColors();
                _actualThemeChangedHooked = true;
            }
        }
        ApplyCaptionButtonColors();
    }

    private bool _actualThemeChangedHooked;

    /// <summary>
    /// Paint the min/max/close caption glyphs so they contrast against the
    /// Mica backdrop. When the app uses dark mode but Windows itself is set
    /// to light (or vice versa), the framework's default caption colors come
    /// from the system theme and can disappear into the backdrop — this
    /// pushes explicit colors based on the effective app theme instead.
    /// </summary>
    private void ApplyCaptionButtonColors()
    {
        if (_appWindow?.TitleBar is null) return;

        bool isDark;
        var mode = WinNewsWire.Core.AppDefaults.Shared.AppearanceMode;
        if (mode == WinNewsWire.Core.AppDefaults.Appearance.Dark) isDark = true;
        else if (mode == WinNewsWire.Core.AppDefaults.Appearance.Light) isDark = false;
        else if (Content is FrameworkElement fe && fe.ActualTheme != ElementTheme.Default)
            isDark = fe.ActualTheme == ElementTheme.Dark;
        else
            isDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;

        var titleBar = _appWindow.TitleBar;
        // Keep button cells transparent so the Mica backdrop continues to
        // show through (matches the look of every other WinUI app).
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        if (isDark)
        {
            var fg = Microsoft.UI.Colors.White;
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedForegroundColor = fg;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0x9A, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverBackgroundColor   = Microsoft.UI.ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x44, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            var fg = Microsoft.UI.Colors.Black;
            titleBar.ButtonForegroundColor = fg;
            titleBar.ButtonHoverForegroundColor = fg;
            titleBar.ButtonPressedForegroundColor = fg;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(0x9A, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverBackgroundColor   = Microsoft.UI.ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(0x30, 0x00, 0x00, 0x00);
        }
    }

    private void SyncAppearanceMenu()
    {
        var mode = WinNewsWire.Core.AppDefaults.Shared.AppearanceMode;
        if (MenuAppearanceSystem is not null)
            MenuAppearanceSystem.IsChecked = mode == WinNewsWire.Core.AppDefaults.Appearance.System;
        if (MenuAppearanceLight is not null)
            MenuAppearanceLight.IsChecked = mode == WinNewsWire.Core.AppDefaults.Appearance.Light;
        if (MenuAppearanceDark is not null)
            MenuAppearanceDark.IsChecked = mode == WinNewsWire.Core.AppDefaults.Appearance.Dark;
    }

    private bool _menuTogglesHooked;

    internal void UpdateRefreshAvailability()
    {
        if (_closed) return;
        MenuRefreshAll.IsEnabled = MenuRefresh.IsEnabled =
            !MainContentControl.ViewModel.IsLoading && AppRuntime.AppService.Shared.Accounts.CanRefresh;
    }

    private void SyncMenuToggles()
    {
        var vm = MainContentControl.ViewModel;
        UpdateRefreshAvailability();
        MenuShowStatusBar.Text = AppStrings.Get("StatusBarShow");
        MenuShowStatusBar.IsChecked = Core.AppDefaults.Shared.ShowStatusBar;
        MenuShowStatusBarThemes.Text = AppStrings.Get("StatusBarShowThemes");
        MenuShowStatusBarThemes.IsChecked = Core.AppDefaults.Shared.ShowStatusBarThemeMenu;
        if (MenuUnifiedLayout is not null) MenuUnifiedLayout.IsChecked = vm.IsUnifiedLayout;
        MenuFullScreen.IsChecked = IsFullScreen;
        MenuColumnLayout.IsChecked = vm.UseColumnLayout;
        MenuLargeThumbnails.Text = AppStrings.Get("TimelineShowLargeThumbnails");
        MenuLargeThumbnails.IsChecked = vm.TimelineShowsLargeThumbnails;
        MenuLargeThumbnails.IsEnabled = !vm.UseColumnLayout;
        if (MenuShowSidebar is not null) MenuShowSidebar.IsChecked = vm.IsSidebarVisible;
        if (MenuReaderMode is not null) MenuReaderMode.IsChecked = vm.IsReaderMode;
        MenuSortDate.IsChecked = vm.SortParameters.Key == ArticleSortKey.Date;
        MenuSortFeed.IsChecked = vm.SortParameters.Key == ArticleSortKey.Feed;
        MenuSortTitle.IsChecked = vm.SortParameters.Key == ArticleSortKey.Title;
        MenuSortUnread.IsChecked = vm.SortParameters.Key == ArticleSortKey.Unread;
        MenuSortStarred.IsChecked = vm.SortParameters.Key == ArticleSortKey.Starred;
        MenuSortAscending.IsChecked = !vm.SortParameters.Descending;
        MenuSortDescending.IsChecked = vm.SortParameters.Descending;
        MenuHideRead.IsChecked = vm.IsReadFiltered;
        MenuReadFeedsFilter.Text = vm.SidebarReadFilterLabel;
        MenuHideRead.IsEnabled = vm.CanToggleReadFilter;
        MenuSearchAllAccounts.IsChecked = vm.SearchAllAccounts;
        MenuOpenBrowserInverted.Text = Browser.InvertedOpenTitle;
        MenuOpenWebView.IsEnabled = MainContentControl.CanOpenSelectedInWebView;
        MenuGoNextUnread.IsEnabled = MainContentControl.CanSelectNextUnread;
        MenuArticleNextUnread.IsEnabled = MenuGoNextUnread.IsEnabled;
        MenuFindArticle.IsEnabled = MenuFindNext.IsEnabled = MenuFindPrevious.IsEnabled
            = MenuUseSelectionFind.IsEnabled = MenuJumpToSelection.IsEnabled = MainContentControl.HasDisplayedDocument;
        MenuStartSpeaking.IsEnabled = MainContentControl.HasDisplayedDocument;
        MenuStopSpeaking.IsEnabled = AppRuntime.AppService.Shared.Speech.IsSpeaking;
        MenuCopyArticleUrl.IsEnabled = MainContentControl.CanCopySelectedLink();
        MenuCopyExternalUrl.IsEnabled = MainContentControl.CanCopySelectedLink(external: true);
        MenuArticleCopyLink.IsEnabled = MenuCopyArticleUrl.IsEnabled;
        MenuUndo.IsEnabled = vm.CanUndo;
        MenuRedo.IsEnabled = vm.CanRedo;
        MenuUndo.Text = vm.UndoTitle;
        MenuRedo.Text = vm.RedoTitle;
        MenuDeleteItem.IsEnabled = MainContentControl.CanDeleteSidebarSelection;
        MenuInfoItem.IsEnabled = vm.SelectedSidebarItem is not null;
        UpdateMenuRenameEnabled();
        if (_menuTogglesHooked) return;
        _menuTogglesHooked = true;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(vm.SortParameters) or nameof(vm.IsReaderMode) or nameof(vm.IsSidebarVisible)
                or nameof(vm.IsUnifiedLayout) or nameof(vm.UseColumnLayout) or nameof(vm.TimelineShowsLargeThumbnails)
                or nameof(vm.SelectedSidebarItem) or nameof(vm.SelectedSidebarItems)
                or nameof(vm.IsReadFiltered) or nameof(vm.IsSidebarReadFiltered) or nameof(vm.CanToggleReadFilter) or nameof(vm.SearchAllAccounts)
                or nameof(vm.SelectedArticles) or nameof(vm.SelectedArticle)
                or nameof(vm.ArticleListUnreadCount) or nameof(vm.ArticleItems)
                or nameof(vm.CanUndo) or nameof(vm.CanRedo) or nameof(vm.UndoTitle) or nameof(vm.RedoTitle) or nameof(vm.IsLoading))
                SyncMenuToggles();
        };
    }

    private void UpdateMenuRenameEnabled()
    {
        // Feed > Rename… is only meaningful for items backed by a Feed, Folder, or
        // Account. Smart feeds and section headers can't be renamed, so we gray it
        // out — mirroring NetNewsWire Mac's NSMenuItem.isEnabled behavior.
        if (MenuRenameItem is null) return;
        MenuRenameItem.IsEnabled =
            MainContent.CanRenameSidebarItem(MainContentControl.ViewModel.SelectedSidebarItem);
    }
}
