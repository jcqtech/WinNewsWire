using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.Timer;
using WinNewsWire.Core;
using WinNewsWire.Dialogs;

namespace WinNewsWire.AppWindows;

public sealed partial class PreferencesWindow : Window
{
    private bool _loaded;
    private bool _closed;
    private bool _populatingBrowsers;
    private bool _updatingUnreadDisplay;
    private bool _updatingTimelineThumbnails;
    private bool _updatingTimelineFeedIcons;
    private bool _updatingAutomation;
    private bool _updatingRendering;
    private bool _updatingThemes;
    private int _themeUpdateQueued;

    public PreferencesWindow()
    {
        InitializeComponent();
        VersionText.Text = $"WinNewsWire {App.DisplayVersion}";
        void OnClosed(object sender, WindowEventArgs args)
        {
            Closed -= OnClosed;
            _closed = true;
            AppDefaults.Shared.Changed -= PreferencesDefaultsChanged;
            AppService.Shared.AutomationStateChanged -= AutomationStateChanged;
            WinNewsWire.AppShared.ArticleThemes.ArticleThemesManager.Shared.ThemeNamesChanged -= ArticleThemesChanged;
            WinNewsWire.AppShared.ArticleThemes.ArticleThemesManager.Shared.CurrentThemeChanged -= ArticleThemesChanged;
        }
        Closed += OnClosed;
        WindowIconHelper.ApplyFlatIcon(this);
        WindowThemeHelper.Attach(this);
        PopulateCombos();
        PopulateAppearanceOptions();
        PopulateTimelineOptions();
        AppDefaults.Shared.Changed += PreferencesDefaultsChanged;
        AppService.Shared.AutomationStateChanged += AutomationStateChanged;
        WinNewsWire.AppShared.ArticleThemes.ArticleThemesManager.Shared.ThemeNamesChanged += ArticleThemesChanged;
        WinNewsWire.AppShared.ArticleThemes.ArticleThemesManager.Shared.CurrentThemeChanged += ArticleThemesChanged;
        _loaded = true;
    }

