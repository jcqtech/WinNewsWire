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

    private void PopulateAppearanceOptions()
    {
        AddAppearanceChoice(AppearanceOptions, AppDefaults.Key.AppearanceMode, "Appearance", "AppearanceModeCombo",
            new[] { ("System", AppDefaults.Appearance.System), ("Light", AppDefaults.Appearance.Light), ("Dark", AppDefaults.Appearance.Dark) },
            () => AppDefaults.Shared.AppearanceMode, value => AppDefaults.Shared.AppearanceMode = value);
    }

    private void AddAppearanceChoice<T>(StackPanel panel, string key, string labelKey, string name,
        IEnumerable<(string LabelKey, T Value)> options, Func<T> read, Action<T> write) where T : struct
    {
        var combo = new ComboBox { Name = name, MinWidth = 180 };
        foreach (var option in options)
            combo.Items.Add(new ComboBoxItem { Content = AppStrings.Get(option.LabelKey), Tag = option.Value });
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
        if (DispatcherQueue.HasThreadAccess) update();
        else if (!DispatcherQueue.TryEnqueue(() => { if (!_closed) update(); }))
            throw new InvalidOperationException("The appearance preference dispatcher is unavailable.");
        return true;
    }
}
