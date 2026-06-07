using System;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace WinNewsWire;

public sealed partial class MainWindow : Window
{
    private const double TitleCollapseThreshold = 700;
    private AppWindow? _appWindow;

    public MainWindow()
    {
        InitializeComponent();

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

        Title = "WinNewsWire";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));

        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "wnw-flat.ico");
            // AppWindow.SetIcon loads .ico files via a path that does not always
            // preserve the alpha channel — small/large icons end up rendered with
            // an opaque (often blue) backplate in the taskbar instead of being
            // transparent. Use Win32 LoadImage + WM_SETICON instead, which
            // honors LR_LOADTRANSPARENT and 32 bpp BMP / PNG ico entries.
            WinNewsWire.AppWindows.WindowIconHelper.SetWindowIconFromIco(hWnd, iconPath);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SetIcon failed: {ex.Message}"); }

        UpdateCaptionButtonWidth(appWindow);
        AppTitleBar.SizeChanged += AppTitleBar_SizeChanged;

        // The whole AppTitleBar is registered as the system title bar, which
        // means the framework treats clicks/double-clicks anywhere inside it
        // (including the search box) as title-bar gestures — so a double-click
        // inside the search box would maximize/restore the window instead of
        // selecting a word. Carve the search box out of the title bar by
        // marking its bounds as a Passthrough non-client region; pointer
        // input then routes to the AutoSuggestBox normally.
        TitleBarSearchBox.SizeChanged += (_, _) => UpdateSearchBoxPassthrough();
        TitleBarSearchBox.Loaded += (_, _) => UpdateSearchBoxPassthrough();
        AppTitleBar.SizeChanged += (_, _) => UpdateSearchBoxPassthrough();

        Activated += (_, _) => { PopulateThemesMenu(); SyncMenuToggles(); SyncAppearanceMenu(); };

        // Restore the saved appearance preference once the content tree is up.
        ((FrameworkElement)Content).Loaded += (_, _) => ApplyRequestedTheme();

        // Mirror programmatic clears of SearchQuery (e.g. when the user clicks
        // a different sidebar section) back into the title-bar search box, so
        // the visible text stays in sync with the filter state.
        MainContentControl.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainContentControl.ViewModel.SearchQuery) &&
                string.IsNullOrEmpty(MainContentControl.ViewModel.SearchQuery) &&
                !string.IsNullOrEmpty(TitleBarSearchBox.Text))
            {
                TitleBarSearchBox.Text = string.Empty;
            }
        };
    }

    private void UpdateSearchBoxPassthrough()
    {
        if (_appWindow is null || TitleBarSearchBox.XamlRoot is null) return;
        if (TitleBarSearchBox.ActualWidth <= 0 || TitleBarSearchBox.ActualHeight <= 0) return;
        try
        {
            var content = (UIElement)Content;
            var bounds = TitleBarSearchBox
                .TransformToVisual(content)
                .TransformBounds(new Windows.Foundation.Rect(
                    0, 0, TitleBarSearchBox.ActualWidth, TitleBarSearchBox.ActualHeight));
            var scale = TitleBarSearchBox.XamlRoot.RasterizationScale;
            var rect = new RectInt32(
                (int)Math.Floor(bounds.X * scale),
                (int)Math.Floor(bounds.Y * scale),
                (int)Math.Ceiling(bounds.Width * scale),
                (int)Math.Ceiling(bounds.Height * scale));
            var src = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            src.SetRegionRects(NonClientRegionKind.Passthrough, new[] { rect });
        }
        catch { /* layout not ready yet — next SizeChanged will retry */ }
    }

    internal void ShutdownContent()
    {
        try { MainContentControl?.Shutdown(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShutdownContent: {ex.Message}"); }
    }

    private void UpdateCaptionButtonWidth(AppWindow appWindow)
    {
        var titleBar = appWindow.TitleBar;
        if (titleBar != null)
        {
            var rightInset = titleBar.RightInset;
            if (rightInset > 0)
                AppTitleBar.Padding = new Thickness(16, 0, rightInset, 0);
        }
    }

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < TitleCollapseThreshold)
        {
            TitlePanel.Visibility = Visibility.Collapsed;
            TitleColumn.Width = new GridLength(0);
        }
        else
        {
            TitlePanel.Visibility = Visibility.Visible;
            TitleColumn.Width = GridLength.Auto;
        }
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

    // --- Classic handlers (reused from the pre-MenuBar flyout) ---

    private void Preferences_Click(object sender, RoutedEventArgs e)
        => new AppWindows.PreferencesWindow().Activate();

    private void About_Click(object sender, RoutedEventArgs e)
        => new AppWindows.AboutWindow().Activate();

    private void ErrorLog_Click(object sender, RoutedEventArgs e)
        => new AppWindows.ErrorLogWindow().Activate();

    private void KeyboardShortcuts_Click(object sender, RoutedEventArgs e)
        => new AppWindows.KeyboardShortcutsWindow().Activate();

    private async void ImportOpml_Click(object sender, RoutedEventArgs e)
        => await Dialogs.OpmlCommands.ImportAsync(WindowNative.GetWindowHandle(this));

    private async void ImportNnw3_Click(object sender, RoutedEventArgs e)
        => await Dialogs.OpmlCommands.ImportNnw3Async(WindowNative.GetWindowHandle(this));

    private async void ExportOpml_Click(object sender, RoutedEventArgs e)
        => await Dialogs.OpmlCommands.ExportAsync(WindowNative.GetWindowHandle(this));

    // --- MenuBar handlers (File / Edit / View / Article / Feed / Window / Help) ---

    private async void Menu_NewFeed(object sender, RoutedEventArgs e) => await MainContentControl.NewFeedAsync();
    private async void Menu_NewFolder(object sender, RoutedEventArgs e) => await MainContentControl.NewFolderAsync();
    private void Menu_CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void Menu_Quit(object sender, RoutedEventArgs e) => Microsoft.UI.Xaml.Application.Current.Exit();

    private void Menu_Undo(object sender, RoutedEventArgs e)
        => AppRuntime.AppService.Shared.Undo.Undo();
    private void Menu_Redo(object sender, RoutedEventArgs e)
        => AppRuntime.AppService.Shared.Undo.Redo();
    private void Menu_Cut(object sender, RoutedEventArgs e) { }
    private void Menu_Copy(object sender, RoutedEventArgs e) { }
    private void Menu_Paste(object sender, RoutedEventArgs e) { }
    private void Menu_SelectAll(object sender, RoutedEventArgs e) { }
    private void Menu_Find(object sender, RoutedEventArgs e) => TitleBarSearchBox.Focus(FocusState.Programmatic);

    private void Menu_ToggleUnified(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.IsUnifiedLayout = MenuUnifiedLayout.IsChecked;
    private void Menu_ToggleSidebar(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.IsSidebarVisible = MenuShowSidebar.IsChecked;
    private void Menu_ToggleReaderMode(object sender, RoutedEventArgs e)
        => MainContentControl.ViewModel.IsReaderMode = MenuReaderMode.IsChecked;
    private async void Menu_Refresh(object sender, RoutedEventArgs e)
        => await MainContentControl.ViewModel.ReloadFeedsCommand.ExecuteAsync(null);

    private async void Menu_InstallTheme(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".nnwtheme");
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var theme = AppShared.ArticleThemes.ArticleThemesManager.Shared.InstallFromZip(file.Path);
        if (theme is not null)
        {
            AppShared.ArticleThemes.ArticleThemesManager.Shared.CurrentTheme = theme;
            PopulateThemesMenu();
        }
    }

    private void Menu_ToggleRead(object sender, RoutedEventArgs e)
        => _ = MainContentControl.ToggleSelectedReadAsync();
    private void Menu_ToggleStar(object sender, RoutedEventArgs e)
        => _ = MainContentControl.ToggleSelectedStarAsync();
    private void Menu_NextUnread(object sender, RoutedEventArgs e)
        => MainContentControl.SelectNextUnread();
    private void Menu_Next(object sender, RoutedEventArgs e)
        => MainContentControl.SelectNext();
    private void Menu_Prev(object sender, RoutedEventArgs e)
        => MainContentControl.SelectPrev();
    private void Menu_OpenBrowser(object sender, RoutedEventArgs e)
        => MainContentControl.OpenSelectedInBrowser();
    private void Menu_CopyLink(object sender, RoutedEventArgs e)
        => MainContentControl.CopySelectedLink();
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

    private void Menu_Website(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(AppShared.HelpUrl.Website));

    private void Menu_ReleaseNotes(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(AppShared.HelpUrl.ReleaseNotes));

    private void Menu_HelpHome(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(AppShared.HelpUrl.HelpHome));

    private void Menu_GithubRepo(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(AppShared.HelpUrl.GithubRepo));

    private void Menu_BugTracker(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(AppShared.HelpUrl.BugTracker));

    private void Menu_Discourse(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(AppShared.HelpUrl.Discourse));

    private void Menu_PrivacyPolicy(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(AppShared.HelpUrl.PrivacyPolicy));

    // --- Theme submenu population + menu toggle sync ---

    private void PopulateThemesMenu()
    {
        if (MenuThemes is null) return;
        MenuThemes.Items.Clear();
        var mgr = AppShared.ArticleThemes.ArticleThemesManager.Shared;
        foreach (var t in mgr.Themes)
        {
            var theme = t;
            var item = new ToggleMenuFlyoutItem
            {
                Text = theme.Name + (theme.IsAppTheme ? "  (built-in)" : ""),
                IsChecked = theme.Name == mgr.CurrentTheme.Name,
            };
            item.Click += (_, _) =>
            {
                mgr.CurrentTheme = theme;
                PopulateThemesMenu();
                // Re-render the currently selected article with the new theme.
                MainContentControl.ViewModel.RefreshArticleHtml();
            };
            MenuThemes.Items.Add(item);
        }
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

    private void SyncMenuToggles()
    {
        var vm = MainContentControl.ViewModel;
        if (MenuUnifiedLayout is not null) MenuUnifiedLayout.IsChecked = vm.IsUnifiedLayout;
        if (MenuShowSidebar is not null) MenuShowSidebar.IsChecked = vm.IsSidebarVisible;
        if (MenuReaderMode is not null) MenuReaderMode.IsChecked = vm.IsReaderMode;
        UpdateMenuRenameEnabled();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.IsReaderMode)) MenuReaderMode.IsChecked = vm.IsReaderMode;
            if (args.PropertyName == nameof(vm.IsSidebarVisible)) MenuShowSidebar.IsChecked = vm.IsSidebarVisible;
            if (args.PropertyName == nameof(vm.IsUnifiedLayout)) MenuUnifiedLayout.IsChecked = vm.IsUnifiedLayout;
            if (args.PropertyName == nameof(vm.SelectedSidebarItem)) UpdateMenuRenameEnabled();
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
