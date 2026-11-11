using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.Toolbar;

namespace WinNewsWire.Dialogs;

internal sealed class ToolbarCustomizationDialog : ContentDialog
{
    private sealed class Choice(string identifier)
    {
        public string Identifier { get; } = identifier;
        public string Label => ToolbarConfiguration.Label(Identifier);
    }

    private readonly ObservableCollection<Choice> _items = new();
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single, Height = 224
    };
    private readonly ComboBox _available = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _visible = new() { Content = AppStrings.Get("ShowToolbar") };
    private readonly ComboBox _display = new() { Header = AppStrings.Get("ToolbarDisplayMode"), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _earlier = new() { Content = AppStrings.Get("ToolbarMoveEarlier") };
    private readonly Button _later = new() { Content = AppStrings.Get("ToolbarMoveLater") };
    private readonly Button _hide = new() { Content = AppStrings.Get("ToolbarHideItem") };
    private readonly Button _show = new() { Content = AppStrings.Get("ToolbarShowItem") };
    private ToolbarConfiguration _basis = ToolbarConfiguration.Default;

    public ToolbarCustomizationDialog(ToolbarConfiguration configuration, Func<ToolbarConfiguration, string?> save)
    {
        Title = AppStrings.Get("CustomizeToolbar");
        PrimaryButtonText = AppStrings.Get("ToolbarSave");
        CloseButtonText = AppStrings.Get("Cancel");
        DefaultButton = ContentDialogButton.Primary;
        // This bounded command list does not need the native virtualized insertion/scroll path.
        _list.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["ToolbarCommandItemsPanel"];
        _list.ItemContainerStyle = new Style(typeof(ListViewItem))
        {
            Setters =
            {
                new Setter(Control.PaddingProperty, new Thickness(12, 0, 32, 0)),
                new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)
            }
        };
        _list.ItemsSource = _items;
        _list.DisplayMemberPath = nameof(Choice.Label);
        _available.DisplayMemberPath = nameof(Choice.Label);
        AutomationProperties.SetName(_list, AppStrings.Get("ToolbarShownItems"));
        AutomationProperties.SetName(_available, AppStrings.Get("ToolbarAvailableItems"));
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        foreach (var mode in Enum.GetValues<ToolbarDisplayMode>())
            _display.Items.Add(new ComboBoxItem { Content = AppStrings.Get("ToolbarDisplay_" + mode), Tag = mode });

        var movement = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        movement.Children.Add(_earlier);
        movement.Children.Add(_later);
        movement.Children.Add(_hide);
        var add = new Grid { ColumnSpacing = 8 };
        add.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        add.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        add.Children.Add(_available);
        Grid.SetColumn(_show, 1);
        add.Children.Add(_show);
        var reset = new Button { Content = AppStrings.Get("ToolbarRestoreDefaults") };
        var content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 20, 0) };
        content.Children.Add(new TextBlock { Text = AppStrings.Get("ToolbarCustomizationHelp"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(_visible);
        content.Children.Add(_display);
        content.Children.Add(new TextBlock { Text = AppStrings.Get("ToolbarShownItems") });
        content.Children.Add(_list);
        content.Children.Add(movement);
        content.Children.Add(new TextBlock { Text = AppStrings.Get("ToolbarAvailableItems") });
        content.Children.Add(add);
        content.Children.Add(reset);
        content.Children.Add(_error);
        Content = new ScrollViewer
        {
            Content = content, MinWidth = 420, MaxWidth = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled
        };

        _list.SelectionChanged += (_, _) => UpdateButtons();
        _available.SelectionChanged += (_, _) => UpdateButtons();
        _earlier.Click += (_, _) => Move(-1);
        _later.Click += (_, _) => Move(1);
        _hide.Click += (_, _) =>
        {
            int index = _list.SelectedIndex;
            if (index < 0) return;
            _items.RemoveAt(index);
            _list.SelectedIndex = Math.Min(index, _items.Count - 1);
            UpdateAvailable();
        };
        _show.Click += (_, _) =>
        {
            if (_available.SelectedItem is not Choice choice) return;
            int index = _list.SelectedIndex < 0 ? _items.Count : _list.SelectedIndex + 1;
            _items.Insert(index, choice);
            _list.SelectedIndex = index;
            UpdateAvailable();
            RevealSelectedItem();
        };
        reset.Click += (_, _) => Load(ToolbarConfiguration.Default);
        PrimaryButtonClick += (_, args) =>
        {
            var next = _basis.WithHeaderItems(_items.Select(item => item.Identifier))
                .WithVisibility(_visible.IsChecked == true)
                .WithDisplayMode((ToolbarDisplayMode)((ComboBoxItem)_display.SelectedItem).Tag);
            string? error = save(next);
            args.Cancel = error is not null;
            _error.Text = error ?? "";
        };
        Load(configuration);
    }

    private void Load(ToolbarConfiguration configuration)
    {
        _basis = configuration;
        _items.Clear();
        foreach (var id in configuration.Items.Where(ToolbarConfiguration.IsHeaderItem)) _items.Add(new(id));
        _visible.IsChecked = configuration.IsVisible;
        _display.SelectedIndex = (int)configuration.DisplayMode;
        _list.SelectedIndex = _items.Count > 0 ? 0 : -1;
        UpdateAvailable();
    }

    private void Move(int direction)
    {
        int index = _list.SelectedIndex;
        int destination = index + direction;
        if (index < 0 || destination < 0 || destination >= _items.Count) return;
        _items.Move(index, destination);
        _list.SelectedIndex = destination;
        RevealSelectedItem();
        UpdateButtons();
    }

    private void RevealSelectedItem()
    {
        _list.UpdateLayout();
        if (_list.ContainerFromItem(_list.SelectedItem) is UIElement row)
            row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        else
        {
            var error = new InvalidOperationException("The selected toolbar command could not be laid out.");
            App.LogException("Reveal toolbar command", error);
            _error.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, AppStrings.Get("ToolbarCustomizeError"), error.Message);
        }
    }

    private void UpdateAvailable()
    {
        string? selected = (_available.SelectedItem as Choice)?.Identifier;
        var available = ToolbarConfiguration.AllowedItems.Where(ToolbarConfiguration.IsHeaderItem)
            .Where(id => !_items.Any(item => item.Identifier == id)).Select(id => new Choice(id)).ToArray();
        _available.ItemsSource = available;
        _available.SelectedItem = available.FirstOrDefault(item => item.Identifier == selected) ?? available.FirstOrDefault();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _earlier.IsEnabled = _list.SelectedIndex > 0;
        _later.IsEnabled = _list.SelectedIndex >= 0 && _list.SelectedIndex < _items.Count - 1;
        _hide.IsEnabled = _list.SelectedIndex >= 0;
        _show.IsEnabled = _available.SelectedItem is Choice;
    }
}
