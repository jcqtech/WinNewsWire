using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppShared;
using WinNewsWire.Core;

namespace WinNewsWire.AppWindows;

public sealed partial class PreferencesWindow
{
    private readonly Dictionary<string, Action> _appearanceUpdates = new();
    private bool _updatingAppearance;
    private ComboBox? _timelineLineCountCombo;

    private void PopulateAppearanceOptions()
    {
        AddAppearanceChoice(AppearanceOptions, AppDefaults.Key.AppearanceMode, "Appearance", "AppearanceModeCombo",
            new[] { ("System", AppDefaults.Appearance.System), ("Light", AppDefaults.Appearance.Light), ("Dark", AppDefaults.Appearance.Dark) },
            () => AppDefaults.Shared.AppearanceMode, value => AppDefaults.Shared.AppearanceMode = value);
        AddAppearanceToggle(AppearanceOptions, AppDefaultsSharedExtensions.SidebarShowsIconsKey,
            "SidebarShowIcons", "SidebarShowIconsToggle",
            () => AppDefaults.Shared.GetSidebarShowsIcons(), value => AppDefaults.Shared.SetSidebarShowsIcons(value));
        AddAppearanceChoice(AppearanceOptions, AppDefaultsSharedExtensions.SidebarTextSizeKey, "SidebarTextSize", "SidebarTextSizeCombo",
            Enum.GetValues<SidebarTextSize>().Select(size => (size.ToString(), size)),
            () => AppDefaults.Shared.GetSidebarTextSize(), value => AppDefaults.Shared.SetSidebarTextSize(value));
        AddAppearanceToggle(AppearanceOptions, AppDefaultsSharedExtensions.ShowTodayFeedKey, "ShowTodayFeed", "ShowTodayFeedToggle",
            () => AppDefaults.Shared.GetShowTodayFeed(), value => AppDefaults.Shared.SetShowTodayFeed(value));
        AddAppearanceToggle(AppearanceOptions, AppDefaultsSharedExtensions.ShowUnreadFeedKey, "ShowUnreadFeed", "ShowUnreadFeedToggle",
            () => AppDefaults.Shared.GetShowUnreadFeed(), value => AppDefaults.Shared.SetShowUnreadFeed(value));
        AddAppearanceToggle(AppearanceOptions, AppDefaultsSharedExtensions.ShowStarredFeedKey, "ShowStarredFeed", "ShowStarredFeedToggle",
            () => AppDefaults.Shared.GetShowStarredFeed(), value => AppDefaults.Shared.SetShowStarredFeed(value));
        AddAppearanceChoice(TimelineAppearanceOptions, AppDefaultsSharedExtensions.TimelineIconSizeKey, "TimelineIconSize", "TimelineIconSizeCombo",
            Enum.GetValues<TimelineIconSize>().Select(size => (size.ToString(), size)),
            () => AppDefaults.Shared.GetTimelineIconSize(), value => AppDefaults.Shared.SetTimelineIconSize(value));
        AddAppearanceChoice(TimelineAppearanceOptions, AppDefaultsSharedExtensions.TimelineTextSizeKey, "TimelineTextSize", "TimelineTextSizeCombo",
            Enum.GetValues<TimelineTextSize>().Select(size => (size.ToString(), size)),
            () => AppDefaults.Shared.GetTimelineTextSize(), value => AppDefaults.Shared.SetTimelineTextSize(value));
        _timelineLineCountCombo = AddAppearanceChoice(TimelineAppearanceOptions, AppDefaultsSharedExtensions.TimelineNumberOfLinesKey, "TimelineNumberOfLines", "TimelineNumberOfLinesCombo",
            Enumerable.Range(1, 6).Select(lines => (lines.ToString(System.Globalization.CultureInfo.CurrentCulture), lines)),
            () => AppDefaults.Shared.GetTimelineNumberOfLines(), value => AppDefaults.Shared.SetTimelineNumberOfLines(value), localizeOptions: false);
        AddAppearanceToggle(TimelineAppearanceOptions, AppDefaultsSharedExtensions.TimelineShowSeparatorsKey,
            "TimelineShowSeparators", "TimelineShowSeparatorsToggle",
            () => AppDefaults.Shared.GetTimelineShowSeparators(), value => AppDefaults.Shared.SetTimelineShowSeparators(value));
        AddAppearanceToggle(TimelineAppearanceOptions, AppDefaultsSharedExtensions.TimelineShowFullTitlesKey,
            "TimelineShowFullTitles", "TimelineShowFullTitlesToggle",
            () => AppDefaults.Shared.GetTimelineShowFullTitles(), value => AppDefaults.Shared.SetTimelineShowFullTitles(value));
        AddAppearanceToggle(TimelineAppearanceOptions, AppDefaultsSharedExtensions.TimelineFadeReadArticlesKey,
            "TimelineFadeReadArticles", "TimelineFadeReadArticlesToggle",
            () => AppDefaults.Shared.GetTimelineFadeReadArticles(), value => AppDefaults.Shared.SetTimelineFadeReadArticles(value));
        SyncTimelineRowsAvailability();
    }

