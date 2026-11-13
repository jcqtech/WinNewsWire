using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.AppShared.StatusBar;
using WinNewsWire.Core;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private int _statusBarUpdateQueued;
    private string _statusBarLink = "";
    private int _statusBarClockInvalidated;
    private DispatcherQueueTimer? _statusBarMidnightTimer;

    private void InitializeStatusBar()
    {
        AutomationProperties.SetName(StatusBarSurface, AppStrings.Get("StatusBar"));
        AutomationProperties.SetName(StatusBarGearButton, AppStrings.Get("StatusBarGear"));
        AutomationProperties.SetHelpText(StatusBarCountButton, AppStrings.Get("StatusBarCountHelp"));
        AutomationProperties.SetName(StatusBarThemeButton, AppStrings.Get("Toolbar_articleThemeMenu"));
        CombinedRefreshProgress.Shared.ProgressChanged += StatusBarProgressChanged;
        AppDefaults.Shared.Changed += StatusBarDefaultsChanged;
        ArticleThemesManager.Shared.CurrentThemeChanged += StatusBarThemeChanged;
        SidebarPanel.SizeChanged += StatusBarSidebar_SizeChanged;
        _statusBarMidnightTimer = DispatcherQueue.CreateTimer();
        _statusBarMidnightTimer.IsRepeating = false;
        _statusBarMidnightTimer.Tick += StatusBarMidnight;
        ScheduleStatusBarMidnight();
        QueueStatusBarUpdate();
    }

    private void DisposeStatusBar()
    {
        CombinedRefreshProgress.Shared.ProgressChanged -= StatusBarProgressChanged;
        AppDefaults.Shared.Changed -= StatusBarDefaultsChanged;
        ArticleThemesManager.Shared.CurrentThemeChanged -= StatusBarThemeChanged;
        SidebarPanel.SizeChanged -= StatusBarSidebar_SizeChanged;
        if (_statusBarMidnightTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= StatusBarMidnight;
            _statusBarMidnightTimer = null;
        }
    }

    private void StatusBarMidnight(DispatcherQueueTimer sender, object args) => InvalidateStatusBarClock();

    internal void InvalidateStatusBarClock()
    {
        Interlocked.Exchange(ref _statusBarClockInvalidated, 1);
        QueueStatusBarUpdate();
    }

    private void ScheduleStatusBarMidnight()
    {
        if (_statusBarMidnightTimer is not { } timer) return;
        var delay = new DateTimeOffset(DateTime.Today.AddDays(1)) - DateTimeOffset.Now;
        timer.Stop();
        timer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
        timer.Start();
    }

    private void StatusBarProgressChanged(object? sender, ProgressInfo progress) => QueueStatusBarUpdate();
    private void StatusBarThemeChanged(object? sender, EventArgs args) => QueueStatusBarUpdate();
    private void StatusBarSidebar_SizeChanged(object sender, SizeChangedEventArgs args) => QueueStatusBarUpdate();

    private void StatusBarDefaultsChanged(object? sender, string key)
    {
        if (key is AppDefaults.Key.ShowStatusBar or AppDefaults.Key.ShowStatusBarThemeMenu
            or AppDefaults.Key.StatusBarCountMode or AppDefaultsSharedExtensions.UnreadCountDisplayKey)
            QueueStatusBarUpdate();
    }

    private void QueueStatusBarUpdate()
    {
        if (_shuttingDown || Interlocked.Exchange(ref _statusBarUpdateQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _statusBarUpdateQueued, 0);
            if (_shuttingDown) return;
            if (Interlocked.Exchange(ref _statusBarClockInvalidated, 0) != 0)
            {
                TimeZoneInfo.ClearCachedData();
                CultureInfo.CurrentCulture.ClearCachedData();
                ScheduleStatusBarMidnight();
            }
            UpdateStatusBar();
            QueueToolbarUpdate();
            (App.WindowForXamlRoot(XamlRoot) as MainWindow)?.UpdateRefreshAvailability();
        })) Interlocked.Exchange(ref _statusBarUpdateQueued, 0);
    }

    private void UpdateStatusBar()
    {
        var defaults = AppDefaults.Shared;
        bool show = defaults.ShowStatusBar;
        StatusBarSurface.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SidebarFooter.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) return;

        bool sidebar = SidebarPanel.Visibility == Visibility.Visible && !ViewModel.IsUnifiedLayout;
        StatusBarSidebarColumn.Width = sidebar ? SidebarColumn.Width : GridLength.Auto;
        StatusBarCountButton.Visibility = StatusBarSplitter.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        bool showCounts = ViewModel.UnreadDisplayMode == UnreadCountDisplay.Count;
        var progress = CombinedRefreshProgress.Shared.Snapshot;
        bool refreshing = progress.NumberRemaining > 0;
        StatusBarProgress.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
        StatusBarProgress.Maximum = Math.Max(1, progress.NumberOfTasks);
        StatusBarProgress.Value = progress.NumberCompleted;
        if (refreshing)
        {
            StatusBarCountText.Text = string.Format(CultureInfo.CurrentCulture, AppStrings.Get("StatusBarProgress"),
                progress.NumberCompleted, progress.NumberOfTasks);
        }
        else
        {
            var accounts = AppRuntime.AppService.Shared.Accounts.ActiveAccounts.ToArray();
            StatusBarCountText.Text = StatusBarPresentation.CountText(defaults.GetStatusBarCountMode(), showCounts,
                accounts.SelectMany(account => account.FlattenedFeeds()).Sum(feed => feed.UnreadCount),
                accounts.Sum(account => account.Folders.Count),
                accounts.Sum(account => account.FlattenedFeeds().Count()),
                AppRuntime.AppService.Shared.Accounts.LastRefreshCompletedDate);
        }
        _statusBarLink = ViewModel.HoveredLink ?? ViewModel.SelectedArticle?.Link ?? "";
        StatusBarLink.Text = StatusBarPresentation.LinkText(_statusBarLink);
        ToolTipService.SetToolTip(StatusBarLink, _statusBarLink.Length > 0 ? _statusBarLink : null);
        StatusBarThemeButton.Content = ArticleThemesManager.Shared.CurrentTheme.Name;
        StatusBarThemeButton.Visibility = defaults.ShowStatusBarThemeMenu ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetStatusBarVisible(bool show)
        => SaveStatusBarPreference(() => AppDefaults.Shared.ShowStatusBar = show);

    internal void SetStatusBarThemeMenuVisible(bool show)
        => SaveStatusBarPreference(() => AppDefaults.Shared.ShowStatusBarThemeMenu = show);

    private void SaveStatusBarPreference(Action save)
    {
        try { save(); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            ReportToolbarError("StatusBarSaveError", error);
        }
    }

    private void StatusBarCount_Click(object sender, RoutedEventArgs args)
        => SaveStatusBarPreference(() => AppDefaults.Shared.SetStatusBarCountMode(
            StatusBarPresentation.NextMode(AppDefaults.Shared.GetStatusBarCountMode(),
                ViewModel.UnreadDisplayMode == UnreadCountDisplay.Count)));

    private void StatusBarContextMenu_Opening(object sender, object args)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        var themes = new ToggleMenuFlyoutItem
        {
            Text = AppStrings.Get("StatusBarShowThemes"), IsChecked = AppDefaults.Shared.ShowStatusBarThemeMenu
        };
        themes.Click += (_, _) => SetStatusBarThemeMenuVisible(themes.IsChecked);
        menu.Items.Add(themes);
        menu.Items.Add(MakeMenuItem(AppStrings.Get("StatusBarHide"), null, (_, _) => SetStatusBarVisible(false)));
    }

    private void StatusBarLinkMenu_Opening(object sender, object args)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        string link = _statusBarLink;
        menu.Items.Add(MakeMenuItem(AppStrings.Get("StatusBarCopyLink"), "\uE8C8", (_, _) => CopyToClipboard(link),
            isEnabled: link.Length > 0));
    }

    private void StatusBarGearMenu_Opening(object sender, object args)
    {
        var menu = (MenuFlyout)sender;
        menu.Items.Clear();
        if (AppRuntime.AppService.Shared.Accounts.CanRefresh)
            menu.Items.Add(MakeMenuItem(AppStrings.Get("Refresh"), "\uE72C", ReloadButton_Click,
                isEnabled: !ViewModel.IsLoading));
        menu.Items.Add(MakeMenuItem(AppStrings.Get(ViewModel.IsSidebarVisible ? "ToolbarHideSidebar" : "ToolbarShowSidebar"),
            null, ToggleSidebarButton_Click, isEnabled: !ViewModel.IsUnifiedLayout));
        menu.Items.Add(new MenuFlyoutSeparator());
        BuildSidebarMenuForNoSelection(menu);
        var selection = ViewModel.SelectedSidebarItems;
        if (selection.Count == 0) return;
        string heading = selection.Count == 1 ? selection[0].Title
            : AppStrings.Get(selection.All(item => item.Tag is Account.Feed) ? "StatusBarMultipleFeeds"
                : selection.All(item => item.Tag is Account.Folder) ? "StatusBarMultipleFolders"
                : selection.All(item => item.Tag is AppShared.SmartFeeds.IPseudoFeed) ? "StatusBarMultipleSmartFeeds"
                : "StatusBarMultipleSelection");
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = heading, IsEnabled = false });
        int firstCommand = menu.Items.Count;
        PopulateSidebarSelectionMenu(menu, selection[0]);
        for (int index = firstCommand; index < menu.Items.Count; index++)
            menu.Items[index].Margin = new Thickness(16, 0, 0, 0);
    }
}
