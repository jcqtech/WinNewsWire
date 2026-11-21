using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.ArticleThemes;
using WinNewsWire.AppShared.Toolbar;
using WinNewsWire.Core;
using WinNewsWire.Web;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private ToolbarConfiguration _toolbarConfiguration = ToolbarConfiguration.Default;
    private readonly Dictionary<string, Control> _toolbarControls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _toolbarLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IconElement> _toolbarIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _toolbarWidths = new(StringComparer.Ordinal);
    private ToolbarLayoutResult _toolbarLayout = new([], []);
    private AutoSuggestBox? _toolbarSearchBox;
    private Dialogs.ToolbarCustomizationDialog? _toolbarDialog;
    private bool _toolbarUpdateQueued;
    private bool _toolbarMeasurementsDirty = true;
    private bool _layingOutToolbar;
    private double _headerHeight = 52;
    private bool? _toolbarReadActionIsUnread;
    internal event EventHandler? ToolbarConfigurationChanged;
    internal ToolbarConfiguration CurrentToolbarConfiguration => _toolbarConfiguration;

    private static Geometry CreateReadStatusGeometry(bool markUnread = false)
        // WinUI geometry has a single owner, so share path text rather than Geometry instances.
        => (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry),
            Application.Current.Resources[markUnread ? "TextboxIconPath" : "TextboxCheckmarkIconPath"]);

    private static PathIcon ReadStatusMenuIcon(bool markUnread = false)
        => new() { Data = CreateReadStatusGeometry(markUnread), Width = 16, Height = 16 };

    private void InitializeToolbar()
    {
        var buttons = new (string ID, ContentControl Control)[]
        {
            ("toggleSidebar", ToggleSidebarButton), ("refresh", ReloadButton), ("newSidebarItemMenu", AddDropdown),
            ("markAllAsRead", MarkAllReadButton), ("toggleReadArticlesFilter", ReadArticlesFilterButton),
            ("markRead", ToolbarMarkReadButton), ("markStar", StarButton), ("nextUnread", NextUnreadButton),
            ("readerView", ReaderModeToggle), ("share", ShareButton), ("openInWebView", OpenInWebViewButton),
            ("openInBrowser", OpenInBrowserButton),
            ("articleThemeMenu", ArticleThemeButton), ("cleanUp", CleanUpButton)
        };
        foreach (var (id, button) in buttons)
        {
            _toolbarControls.Add(id, button);
            var icon = (IconElement)button.Content;
            button.Content = null;
            var label = new TextBlock { Text = ToolbarConfiguration.Label(id), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
            var stack = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(icon);
            stack.Children.Add(label);
            button.Content = stack;
            button.MinWidth = 0;
            button.MinHeight = 36;
            button.Padding = new Thickness(6, 4, 6, 4);
            button.BorderThickness = new Thickness(0);
            button.VerticalAlignment = VerticalAlignment.Center;
            _toolbarLabels.Add(id, label);
            _toolbarIcons.Add(id, icon);
            label.SizeChanged += (_, _) => InvalidateToolbarMeasurements();
            icon.SizeChanged += (_, _) => InvalidateToolbarMeasurements();
            SetToolbarActionLabel(id, label.Text);
        }
        AutomationProperties.SetName(MainToolbar, AppStrings.Get("Toolbar"));
        AutomationProperties.SetHelpText(MainToolbar, AppStrings.Get("ToolbarKeyboardHelp"));
        AutomationProperties.SetName(ToolbarOverflowButton, AppStrings.Get("ToolbarMore"));
        ToolTipService.SetToolTip(ToolbarOverflowButton, AppStrings.Get("ToolbarMore"));
        AutomationProperties.SetName(FloatingToolbarButton, AppStrings.Get("ToolbarOptions"));
        ToolTipService.SetToolTip(FloatingToolbarButton, AppStrings.Get("ToolbarOptions"));
        InitializeFloatingToolbar();
        var addItems = ((MenuFlyout)AddDropdown.Flyout).Items.OfType<MenuFlyoutItem>().ToArray();
        addItems[0].Text = AppStrings.Get("ToolbarNewFeed");
        addItems[1].Text = AppStrings.Get("ToolbarNewFolder");
        ToolbarOverflowButton.TabIndex = 100;
        FloatingToolbarButton.TabIndex = 100;
        ToolbarItemsPanel.GotFocus += (_, _) => QueueToolbarUpdate();
        ToolbarItemsPanel.LostFocus += (_, _) => QueueToolbarUpdate();
        SidebarToolbarItemsPanel.GotFocus += (_, _) => QueueToolbarUpdate();
        TimelineToolbarItemsPanel.GotFocus += (_, _) => QueueToolbarUpdate();
        TimelineHeaderCaption.SizeChanged += (_, args) =>
        {
            if (args.NewSize.Height != args.PreviousSize.Height) InvalidateToolbarMeasurements();
        };
        SidebarPanel.SizeChanged += (_, _) => QueueToolbarUpdate();
        TimelinePanel.SizeChanged += (_, _) => QueueToolbarUpdate();
        ArticleFindBar.SizeChanged += (_, _) => QueueToolbarUpdate();
        WebPageError.SizeChanged += (_, _) => QueueToolbarUpdate();
        AppDefaults.Shared.Changed += ToolbarDefaultsChanged;
        ApplyToolbarConfiguration(ToolbarConfiguration.Load(AppDefaults.Shared));
    }

    internal void AttachTitleBarControls(AutoSuggestBox searchBox, ContentControl sidebarHost)
    {
        _toolbarSearchBox = searchBox;
        searchBox.MinWidth = 0;
        searchBox.PlaceholderText = AppStrings.Get("ToolbarSearchPlaceholder");
        AutomationProperties.SetName(searchBox, ToolbarConfiguration.Label("search"));
        ToolTipService.SetToolTip(searchBox, ToolbarConfiguration.Label("search") + " (Ctrl+Alt+F)");
        _toolbarControls.Add("search", searchBox);
        ToolbarItemsPanel.Children.Remove(ToggleSidebarButton);
        sidebarHost.Content = ToggleSidebarButton;
        ToggleSidebarButton.Width = ToggleSidebarButton.Height = 32;
        ToggleSidebarButton.MinHeight = 32;
        searchBox.GotFocus += (_, _) => SuspendSingleKeyAccelerators();
        searchBox.LostFocus += (_, _) => ResumeSingleKeyAccelerators();
        QueueToolbarUpdate();
    }

    private void ShutdownToolbar()
    {
        AppDefaults.Shared.Changed -= ToolbarDefaultsChanged;
        ShutdownFloatingToolbar();
        try
        {
            _toolbarDialog?.Hide();
            ToolbarOverflowMenu.Hide();
            FloatingToolbarMenu.Hide();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            App.LogException("Close toolbar popups", error);
        }
    }

    private void ToolbarDefaultsChanged(object? sender, string key)
    {
        if (key != ToolbarConfiguration.PreferenceKey || _shuttingDown) return;
        void Apply()
        {
            if (!_shuttingDown) ApplyToolbarConfiguration(ToolbarConfiguration.Load(AppDefaults.Shared));
        }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else DispatcherQueue.TryEnqueue(Apply);
    }

    internal string? SaveToolbarConfiguration(ToolbarConfiguration configuration)
    {
        try
        {
            configuration.Save(AppDefaults.Shared);
            ToolbarErrorInfoBar.IsOpen = false;
            return null;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Neither this window nor another window sees a speculative configuration.
            ApplyToolbarConfiguration(_toolbarConfiguration);
            return ReportToolbarError("ToolbarSaveError", error);
        }
    }

    private string ReportToolbarError(string resource, Exception error)
    {
        App.LogException("Toolbar", error);
        var message = string.Format(System.Globalization.CultureInfo.CurrentCulture, AppStrings.Get(resource), error.Message);
        ViewModel.StatusMessage = message;
        ToolbarErrorInfoBar.Title = AppStrings.Get("Toolbar");
        ToolbarErrorInfoBar.Message = message;
        ToolbarErrorInfoBar.IsOpen = true;
        return message;
    }

    private void ApplyToolbarConfiguration(ToolbarConfiguration configuration)
    {
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        bool restoreHeaderFocus = configuration.IsVisible && IsWithin(focused, FloatingToolbarButton);
        bool moveFocus = (!configuration.IsVisible && (IsWithin(focused, SidebarHeader)
            || IsWithin(focused, TimelineToolbar) || IsWithin(focused, MainToolbar)))
            || _toolbarControls.Any(pair => !ToolbarConfiguration.IsTitleBarItem(pair.Key)
                && IsWithin(focused, pair.Value) && (!configuration.IsVisible || !configuration.Items.Contains(pair.Key)));
        _toolbarConfiguration = configuration;
        UpdateToolbarState();
        UpdateToolbarPresentation();
        UpdateToolbarLayout();
        if (restoreHeaderFocus) ToolbarOverflowButton.Focus(FocusState.Programmatic);
        else if (moveFocus)
        {
            if (!configuration.IsVisible) FloatingToolbarButton.Focus(FocusState.Programmatic);
            else if (ViewModel.IsUnifiedLayout) ContentWebView.Focus(FocusState.Programmatic);
            else ArticleListView.Focus(FocusState.Programmatic);
        }
        ToolbarConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void ToggleToolbar()
        => SaveToolbarConfiguration(_toolbarConfiguration.WithVisibility(!_toolbarConfiguration.IsVisible));

    internal async Task CustomizeToolbarAsync()
    {
        if (_toolbarDialog is not null || _shuttingDown) return;
        var dialog = new Dialogs.ToolbarCustomizationDialog(_toolbarConfiguration, SaveToolbarConfiguration) { XamlRoot = XamlRoot };
        _toolbarDialog = dialog;
        SuspendSingleKeyAccelerators();
        try { await dialog.ShowAsync(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            if (!_shuttingDown) ReportToolbarError("ToolbarCustomizeError", error);
        }
        finally
        {
            _toolbarDialog = null;
            if (!_shuttingDown && XamlRoot is not null && IsTextInputControl(FocusManager.GetFocusedElement(XamlRoot))) SuspendSingleKeyAccelerators();
            else ResumeSingleKeyAccelerators();
        }
    }

    internal void FocusToolbarSearch()
    {
        if (_shuttingDown || _toolbarSearchBox is null) return;
        ToolbarOverflowMenu.Hide();
        FloatingToolbarMenu.Hide();
        _toolbarSearchBox.UpdateLayout();
        if (!_toolbarSearchBox.Focus(FocusState.Programmatic))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_shuttingDown) return;
                _toolbarSearchBox.Focus(FocusState.Programmatic);
            });
    }

    private static string ToolbarShortcut(string id) => id switch
    {
        "toggleSidebar" => "Ctrl+Shift+B", "refresh" => "Ctrl+R", "markAllAsRead" => "Ctrl+K",
        "markRead" => "U", "markStar" => "Ctrl+L", "nextUnread" => "N", "readerView" => "Ctrl+Shift+W",
        "openInBrowser" => "Ctrl+B", "search" => "Ctrl+Alt+F", _ => ""
    };

    private void SetToolbarActionLabel(string id, string label)
    {
        if (!_toolbarLabels.TryGetValue(id, out var text)) return;
        if (text.Text != label)
        {
            text.Text = label;
            if (_toolbarConfiguration.DisplayMode != ToolbarDisplayMode.IconsOnly) _toolbarMeasurementsDirty = true;
            QueueToolbarUpdate();
        }
        var control = _toolbarControls[id];
        AutomationProperties.SetName(control, label);
        string shortcut = ToolbarShortcut(id);
        ToolTipService.SetToolTip(control, shortcut.Length == 0 ? label : $"{label} ({shortcut})");
    }

    private void UpdateToolbarState()
    {
        if (_toolbarControls.Count == 0 || _shuttingDown) return;
#if PARITY_PROBE
        _probeToolbarStateUpdates++;
#endif
        bool selected = ViewModel.HasSelectedArticles;
        ToggleSidebarButton.IsEnabled = !ViewModel.IsUnifiedLayout;
        ReloadButton.IsEnabled = !ViewModel.IsLoading && AppRuntime.AppService.Shared.Accounts.CanRefresh;
        MarkAllReadButton.IsEnabled = ViewModel.ArticleItems.Any(article => !article.IsRead);
        ToolbarMarkReadButton.IsEnabled = ViewModel.CanToggleSelectedRead;
        StarButton.IsEnabled = selected;
        ReaderModeToggle.IsEnabled = ViewModel.SelectedArticle is { } article
            && Uri.TryCreate(article.Link, UriKind.Absolute, out var readerUri) && readerUri.Scheme is "http" or "https";
        ShareButton.IsEnabled = _showingWebPage || selected;
        OpenInWebViewButton.IsEnabled = CanOpenSelectedInWebView;
        OpenInBrowserButton.IsEnabled = _showingWebPage || ViewModel.SelectedArticles.Any(article =>
            Uri.TryCreate(article.Link, UriKind.Absolute, out var uri) && uri.PreparedForOpeningInBrowser() is not null);
        NextUnreadButton.IsEnabled = CanSelectNextUnread;
        ReadArticlesFilterButton.IsEnabled = ViewModel.CanToggleReadFilter;
        ReadArticlesFilterButton.IsChecked = ViewModel.IsReadFiltered;
        CleanUpButton.IsEnabled = ViewModel.IsReadFiltered && ViewModel.ArticleItems.Any(article =>
            article.IsRead && !ViewModel.SelectedArticles.Contains(article));
        SetToolbarActionLabel("toggleSidebar", AppStrings.Get(ViewModel.IsSidebarVisible ? "ToolbarHideSidebar" : "ToolbarShowSidebar"));
        bool markUnread = selected && ViewModel.SelectedArticles.All(article => article.IsRead);
        SetToolbarActionLabel("markRead", AppStrings.Get(markUnread
            ? "ToolbarMarkUnread" : "Toolbar_markRead"));
        if (_toolbarReadActionIsUnread != markUnread)
        {
            ToolbarReadStatusIcon.Data = CreateReadStatusGeometry(markUnread);
            _toolbarReadActionIsUnread = markUnread;
        }
        SetToolbarActionLabel("toggleReadArticlesFilter", AppStrings.Get(ViewModel.IsReadFiltered ? "ToolbarShowReadArticles" : "ToolbarHideReadArticles"));
        ToolTipService.SetToolTip(ReadArticlesFilterButton, AppStrings.Get(
            !ViewModel.CanToggleReadFilter ? "ToolbarReadFilterUnavailable"
                : ViewModel.IsReadFiltered ? "ToolbarShowReadArticles" : "ToolbarHideReadArticlesHelp"));
        UpdateStarButtonIcon();
        UpdateReaderModeButtonLabel();
    }

    private void UpdateToolbarPresentation()
    {
        _toolbarMeasurementsDirty = false;
#if PARITY_PROBE
        _probeToolbarPresentationUpdates++;
#endif
        double height = _toolbarConfiguration.DisplayMode == ToolbarDisplayMode.IconsAndText ? 56 : 36;
        foreach (var (id, label) in _toolbarLabels)
        {
            if (ToolbarConfiguration.IsTitleBarItem(id))
            {
                label.Visibility = Visibility.Collapsed;
                _toolbarIcons[id].Visibility = Visibility.Visible;
                continue;
            }
            bool icons = _toolbarConfiguration.DisplayMode != ToolbarDisplayMode.TextOnly;
            bool text = _toolbarConfiguration.DisplayMode != ToolbarDisplayMode.IconsOnly;
            _toolbarIcons[id].Visibility = icons ? Visibility.Visible : Visibility.Collapsed;
            label.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
            var control = _toolbarControls[id];
            control.Height = double.NaN;
            control.MinHeight = icons && text ? 56 : 36;
            var visibility = control.Visibility;
            control.Visibility = Visibility.Visible;
            control.Width = double.NaN;
            control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _toolbarWidths[id] = Math.Max(36, control.DesiredSize.Width);
            height = Math.Max(height, control.DesiredSize.Height);
            control.Visibility = visibility;
        }
        TimelineHeaderCaption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _headerHeight = Math.Max(_toolbarConfiguration.IsVisible ? height : 36, TimelineHeaderCaption.DesiredSize.Height) + 16;
        TimelineToolbar.MinHeight = SidebarHeader.MinHeight = _headerHeight;
        ToolbarItemsPanel.MinHeight = _toolbarConfiguration.IsVisible ? height : 36;
    }

    private void QueueToolbarUpdate()
    {
        if (_toolbarUpdateQueued || _shuttingDown) return;
        _toolbarUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _toolbarUpdateQueued = false;
            if (_shuttingDown) return;
            UpdateToolbarState();
            if (_toolbarMeasurementsDirty) UpdateToolbarPresentation();
            UpdateToolbarLayout();
        })) _toolbarUpdateQueued = false;
    }

    private void InvalidateToolbarMeasurements()
    {
        _toolbarMeasurementsDirty = true;
        QueueToolbarUpdate();
    }

    private void ToolbarItemsPanel_SizeChanged(object sender, SizeChangedEventArgs args) => QueueToolbarUpdate();

    private void PlaceHeaderControls(bool column, bool combined)
    {
        var host = column ? CombinedToolbarHost : ArticleToolbarHost;
        if (!ReferenceEquals(host.Content, MainToolbar))
        {
            if (MainToolbar.Parent is Panel original) original.Children.Remove(MainToolbar);
            ArticleToolbarHost.Content = null;
            CombinedToolbarHost.Content = null;
            host.Content = MainToolbar;
            FloatingToolbarButton.SetBinding(Control.BackgroundProperty, new Binding
            {
                Source = column ? TimelineToolbar : ContentContainer,
                Path = new PropertyPath(nameof(MainToolbar.Background)),
                Mode = BindingMode.OneWay
            });
        }
        var headerVisibility = _toolbarConfiguration.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        MainToolbar.Visibility = TimelineToolbar.Visibility = SidebarHeader.Visibility = headerVisibility;
        CombinedToolbarHost.Visibility = column ? headerVisibility : Visibility.Collapsed;
        ArticleToolbarHost.Visibility = column ? Visibility.Collapsed : headerVisibility;
        FloatingToolbarButton.Visibility = _toolbarConfiguration.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        MainToolbar.Padding = column ? new Thickness(0) : new Thickness(12, 8, 12, 8);
        MainToolbar.BorderThickness = column ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        TimelineToolbar.BorderThickness = column ? new Thickness(0) : new Thickness(0, 0, 0, 1);
        MainToolbar.MinHeight = column ? 0 : _headerHeight;
        SidebarReadFilterButton.Visibility = _toolbarConfiguration.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateTimelineHeaderInsets();
        foreach (var id in ToolbarConfiguration.AllowedItems.Where(ToolbarConfiguration.IsHeaderItem))
        {
            var target = id is "refresh" or "newSidebarItemMenu" ? SidebarToolbarItemsPanel
                : combined ? ToolbarItemsPanel
                : id is "markAllAsRead" or "toggleReadArticlesFilter" or "cleanUp" ? TimelineToolbarItemsPanel
                : ToolbarItemsPanel;
            var control = _toolbarControls[id];
            if (target.Children.Contains(control)) continue;
            foreach (var previous in new[] { ToolbarItemsPanel, SidebarToolbarItemsPanel, TimelineToolbarItemsPanel })
                if (previous.Children.Contains(control)) previous.Children.Remove(control);
            target.Children.Add(control);
        }
    }

    private void UpdateToolbarLayout(string? priorityItem = null)
    {
        if (_layingOutToolbar || _shuttingDown || !IsLoaded) return;
#if PARITY_PROBE
        _probeToolbarLayoutUpdates++;
#endif
        _layingOutToolbar = true;
        try
        {
            var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            priorityItem ??= _toolbarControls.FirstOrDefault(pair => IsWithin(focused, pair.Value)).Key;
            bool column = ViewModel.UseColumnLayout && !ViewModel.IsUnifiedLayout;
            bool combined = column || ViewModel.IsUnifiedLayout;
            PlaceHeaderControls(column, combined);
            double contentInset = column ? TimelineColumnsHeader.ActualHeight
                : Math.Max(ArticleFindBar.Visibility == Visibility.Visible ? ArticleFindBar.ActualHeight : 0,
                    WebPageError.IsOpen ? WebPageError.ActualHeight : 0);
            FloatingToolbarSurface.Margin = new Thickness(0, contentInset + 8, 28, 0);
            UpdateFloatingToolbarPlacement();
            double Desired(ToolbarSection section) => !_toolbarConfiguration.IsVisible ? 0
                : ToolbarLayout.HeaderItems(_toolbarConfiguration, section).Sum(id => _toolbarWidths.GetValueOrDefault(id, 36));
            double titleBudget = Math.Max(0, TimelineToolbar.ActualWidth - 24 - 92);
            TimelineToolbarItemsPanel.Width = combined ? 0 : Math.Min(Desired(ToolbarSection.Timeline), titleBudget);
            TimelineToolbarItemsPanel.Visibility = combined ? Visibility.Collapsed : Visibility.Visible;
            CombinedToolbarHost.Width = Math.Min(Desired(ToolbarSection.Reading) + 36, Math.Max(36, titleBudget));

            bool sidebarVisible = SidebarPanel.Visibility == Visibility.Visible;
            var side = ToolbarLayout.CalculateHeader(_toolbarConfiguration, ToolbarSection.Sidebar, _toolbarWidths,
                sidebarVisible ? SidebarToolbarItemsPanel.ActualWidth : 0, sidebarVisible ? priorityItem : null);
            var timeline = combined ? new ToolbarLayoutResult([], [])
                : ToolbarLayout.CalculateHeader(_toolbarConfiguration, ToolbarSection.Timeline, _toolbarWidths,
                    TimelinePanel.Visibility == Visibility.Visible ? TimelineToolbarItemsPanel.Width : 0, priorityItem);
            double mainWidth = (column ? CombinedToolbarHost.Width : MainToolbar.ActualWidth)
                - MainToolbar.Padding.Left - MainToolbar.Padding.Right - 36;
            var main = ToolbarLayout.CalculateHeader(_toolbarConfiguration,
                combined ? ToolbarSection.Reading : ToolbarSection.Article, _toolbarWidths, mainWidth, priorityItem);
            ApplyHeaderSlots(SidebarToolbarItemsPanel, side, alignRight: true);
            ApplyHeaderSlots(TimelineToolbarItemsPanel, timeline, alignRight: true);
            ApplyHeaderSlots(ToolbarItemsPanel, main, alignRight: column);
            var overflow = side.OverflowItems.Concat(timeline.OverflowItems).Concat(main.OverflowItems).ToList();
            if (_toolbarConfiguration.IsVisible && !sidebarVisible)
                overflow.Add("toggleReadFeedsFilter");
            _toolbarLayout = new(side.Slots.Concat(timeline.Slots).Concat(main.Slots).ToArray(), overflow);
        }
        finally { _layingOutToolbar = false; }
    }

    private void ApplyHeaderSlots(Grid panel, ToolbarLayoutResult layout, bool alignRight)
    {
        var shown = layout.Slots.Select(slot => slot.Identifier).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in _toolbarControls.Where(pair => panel.Children.Contains(pair.Value)
            && !ToolbarConfiguration.IsTitleBarItem(pair.Key)))
        {
            var visibility = shown.Contains(pair.Key) ? Visibility.Visible : Visibility.Collapsed;
            if (pair.Value.Visibility != visibility) pair.Value.Visibility = visibility;
        }
        int count = layout.Slots.Count + 1;