    private void PopulateCombos()
    {
        UnreadCountsLabel.Text = AppStrings.Get("UnreadCounts");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UnreadCountDisplayCombo, UnreadCountsLabel.Text);
        foreach (var (mode, key) in new[]
        {
            (UnreadCountDisplay.Count, "ShowUnreadCounts"),
            (UnreadCountDisplay.Dot, "ShowUnreadDots"),
            (UnreadCountDisplay.Hidden, "HideUnreadCounts")
        })
            UnreadCountDisplayCombo.Items.Add(new ComboBoxItem { Content = AppStrings.Get(key), Tag = mode });
        SyncUnreadCountDisplay();
        TimelineHeading.Text = AppStrings.Get("Timeline");
        TimelineThumbnailsLabel.Text = AppStrings.Get("ThumbnailSize");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TimelineThumbnailsCombo, TimelineThumbnailsLabel.Text);
        foreach (var mode in new[] { TimelineThumbnailsDisplay.None, TimelineThumbnailsDisplay.Small, TimelineThumbnailsDisplay.Large })
            TimelineThumbnailsCombo.Items.Add(new ComboBoxItem { Content = mode.DisplayString(), Tag = mode });
        SyncTimelineThumbnails();
        TimelineFeedIconsLabel.Text = AppStrings.Get("FeedIcons");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TimelineFeedIconsCombo, TimelineFeedIconsLabel.Text);
        foreach (var mode in Enum.GetValues<TimelineFeedIconsDisplay>())
            TimelineFeedIconsCombo.Items.Add(new ComboBoxItem { Content = mode.DisplayString(), Tag = mode });
        SyncTimelineFeedIcons();

        foreach (var v in Enum.GetValues<RefreshInterval>())
            RefreshIntervalCombo.Items.Add(new ComboBoxItem { Content = v.DisplayString(), Tag = (int)v });
        RefreshIntervalCombo.SelectedIndex = Enum.GetValues<RefreshInterval>()
            .ToList().FindIndex(v => (int)v == AppDefaults.Shared.RefreshIntervalRaw);
        if (RefreshIntervalCombo.SelectedIndex < 0) RefreshIntervalCombo.SelectedIndex = 0;

        foreach (var n in new[] { "Small", "Medium", "Large", "Extra Large", "Extra Extra Large" })
            TextSizeCombo.Items.Add(new ComboBoxItem { Content = n });
        TextSizeCombo.SelectedIndex = Math.Clamp(AppDefaults.Shared.ArticleTextSizeRaw, 0, 4);

        OpenBackgroundToggle.IsOn = AppDefaults.Shared.OpenInBrowserInBackground;
        PopulateBrowsers();

        // Theme picker — pulls the list from the same ArticleThemesManager the View
        // menu uses, so swapping themes here updates every render path immediately.
        PopulateThemeCombo();

        // Notifications toggle reflects the app-wide default; the per-feed flag
        // still lives in the sidebar context menu.
        NotificationsEnabledToggle.IsOn = AppDefaults.Shared.NotificationsEnabled;

        ReaderModeDefaultToggle.IsOn = AppDefaults.Shared.ReaderModeDefault;
        JavascriptToggle.IsOn = AppDefaults.Shared.ArticleContentJavascriptEnabled;
        DebugMenuToggle.IsOn = AppDefaults.Shared.ShowDebugMenu;
        AutomationToggle.Header = AppStrings.Get("AllowLocalAutomation");
        AutomationDescription.Text = AppStrings.Get("AutomationDescription");
        SyncAutomationPreference();
        CompatibilityRenderingToggle.Header = AppStrings.Get("CompatibilityRendering");
        CompatibilityRenderingDescription.Text = AppStrings.Get("CompatibilityRenderingDescription");
        DirectBrowserScrollingToggle.Header = AppStrings.Get("DirectBrowserScrolling");
        DirectBrowserScrollingDescription.Text = AppStrings.Get("DirectBrowserScrollingDescription");
        SyncRenderingPreference();

        ReloadAccounts();
    }

    private void PreferencesDefaultsChanged(object? sender, string key)
    {
        if (!_closed && SyncAppearancePreference(key)) return;
        if (!_closed && key == AppDefaultsSharedExtensions.TimelineFeedIconsDisplayKey)
        {
            if (DispatcherQueue.HasThreadAccess) SyncTimelineFeedIcons();
            else DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncTimelineFeedIcons(); });
            return;
        }
        if (!_closed && key == AppDefaultsSharedExtensions.TimelineThumbnailsDisplayKey)
        {
            if (DispatcherQueue.HasThreadAccess) SyncTimelineThumbnails();
            else DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncTimelineThumbnails(); });
            return;
        }
        if (!_closed && key is AppDefaults.Key.UseSoftwareRasterization or AppDefaults.Key.UseDirectBrowserScrolling)
        {
            if (DispatcherQueue.HasThreadAccess) SyncRenderingPreference();
            else DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncRenderingPreference(); });
            return;
        }
        if (!_closed && key == AppDefaults.Key.AutomationEnabled)
        {
            if (DispatcherQueue.HasThreadAccess) SyncAutomationPreference();
            else DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncAutomationPreference(); });
            return;
        }
        if (_closed || key != AppDefaultsSharedExtensions.UnreadCountDisplayKey) return;
        if (DispatcherQueue.HasThreadAccess) SyncUnreadCountDisplay();
        else DispatcherQueue.TryEnqueue(() => { if (!_closed) SyncUnreadCountDisplay(); });
    }

    private void SyncTimelineFeedIcons()
    {
        _updatingTimelineFeedIcons = true;
        try
        {
            var mode = AppDefaults.Shared.GetTimelineFeedIconsDisplay();
            TimelineFeedIconsCombo.SelectedItem = TimelineFeedIconsCombo.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag is TimelineFeedIconsDisplay value && value == mode);
            TimelineFeedIconsDescription.Text = mode == TimelineFeedIconsDisplay.Automatic
                ? AppStrings.Get("FeedIconsAutomaticDescription") : "";
        }
        finally { _updatingTimelineFeedIcons = false; }
    }

    private void TimelineFeedIconsCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || _updatingTimelineFeedIcons
            || TimelineFeedIconsCombo.SelectedItem is not ComboBoxItem { Tag: TimelineFeedIconsDisplay mode }) return;
        try
        {
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(mode);
            TimelineFeedIconsStatus.Text = "";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            SyncTimelineFeedIcons();
            TimelineFeedIconsStatus.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                AppStrings.Get("TimelineFeedIconsSaveError"), error.Message);
            App.LogException("Save timeline feed-icon setting", error);
        }
    }

    private void SyncTimelineThumbnails()
    {
        _updatingTimelineThumbnails = true;
        try
        {
            var mode = AppDefaults.Shared.GetTimelineThumbnailsDisplay();
            TimelineThumbnailsCombo.SelectedItem = TimelineThumbnailsCombo.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag is TimelineThumbnailsDisplay value && value == mode);
        }
        finally { _updatingTimelineThumbnails = false; }
    }

    private void TimelineThumbnailsCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || _updatingTimelineThumbnails
            || TimelineThumbnailsCombo.SelectedItem is not ComboBoxItem { Tag: TimelineThumbnailsDisplay mode }) return;
        try
        {
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(mode);
            TimelineThumbnailsStatus.Text = "";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            SyncTimelineThumbnails();
            TimelineThumbnailsStatus.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                AppStrings.Get("TimelineThumbnailsSaveError"), error.Message);
            App.LogException("Save timeline thumbnail setting", error);
        }
    }

    private void SyncRenderingPreference()
    {
        _updatingRendering = true;
        try
        {
            bool enabled = AppDefaults.Shared.UseSoftwareRasterization;
            CompatibilityRenderingToggle.IsOn = enabled;
            CompatibilityRenderingStatus.Text = AppStrings.Get(enabled != WebViewEnvironment.UsesSoftwareRasterization
                ? "RenderingRestartRequired" : enabled ? "RenderingCompatibilityActive" : "RenderingHardwareActive");
            bool direct = AppDefaults.Shared.UseDirectBrowserScrolling;
            DirectBrowserScrollingToggle.IsOn = direct;
            DirectBrowserScrollingStatus.Text = AppStrings.Get(direct != WebViewEnvironment.UsesDirectScrolling
                ? "RenderingRestartRequired" : direct ? "DirectScrollingSelected" : "AnimatedScrollingSelected");
        }
        finally { _updatingRendering = false; }
    }

    private void CompatibilityRenderingToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loaded || _updatingRendering) return;
        try { AppDefaults.Shared.UseSoftwareRasterization = CompatibilityRenderingToggle.IsOn; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            SyncRenderingPreference();
            CompatibilityRenderingStatus.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                AppStrings.Get("RenderingSaveFailed"), error.Message);
            App.LogException("Save compatibility rendering setting", error);
        }
    }

    private void DirectBrowserScrollingToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loaded || _updatingRendering) return;
        try { AppDefaults.Shared.UseDirectBrowserScrolling = DirectBrowserScrollingToggle.IsOn; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            SyncRenderingPreference();
            DirectBrowserScrollingStatus.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                AppStrings.Get("ScrollingSaveFailed"), error.Message);
            App.LogException("Save direct scrolling setting", error);
        }
    }

    private void SyncAutomationPreference()
    {
        _updatingAutomation = true;
        try
        {
            AutomationToggle.IsOn = AppDefaults.Shared.AutomationEnabled;
            AutomationStatusText.Text = AppService.Shared.AutomationStatus;
        }
        finally { _updatingAutomation = false; }
    }

    private void AutomationStateChanged(object? sender, EventArgs args)
    {
        if (_closed) return;
        if (DispatcherQueue.HasThreadAccess) AutomationStatusText.Text = AppService.Shared.AutomationStatus;
        else DispatcherQueue.TryEnqueue(() => { if (!_closed) AutomationStatusText.Text = AppService.Shared.AutomationStatus; });
    }

    private void AutomationToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loaded || _updatingAutomation) return;
        try { AppDefaults.Shared.AutomationEnabled = AutomationToggle.IsOn; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            SyncAutomationPreference();
            AutomationStatusText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                AppStrings.Get("AutomationSaveFailed"), error.Message);
            App.LogException("Save local automation setting", error);
        }
    }

    private void SyncUnreadCountDisplay()
    {
        _updatingUnreadDisplay = true;
        try
        {
            var mode = AppDefaults.Shared.GetUnreadCountDisplay();
            UnreadCountDisplayCombo.SelectedItem = UnreadCountDisplayCombo.Items.OfType<ComboBoxItem>()
                .Single(item => item.Tag is UnreadCountDisplay value && value == mode);
        }
        finally { _updatingUnreadDisplay = false; }
    }

    private void UnreadCountDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || _updatingUnreadDisplay || UnreadCountDisplayCombo.SelectedItem is not ComboBoxItem { Tag: UnreadCountDisplay mode })
            return;
        try
        {
            AppDefaults.Shared.SetUnreadCountDisplay(mode);
            UnreadCountsStatus.Text = "";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            SyncUnreadCountDisplay();
            UnreadCountsStatus.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                AppStrings.Get("UnreadCountSaveError"), error.Message);
            App.LogException("Unread count display", error);
        }
    }

    private void PopulateBrowsers()
    {
        _populatingBrowsers = true;
        try
        {
            BrowserCombo.Items.Clear();
            BrowserCombo.Items.Add(new ComboBoxItem { Content = "System Default" });
            var browsers = Browser.SortedBrowsers();
            var duplicates = Web.WindowsWebBrowser.DuplicateBrowserNames(browsers);
            foreach (var browser in browsers)
            {
                var label = duplicates.Contains(browser.Name) ? browser.Name + " - " + browser.DisplayPath : browser.Name;
                var item = new ComboBoxItem { Content = label, Tag = browser.Identifier };
                ToolTipService.SetToolTip(item, browser.ExecutablePath);
                BrowserCombo.Items.Add(item);
            }
            var selected = AppDefaults.Shared.DefaultBrowserID;
            BrowserCombo.SelectedIndex = 0;
            if (selected is not null)
            {
                var match = browsers.FirstOrDefault(browser => string.Equals(browser.Identifier, selected, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(browser.RegistrationID, selected, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    BrowserCombo.SelectedIndex = browsers.ToList().IndexOf(match) + 1;
                else
                {
                    var missing = new ComboBoxItem { Content = "Unavailable browser - " + selected, Tag = selected };
                    BrowserCombo.Items.Add(missing);
                    BrowserCombo.SelectedItem = missing;
                }
            }
            BrowserStatus.Text = "This preference applies to WinNewsWire, not the Windows default browser.";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or System.IO.IOException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            BrowserStatus.Text = "Could not list installed browsers: " + error.Message;
            App.LogException("List browsers", error);
        }
        finally { _populatingBrowsers = false; }
    }

    private void BrowserCombo_DropDownOpened(object sender, object args) => PopulateBrowsers();

    private void BrowserCombo_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loaded || _populatingBrowsers || BrowserCombo.SelectedItem is not ComboBoxItem item) return;
        try { AppDefaults.Shared.DefaultBrowserID = item.Tag as string; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            PopulateBrowsers();
            BrowserStatus.Text = "Could not save browser selection: " + error.Message;
            App.LogException("Select browser", error);
        }
    }

    private void PopulateThemeCombo()
    {
        _updatingThemes = true;
        try
        {
            ThemeCombo.Items.Clear();
            var themeMgr = WinNewsWire.AppShared.ArticleThemes.ArticleThemesManager.Shared;
            foreach (var theme in themeMgr.Themes)
                ThemeCombo.Items.Add(new ComboBoxItem { Content = theme.Name, Tag = theme.Name });
            var current = themeMgr.CurrentTheme.Name;
            for (int i = 0; i < ThemeCombo.Items.Count; i++)
            {
                if (ThemeCombo.Items[i] is ComboBoxItem cbi && (cbi.Tag as string) == current)
                {
                    ThemeCombo.SelectedIndex = i;
                    break;
                }
            }
            if (ThemeCombo.SelectedIndex < 0 && ThemeCombo.Items.Count > 0)
                ThemeCombo.SelectedIndex = 0;
        }
        finally { _updatingThemes = false; }
    }

    private void ArticleThemesChanged(object? sender, EventArgs args)
    {
        if (_closed || System.Threading.Interlocked.Exchange(ref _themeUpdateQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            System.Threading.Interlocked.Exchange(ref _themeUpdateQueued, 0);
            if (!_closed) PopulateThemeCombo();
        }))
        {
            System.Threading.Interlocked.Exchange(ref _themeUpdateQueued, 0);
            if (!_closed) App.LogException("Update theme preferences", new InvalidOperationException("The preferences dispatcher is unavailable."));
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _updatingThemes || ThemeCombo.SelectedItem is not ComboBoxItem { Tag: string name }) return;
        try
        {
            WinNewsWire.AppShared.ArticleThemes.ArticleThemesManager.Shared.SelectByName(name);
            ThemeErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            PopulateThemeCombo();
            ThemeErrorText.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                AppStrings.Get("ToolbarThemeSaveError"), error.Message);
            ThemeErrorText.Visibility = Visibility.Visible;
            App.LogException("Select article theme", error);
        }
    }

    private void OpenThemesFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = WinNewsWire.AppShared.ArticleThemes.ArticleThemesManager.Shared.FolderPath;
        try { System.IO.Directory.CreateDirectory(path); } catch { }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void NotificationsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        AppDefaults.Shared.NotificationsEnabled = NotificationsEnabledToggle.IsOn;
    }

    private void OpenNotificationSettings_Click(object sender, RoutedEventArgs e)
    {
        // Deep link to Windows' per-app notification settings. Falls back to the
        // top-level page if the app-scoped one isn't available on this build.
        try { _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:notifications")); }
        catch { }
    }

    public sealed record AccountRow(Account.Account Account)
    {
        public string Name => Account.Name;
        public string TypeLabel => Account.Type.ToString();
    }

    private void ReloadAccounts(string? selectedAccountId = null)
    {
        selectedAccountId ??= (AccountsList.SelectedItem as AccountRow)?.Account.AccountID;
        var rows = AppService.Shared.Accounts.SortedAccounts.Select(a => new AccountRow(a)).ToList();
        AccountsList.ItemsSource = rows;
        AccountsList.SelectedIndex = rows.Count == 0 ? -1 : Math.Max(0, rows.FindIndex(row => row.Account.AccountID == selectedAccountId));
        UpdateSelectedAccountText();
    }

    private void UpdateSelectedAccountText()
    {
        ReconnectAccountButton.IsEnabled = AccountsList.SelectedItem is AccountRow
            { Account.Type: not (AccountType.OnMyMac or AccountType.CloudKit) };
        RemoveAccountButton.IsEnabled = AccountsList.SelectedItem is AccountRow selected
            && !ReferenceEquals(selected.Account, AppService.Shared.Accounts.DefaultAccount);
        var rows = AccountsList.ItemsSource as System.Collections.Generic.IList<AccountRow>;
        if (AccountsList.SelectedItem is AccountRow row)
            SelectedAccountText.Text = row.Name;
        else if (rows is null || rows.Count == 0)
            SelectedAccountText.Text = "None";
        else
            SelectedAccountText.Text = $"{rows.Count} account{(rows.Count == 1 ? "" : "s")}";
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectedAccountText();

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var created = await WinNewsWire.Dialogs.AccountSheets.AccountTypePickerDialog.ShowAsync(((FrameworkElement)((Button)sender)).XamlRoot);
        if (!_closed && created is not null) ReloadAccounts(created.AccountID);
    }

    private async void ReconnectAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRow row) return;
        if (await Dialogs.AccountSheets.AccountTypePickerDialog.ReauthorizeAsync(((FrameworkElement)sender).XamlRoot, row.Account) is not null && !_closed)
            ReloadAccounts(row.Account.AccountID);
    }

    private async void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountRow row) return;
        var root = ((FrameworkElement)sender).XamlRoot;
        var confirmation = new ContentDialog
        {
            XamlRoot = root,
            Title = "Remove account?",
            Content = $"Remove {row.Name} and its locally stored subscriptions and articles from WinNewsWire? This does not delete an online service account.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        ((Button)sender).IsEnabled = false;
        try { await AppService.Shared.Accounts.RemoveAccountAsync(row.Account); }
        catch (Exception error)
        {
            await AppService.Shared.LogAsync("Remove account", error);
            if (!_closed) await new ContentDialog
            {
                XamlRoot = root, Title = "Could not finish removing the account",
                Content = error.Message, CloseButtonText = "OK"
            }.ShowAsync();
        }
        finally
        {
            if (!_closed)
            {
                ReloadAccounts();
            }
        }
    }

    private void RefreshIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || RefreshIntervalCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int raw) return;
        AppDefaults.Shared.RefreshIntervalRaw = raw;
    }

    private void TextSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        AppDefaults.Shared.ArticleTextSizeRaw = TextSizeCombo.SelectedIndex;
    }

    private void OpenBackground_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        AppDefaults.Shared.OpenInBrowserInBackground = OpenBackgroundToggle.IsOn;
    }

    private void Advanced_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        AppDefaults.Shared.ReaderModeDefault = ReaderModeDefaultToggle.IsOn;
        AppDefaults.Shared.ArticleContentJavascriptEnabled = JavascriptToggle.IsOn;
        AppDefaults.Shared.ShowDebugMenu = DebugMenuToggle.IsOn;
    }

    private void SendFeedback_Click(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("mailto:feedback@winnewswire.com?subject=WinNewsWire%20Feedback"));

    private void Help_Click(object sender, RoutedEventArgs e)
        => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("https://winnewswire.com/help/"));
}