    private ComboBox AddAppearanceChoice<T>(StackPanel panel, string key, string labelKey, string name,
        IEnumerable<(string LabelKey, T Value)> options, Func<T> read, Action<T> write, bool localizeOptions = true) where T : struct
    {
        var combo = new ComboBox { Name = name, MinWidth = 180 };
        foreach (var option in options)
            combo.Items.Add(new ComboBoxItem { Content = localizeOptions ? AppStrings.Get(option.LabelKey) : option.LabelKey, Tag = option.Value });
        Action update = () =>
        {
            _updatingAppearance = true;
            try { combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(item => item.Tag is T value && value.Equals(read())); }
            finally { _updatingAppearance = false; }
        };
        _appearanceUpdates.Add(key, update);
        update();
        combo.SelectionChanged += (_, _) =>
        {
            if (_loaded && !_updatingAppearance && combo.SelectedItem is ComboBoxItem { Tag: T value })
                SaveAppearancePreference(key, () => write(value));
        };
        AddAppearanceRow(panel, labelKey, combo);
        return combo;
    }

    private void AddAppearanceToggle(StackPanel panel, string key, string labelKey, string name,
        Func<bool> read, Action<bool> write)
    {
        var toggle = new ToggleSwitch { Name = name, MinWidth = 0 };
        Action update = () =>
        {
            _updatingAppearance = true;
            try { toggle.IsOn = read(); }
            finally { _updatingAppearance = false; }
        };
        _appearanceUpdates.Add(key, update);
        update();
        toggle.Toggled += (_, _) =>
        {
            if (_loaded && !_updatingAppearance) SaveAppearancePreference(key, () => write(toggle.IsOn));
        };
        AddAppearanceRow(panel, labelKey, toggle);
    }

    private void AddAppearanceRow(StackPanel panel, string labelKey, FrameworkElement input)
    {
        string label = AppStrings.Get(labelKey);
        AutomationProperties.SetName(input, label);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        panel.Children.Add(new Border { Style = (Style)((FrameworkElement)Content).Resources["SettingsCardStyle"], Child = grid });
    }

    private void SaveAppearancePreference(string key, Action write)
    {
        try { write(); AppearanceStatus.Text = ""; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            _appearanceUpdates[key]();
            AppearanceStatus.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                AppStrings.Get("AppearanceSaveError"), error.Message);
            App.LogException("Save appearance setting", error);
        }
    }

    private bool SyncAppearancePreference(string key)
    {
        if (!_appearanceUpdates.TryGetValue(key, out var update)) return false;
        void Apply() { if (!_closed) { update(); SyncTimelineRowsAvailability(); } }
        if (DispatcherQueue.HasThreadAccess) Apply();
        else if (!DispatcherQueue.TryEnqueue(Apply))
            throw new InvalidOperationException("The appearance preference dispatcher is unavailable.");
        return true;
    }

    private void SyncTimelineRowsAvailability()
    {
        if (_timelineLineCountCombo is not null) _timelineLineCountCombo.IsEnabled = !AppDefaults.Shared.GetTimelineShowFullTitles();
    }
}