#if PARITY_PROBE
        if (panel.ColumnDefinitions.Count != count) _probeToolbarColumnRebuilds++;
#endif
        while (panel.ColumnDefinitions.Count < count) panel.ColumnDefinitions.Add(new());
        while (panel.ColumnDefinitions.Count > count) panel.ColumnDefinitions.RemoveAt(panel.ColumnDefinitions.Count - 1);
        void SetWidth(int index, GridLength width)
        {
            if (panel.ColumnDefinitions[index].Width != width) panel.ColumnDefinitions[index].Width = width;
        }
        SetWidth(alignRight ? 0 : count - 1, new GridLength(1, GridUnitType.Star));
        int index = alignRight ? 1 : 0;
        foreach (var slot in layout.Slots)
        {
            SetWidth(index, new GridLength(slot.Width));
            var control = _toolbarControls[slot.Identifier];
            if (Grid.GetColumn(control) != index) Grid.SetColumn(control, index);
            if (control.Width != slot.Width) control.Width = slot.Width;
            if (control.TabIndex != slot.ConfigurationIndex) control.TabIndex = slot.ConfigurationIndex;
            index++;
        }
    }

    private void MainToolbar_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (IsTextInputControl(focused) || args.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End)) return;
        var scope = sender as DependencyObject ?? MainToolbar;
        var controls = _toolbarLayout.Slots.Where(slot => _toolbarControls.ContainsKey(slot.Identifier))
            .Select(slot => _toolbarControls[slot.Identifier]).Where(control => control.IsEnabled && control.Visibility == Visibility.Visible)
            .Append(ToolbarOverflowButton).Where(control => IsWithin(control, scope)).ToArray();
        if (controls.Length == 0) return;
        int current = Array.FindIndex(controls, control => IsWithin(focused as DependencyObject, control));
        int next = args.Key switch
        {
            VirtualKey.Home => 0, VirtualKey.End => controls.Length - 1,
            VirtualKey.Left => (current - 1 + controls.Length) % controls.Length,
            _ => (current + 1) % controls.Length
        };
        args.Handled = controls[next].Focus(FocusState.Keyboard);
    }

    private void ToolbarContextMenu_Opening(object sender, object args)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Items.Clear();
        AddToolbarConfigurationMenu(menu.Items);
    }

    private void ToolbarOverflowMenu_Opening(object sender, object args)
    {
        UpdateToolbarState();
        if (_toolbarMeasurementsDirty) UpdateToolbarPresentation();
        UpdateToolbarLayout();
        ToolbarOverflowMenu.Items.Clear();
        foreach (var id in _toolbarLayout.OverflowItems) ToolbarOverflowMenu.Items.Add(CreateToolbarOverflowItem(id));
        if (ToolbarOverflowMenu.Items.Count > 0) ToolbarOverflowMenu.Items.Add(new MenuFlyoutSeparator());
        AddToolbarConfigurationMenu(ToolbarOverflowMenu.Items);
    }

    private void AddToolbarConfigurationMenu(IList<MenuFlyoutItemBase> items)
    {
        var visible = new MenuFlyoutItem { Text = AppStrings.Get(_toolbarConfiguration.IsVisible ? "HideToolbar" : "ShowToolbar"),
            KeyboardAcceleratorTextOverride = "Ctrl+Alt+T" };
        visible.Click += (_, _) => ToggleToolbar();
        items.Add(visible);
        var customize = new MenuFlyoutItem { Text = AppStrings.Get("CustomizeToolbar") };
        customize.Click += async (_, _) => await CustomizeToolbarAsync();
        items.Add(customize);
        var display = new MenuFlyoutSubItem { Text = AppStrings.Get("ToolbarDisplayMode") };
        foreach (var mode in Enum.GetValues<ToolbarDisplayMode>())
        {
            var item = new ToggleMenuFlyoutItem { Text = AppStrings.Get("ToolbarDisplay_" + mode), IsChecked = mode == _toolbarConfiguration.DisplayMode };
            item.Click += (_, _) =>
            {
                SaveToolbarConfiguration(_toolbarConfiguration.WithDisplayMode(mode));
                foreach (var toggle in display.Items.OfType<ToggleMenuFlyoutItem>())
                    toggle.IsChecked = toggle == item ? _toolbarConfiguration.DisplayMode == mode
                        : toggle.Text == AppStrings.Get("ToolbarDisplay_" + _toolbarConfiguration.DisplayMode);
            };
            display.Items.Add(item);
        }
        items.Add(display);
    }

    private MenuFlyoutItemBase CreateToolbarOverflowItem(string id)
    {
        if (id == "toggleReadFeedsFilter")
        {
            var filter = new ToggleMenuFlyoutItem { Text = ViewModel.SidebarReadFilterLabel, IsChecked = ViewModel.IsSidebarReadFiltered };
            filter.Click += (_, _) => ViewModel.IsSidebarReadFiltered = !ViewModel.IsSidebarReadFiltered;
            return filter;
        }
        string label = _toolbarControls.TryGetValue(id, out var control) ? AutomationProperties.GetName(control) : ToolbarConfiguration.Label(id);
        if (id is "newSidebarItemMenu" or "articleThemeMenu")
        {
            var submenu = new MenuFlyoutSubItem { Text = label, IsEnabled = control?.IsEnabled ?? true };
            if (id == "articleThemeMenu") PopulateArticleThemeItems(submenu.Items);
            else
            {
                var feed = new MenuFlyoutItem { Text = AppStrings.Get("ToolbarNewFeed") };
                feed.Click += NewFeed_Click;
                var folder = new MenuFlyoutItem { Text = AppStrings.Get("ToolbarNewFolder") };
                folder.Click += NewFolder_Click;
                submenu.Items.Add(feed);
                submenu.Items.Add(folder);
            }
            return submenu;
        }
        if (id is "toggleReadArticlesFilter" or "readerView")
        {
            var toggle = new ToggleMenuFlyoutItem
            {
                Text = label, IsEnabled = control?.IsEnabled ?? true,
                IsChecked = id == "readerView" ? ViewModel.IsReaderMode : ViewModel.IsReadFiltered,
                KeyboardAcceleratorTextOverride = ToolbarShortcut(id)
            };
            toggle.Click += async (_, _) => await ExecuteToolbarItemAsync(id);
            return toggle;
        }
        var item = new MenuFlyoutItem { Text = label, IsEnabled = control?.IsEnabled ?? true, KeyboardAcceleratorTextOverride = ToolbarShortcut(id) };
        if (id == "markRead")
            item.Icon = ReadStatusMenuIcon(_toolbarReadActionIsUnread == true);
        else if (_toolbarIcons.TryGetValue(id, out var icon))
        {
            var glyph = icon switch
            {
                FontIcon font => font.Glyph,
                AnimatedIcon { FallbackIconSource: FontIconSource fallback } => fallback.Glyph,
                _ => null
            };
            if (glyph is not null)
            {
                var copy = new FontIcon { Glyph = glyph };
                if (icon is FontIcon font) copy.FontFamily = font.FontFamily;
                item.Icon = copy;
            }
        }
        item.Click += async (_, _) => await ExecuteToolbarItemAsync(id);
        return item;
    }

    private async Task ExecuteToolbarItemAsync(string id)
    {
        UpdateToolbarState();
        if (_toolbarControls.TryGetValue(id, out var control) && !control.IsEnabled) return;
        try
        {
            switch (id)
            {
                case "toggleSidebar": ViewModel.ToggleSidebarCommand.Execute(null); break;
                case "refresh": await ViewModel.ReloadFeedsCommand.ExecuteAsync(null); break;
                case "markAllAsRead":
                    await ViewModel.MarkAllReadCommand.ExecuteAsync(null);
                    ViewModel.BuildSidebar();
                    BuildSidebarUI();
                    break;
                case "toggleReadArticlesFilter": await ToggleToolbarReadFilterAsync(); break;
                case "markRead": await ToggleSelectedReadAsync(); break;
                case "markStar": await ToggleSelectedStarAsync(); break;
                case "nextUnread": await SelectNextUnreadAsync(); break;
                case "readerView":
                    if (_showingWebPage)
                    {
                        RememberCurrentLocation();
                        ShowWebPage(false);
                        ViewModel.IsReaderMode = true;
                    }
                    else ViewModel.IsReaderMode = !ViewModel.IsReaderMode;
                    break;
                case "openInWebView": await OpenSelectedInWebViewAsync(); break;
                case "openInBrowser": await OpenSelectedLinksAsync(); break;
                case "share": ShareSelectedArticle(); break;
                case "search": FocusToolbarSearch(); break;
                case "cleanUp": ViewModel.CleanUpReadArticlesCommand.Execute(null); break;
            }
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            ReportToolbarError("ToolbarActionError", error);
        }
        finally { QueueToolbarUpdate(); }
    }

    internal async Task ToggleToolbarReadFilterAsync()
    {
        try { await ViewModel.ToggleReadFilterCommand.ExecuteAsync(null); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            ReportToolbarError("ToolbarActionError", error);
        }
        finally { UpdateToolbarState(); }
    }

    private async void ToolbarMarkReadButton_Click(object sender, RoutedEventArgs args) => await ExecuteToolbarItemAsync("markRead");
    private async void ReadArticlesFilterButton_Click(object sender, RoutedEventArgs args) => await ExecuteToolbarItemAsync("toggleReadArticlesFilter");
    private async void NextUnreadButton_Click(object sender, RoutedEventArgs args) => await ExecuteToolbarItemAsync("nextUnread");
    private async void OpenInWebViewButton_Click(object sender, RoutedEventArgs args) => await ExecuteToolbarItemAsync("openInWebView");
    private async void CleanUpButton_Click(object sender, RoutedEventArgs args) => await ExecuteToolbarItemAsync("cleanUp");
    private void ToolbarThemes_Opening(object sender, object args) => PopulateArticleThemeItems(((MenuFlyout)sender).Items);

    internal void PopulateArticleThemeItems(IList<MenuFlyoutItemBase> items)
    {
        items.Clear();
        var manager = ArticleThemesManager.Shared;
        foreach (var theme in manager.Themes)
        {
            if (items.Count == 1) items.Add(new MenuFlyoutSeparator());
            var item = new ToggleMenuFlyoutItem { Text = theme.Name + (theme.IsAppTheme ? AppStrings.Get("ToolbarBuiltInTheme") : ""),
                IsChecked = theme.Name == manager.CurrentTheme.Name };
            item.Click += (_, _) =>
            {
                try
                {
                    manager.CurrentTheme = theme;
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
                {
                    ReportToolbarError("ToolbarThemeSaveError", error);
                }
                PopulateArticleThemeItems(items);
            };
            items.Add(item);
        }
    }

#if PARITY_PROBE
    private int _probeToolbarStateUpdates, _probeToolbarPresentationUpdates, _probeToolbarLayoutUpdates, _probeToolbarColumnRebuilds;
    internal (int State, int Presentation, int Layout, int Columns) ProbeToolbarPerformance
        => (_probeToolbarStateUpdates, _probeToolbarPresentationUpdates, _probeToolbarLayoutUpdates, _probeToolbarColumnRebuilds);
    internal ToolbarLayoutResult ProbeToolbarLayout => _toolbarLayout;
    internal AutoSuggestBox? ProbeToolbarSearch => _toolbarSearchBox;
    internal void ProbeLayoutToolbar(double width)
    {
        MainToolbar.MaxWidth = double.IsNaN(width) ? double.PositiveInfinity
            : width + MainToolbar.Padding.Left + MainToolbar.Padding.Right + 36;
        MainToolbar.UpdateLayout();
        UpdateToolbarLayout();
    }
    internal Dialogs.ToolbarCustomizationDialog? ProbeToolbarDialog => _toolbarDialog;
#endif
}
