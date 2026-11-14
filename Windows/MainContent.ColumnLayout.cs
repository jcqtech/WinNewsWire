using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.Core;
using WinNewsWire.Helpers;
using WinNewsWire.Models;

namespace WinNewsWire;

public sealed partial class MainContent
{
    internal const int ArticlePaneMinimumWidth = 300;
    private const int ContentPaneMargin = 4;
    internal int MinimumReaderWidthInPixels(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        int RoundedEdge(double value) => (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
        // WinUI rounds each border edge separately, not their combined thickness.
        return (int)Math.Ceiling(ArticlePaneMinimumWidth * scale) + 2 * RoundedEdge(ContentPaneMargin)
            + RoundedEdge(ContentContainer.BorderThickness.Left) + RoundedEdge(ContentContainer.BorderThickness.Right);
    }
    private const string TimelineColumnsKey = "TimelineColumns";
    private const string TimelineColumnDragKey = "WinNewsWire.TimelineColumn";
    private static readonly ArticleSortKey[] DefaultTimelineColumns =
        [ArticleSortKey.Unread, ArticleSortKey.Starred, ArticleSortKey.Feed, ArticleSortKey.Title, ArticleSortKey.Date];
    private readonly Dictionary<ArticleSortKey, ColumnDefinition> _timelineColumns = new();
    private readonly List<ArticleSortKey> _timelineColumnOrder = [];
    private readonly HashSet<Grid> _timelineColumnRows = [];
    private readonly Dictionary<ArticleSortKey, FontIcon> _timelineSortChevrons = new();
    private readonly string _timelineColumnDragToken = Guid.NewGuid().ToString("N");
    private ArticleSortKey? _draggedTimelineColumn;
    private DataTemplate? _standardTimelineTemplate;
    private Style? _standardTimelineStyle;
    private bool _appliedColumnLayout;
    private bool _appliedReadingOnly;
    private bool _applyingMainLayout;
    private double _lastColumnTimelineHeight;

    private void InitializeColumnLayout()
    {
        _standardTimelineTemplate = ArticleListView.ItemTemplate;
        _standardTimelineStyle = ArticleListView.ItemContainerStyle;
        var columns = new[] { TimelineUnreadColumn, TimelineStarredColumn, TimelineFeedColumn, TimelineTitleColumn, TimelineDateColumn };
        for (int index = 0; index < columns.Length; index++) _timelineColumns.Add(DefaultTimelineColumns[index], columns[index]);
        LoadTimelineColumns();
        foreach (var button in TimelineColumnsHeader.Children.OfType<Button>())
        {
            var key = Enum.Parse<ArticleSortKey>((string)button.Tag);
            var label = new TextBlock { Text = key.ToString(), VerticalAlignment = VerticalAlignment.Center };
            var chevron = new FontIcon { FontSize = 10, Glyph = "\uE70D", VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed };
            AutomationProperties.SetAccessibilityView(chevron, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            caption.Children.Add(label);
            caption.Children.Add(chevron);
            button.Content = caption;
            _timelineSortChevrons.Add(key, chevron);
            var menu = new MenuFlyout();
            var left = new MenuFlyoutItem { Text = "Move Column Left" };
            var right = new MenuFlyoutItem { Text = "Move Column Right" };
            var reset = new MenuFlyoutItem { Text = "Reset Columns" };
            left.Click += (_, _) => MoveTimelineColumn(key, _timelineColumnOrder.IndexOf(key) - 1);
            right.Click += (_, _) => MoveTimelineColumn(key, _timelineColumnOrder.IndexOf(key) + 1);
            reset.Click += (_, _) => { ResetTimelineColumns(); ApplyTimelineColumnOrder(); SaveTimelineColumns(); };
            menu.Items.Add(left);
            menu.Items.Add(right);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(reset);
            menu.Opening += (_, _) =>
            {
                left.IsEnabled = _timelineColumnOrder.IndexOf(key) > 0;
                right.IsEnabled = _timelineColumnOrder.IndexOf(key) < _timelineColumnOrder.Count - 1;
            };
            button.ContextFlyout = menu;
            button.CanDrag = true;
            button.AllowDrop = true;
            button.DragStarting += (_, args) =>
            {
                _draggedTimelineColumn = key;
                args.Data.Properties[TimelineColumnDragKey] = _timelineColumnDragToken;
                args.Data.RequestedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            };
            button.DropCompleted += (_, _) => _draggedTimelineColumn = null;
            button.DragOver += (_, args) =>
            {
                if (!IsTimelineColumnDrag(args)) return;
                args.AcceptedOperation = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                args.Handled = true;
            };
            button.Drop += (_, args) =>
            {
                if (!IsTimelineColumnDrag(args) || _draggedTimelineColumn is not { } dragged) return;
                MoveTimelineColumn(dragged, _timelineColumnOrder.IndexOf(key));
                _draggedTimelineColumn = null;
                args.Handled = true;
            };
        }
        UpdateTimelineColumnHeaders();
    }

    private bool IsTimelineColumnDrag(DragEventArgs args) => _draggedTimelineColumn is not null
        && args.DataView.Properties.TryGetValue(TimelineColumnDragKey, out var token)
        && token is string value && value == _timelineColumnDragToken;

    private void ResetTimelineColumns()
    {
        _timelineColumnOrder.Clear();
        _timelineColumnOrder.AddRange(DefaultTimelineColumns);
        var widths = new[] { 22d, 22d, 180d, 400d, 130d };
        for (int index = 0; index < widths.Length; index++)
            _timelineColumns[DefaultTimelineColumns[index]].Width = new GridLength(widths[index]);
    }

    private void LoadTimelineColumns()
    {
        ResetTimelineColumns();
        if (AppDefaults.Shared.GetValue<TimelineColumnsState>(TimelineColumnsKey) is { } saved)
        {
            var order = (saved.Order ?? []).Where(_timelineColumns.ContainsKey).Distinct().ToArray();
            _timelineColumnOrder.Clear();
            _timelineColumnOrder.AddRange(order.Concat(DefaultTimelineColumns.Except(order)));
            foreach (var (key, width) in saved.Widths ?? new())
            {
                if (!_timelineColumns.TryGetValue(key, out var column) || !double.IsFinite(width))
                {
                    System.Diagnostics.Trace.TraceWarning($"Invalid saved timeline column: {key}.");
                    continue;
                }
                column.Width = new GridLength(Math.Clamp(width, column.MinWidth, column.MaxWidth));
            }
        }
        ApplyTimelineColumnOrder();
    }

    private void SaveTimelineColumns()
    {
        try
        {
            AppDefaults.Shared.SetValue(TimelineColumnsKey, new TimelineColumnsState
            {
                Order = _timelineColumnOrder.ToArray(),
                Widths = _timelineColumns.ToDictionary(pair => pair.Key, pair => pair.Value.Width.Value)
            });
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            App.LogException("Save timeline columns", error);
            ViewModel.StatusMessage = "Could not save timeline columns: " + error.Message;
        }
    }

    private void MoveTimelineColumn(ArticleSortKey key, int destination)
    {
        if (destination < 0 || destination >= _timelineColumnOrder.Count) throw new ArgumentOutOfRangeException(nameof(destination));
        _timelineColumnOrder.Remove(key);
        _timelineColumnOrder.Insert(destination, key);
        ApplyTimelineColumnOrder();
        SaveTimelineColumns();
    }

    private void ApplyTimelineColumnOrder()
    {
        TimelineColumnsHeader.ColumnDefinitions.Clear();
        foreach (var key in _timelineColumnOrder) TimelineColumnsHeader.ColumnDefinitions.Add(_timelineColumns[key]);
        foreach (var cell in TimelineColumnsHeader.Children.OfType<FrameworkElement>())
            if (cell.Tag is string key) Grid.SetColumn(cell, _timelineColumnOrder.IndexOf(Enum.Parse<ArticleSortKey>(key)));
        foreach (var row in _timelineColumnRows) BindTimelineColumnRow(row);
        UpdateTimelineTableWidth();
        UpdateTimelineColumnHeaders();
    }

    private void BindTimelineColumnRow(Grid row)
    {
        row.ColumnDefinitions.Clear();
        foreach (var key in _timelineColumnOrder)
        {
            var column = new ColumnDefinition();
            BindingOperations.SetBinding(column, ColumnDefinition.WidthProperty,
                new Binding { Source = _timelineColumns[key], Path = new PropertyPath(nameof(ColumnDefinition.Width)), Mode = BindingMode.OneWay });
            row.ColumnDefinitions.Add(column);
        }
        foreach (var cell in row.Children.OfType<FrameworkElement>())
            if (cell.Tag is string key) Grid.SetColumn(cell, _timelineColumnOrder.IndexOf(Enum.Parse<ArticleSortKey>(key)));
    }

    private void TimelineColumnRow_Loaded(object sender, RoutedEventArgs args)
    {
        var row = (Grid)sender;
        _timelineColumnRows.Add(row);
        TimelineAppearanceRow_Loaded(sender, args);
        BindTimelineColumnRow(row);
    }

    private void TimelineColumnRow_Unloaded(object sender, RoutedEventArgs args)
    {
        _timelineColumnRows.Remove((Grid)sender);
        TimelineAppearanceRow_Unloaded(sender, args);
    }

    private void TimelineColumn_WidthChanged(object? sender, EventArgs args) => UpdateTimelineTableWidth();
    private void TimelineColumn_ResizeCompleted(object? sender, EventArgs args) => SaveTimelineColumns();

    private void UpdateTimelineTableWidth()
    {
        TimelineTableContainer.Width = ViewModel.UseColumnLayout ? _timelineColumns.Values.Sum(column => column.Width.Value) : double.NaN;
    }

    private void TimelineColumnSort_Click(object sender, RoutedEventArgs args)
    {
        var key = Enum.Parse<ArticleSortKey>((string)((Button)sender).Tag);
        bool descending = ViewModel.SortParameters.Key == key ? !ViewModel.SortParameters.Descending : !key.SortsAscendingFirst();
        try { ViewModel.SetSortParameters(new(key, descending)); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        {
            App.LogException("Save timeline sort", error);
            ViewModel.StatusMessage = "Could not save timeline sorting: " + error.Message;
        }
        UpdateTimelineColumnHeaders();
    }

    private void UpdateTimelineColumnHeaders()
    {
        foreach (var button in TimelineColumnsHeader.Children.OfType<Button>())
        {
            var key = Enum.Parse<ArticleSortKey>((string)button.Tag);
            bool selected = ViewModel.SortParameters.Key == key;
            string direction = ViewModel.SortParameters.Descending ? "descending" : "ascending";
            if (_timelineSortChevrons.TryGetValue(key, out var chevron))
            {
                chevron.Glyph = ViewModel.SortParameters.Descending ? "\uE70D" : "\uE70E";
                chevron.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
            AutomationProperties.SetName(button, "Sort by " + key + (selected ? ", " + direction : ""));
        }
    }

    private void ApplyMainLayout(bool rememberGeometry = true)
    {
        if (_shuttingDown || _restoringLayout || _applyingMainLayout || XamlRoot is null) return;
        _applyingMainLayout = true;
        ++_sidebarAnimationVersion;
        try
        {
            if (rememberGeometry && !_appliedReadingOnly)
            {
                if (_appliedColumnLayout && ColumnTimelineRow.Height.IsAbsolute && ColumnTimelineRow.ActualHeight > 0)
                    _lastColumnTimelineHeight = ColumnTimelineRow.ActualHeight;
                else if (!_appliedColumnLayout && ArticleListColumn.Width.IsAbsolute && ArticleListColumn.Width.Value > 0)
                    _lastArticleListWidth = ArticleListColumn.Width.Value;
            }
            bool column = ViewModel.UseColumnLayout;
            bool readingOnly = ViewModel.IsUnifiedLayout;
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            bool focusTimeline = IsWithin(focused, ArticleListView);
            if (column != _appliedColumnLayout)
            {
                var wasSynchronizing = _syncingArticleSelection;
                _syncingArticleSelection = true;
                try
                {
                    if (column) LoadTimelineColumns();
                    ArticleListView.ItemTemplate = column ? (DataTemplate)Resources["ColumnArticleTemplate"] : _standardTimelineTemplate;
                    ArticleListView.ItemContainerStyle = column ? (Style)Resources["ColumnTimelineItemStyle"] : _standardTimelineStyle;
                }
                finally { _syncingArticleSelection = wasSynchronizing; }
            }
            _appliedColumnLayout = column;
            _appliedReadingOnly = readingOnly;
            SetSidebarVisibility(!readingOnly && ViewModel.IsSidebarVisible);
            TimelinePanel.Visibility = readingOnly ? Visibility.Collapsed : Visibility.Visible;
            ArticleSplitter.Visibility = readingOnly ? Visibility.Collapsed : Visibility.Visible;
            ReadingAreaColumn.MinWidth = column || readingOnly ? ArticlePaneMinimumWidth : 580;
            bool singleColumn = column || readingOnly;
            ArticleListColumn.MaxWidth = singleColumn ? double.PositiveInfinity : 600;
            ArticleListColumn.MinWidth = singleColumn ? 0 : 280;
            ArticleListColumn.Width = singleColumn ? new GridLength(1, GridUnitType.Star) : new GridLength(_lastArticleListWidth);
            ArticleDividerColumn.Width = singleColumn ? new GridLength(0) : GridLength.Auto;
            ArticleDetailColumn.MinWidth = singleColumn ? 0 : ArticlePaneMinimumWidth;
            ArticleDetailColumn.Width = singleColumn ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(ArticleDetailPanel, singleColumn ? 0 : 2);
            Grid.SetRow(ArticleDetailPanel, column && !readingOnly ? 2 : 0);
            Grid.SetColumn(ArticleSplitter, column ? 0 : 1);
            Grid.SetRow(ArticleSplitter, column ? 1 : 0);
            ArticleSplitter.Width = column ? double.NaN : 8;
            ArticleSplitter.Height = column ? 8 : double.NaN;
            ArticleDividerLine.Width = column ? double.NaN : 1;
            ArticleDividerLine.Height = column ? 1 : double.NaN;
            ArticleDividerLine.HorizontalAlignment = column ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            ArticleDividerLine.VerticalAlignment = column ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            AutomationProperties.SetName(ArticleSplitter, column ? "Resize timeline height" : "Resize article list");
            AutomationProperties.SetHelpText(ArticleSplitter, column ? "Press up or down arrow to resize" : "Press left or right arrow to resize");
            ColumnTimelineRow.MinHeight = column && !readingOnly ? 120 : 0;
            ColumnTimelineRow.Height = column && !readingOnly ? new GridLength(120) : new GridLength(1, GridUnitType.Star);
            ColumnDividerRow.Height = column && !readingOnly ? GridLength.Auto : new GridLength(0);
            ColumnDetailRow.MinHeight = column && !readingOnly ? 200 : 0;
            ColumnDetailRow.Height = column && !readingOnly ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            if (column && !readingOnly) ApplyColumnTimelineHeight();
            TimelineToolbar.Visibility = _toolbarConfiguration.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            TimelineColumnsHeader.Visibility = column ? Visibility.Visible : Visibility.Collapsed;
            UpdateTimelineHeaderInsets();
            TimelineTableContainer.HorizontalAlignment = column ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            TimelineHorizontalScroll.HorizontalContentAlignment = column ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            TimelineFooter.Visibility = column ? Visibility.Collapsed : Visibility.Visible;
            CombinedTimelineStatus.Visibility = column ? Visibility.Visible : Visibility.Collapsed;
            CombinedTimelineStatusColumn.Width = column ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            DetailFooterContent.ColumnSpacing = column ? 12 : 0;
            ArticleDetailStatus.TextAlignment = column ? TextAlignment.Right : TextAlignment.Left;
            WebPageStatus.TextAlignment = column ? TextAlignment.Right : TextAlignment.Left;
            TimelineHorizontalScroll.HorizontalScrollBarVisibility = column ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            TimelineHorizontalScroll.HorizontalScrollMode = column ? ScrollMode.Enabled : ScrollMode.Disabled;
            UpdateTimelineTableWidth();
            UpdateTimelineColumnHeaders();
            SyncArticleSelection();
            if (focusTimeline) ArticleListView.Focus(FocusState.Programmatic);
            _autoCollapsedArticleList = false;
            _wasAboveSidebarBreakpoint = null;
            _wasAboveArticleListBreakpoint = null;
        }
        finally { _applyingMainLayout = false; }
        ApplyResponsiveLayout(MainContentGrid.ActualWidth);
        ++_sidebarAnimationVersion;
        SetSidebarVisibility(!ViewModel.IsUnifiedLayout && ViewModel.IsSidebarVisible);
        QueueToolbarUpdate();
    }

    private double MaximumColumnTimelineHeight => Math.Max(120, ArticleContentGrid.ActualHeight - 208);

    private void TimelineHeader_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateTimelineHeaderInsets();
        QueueToolbarUpdate();
    }

    private void UpdateTimelineHeaderInsets()
    {
        if (_shuttingDown) return;
        double header = TimelineToolbar.Visibility == Visibility.Visible
            ? (TimelineToolbar.ActualHeight > 0 ? TimelineToolbar.ActualHeight : _headerHeight) : 0;
        bool column = ViewModel.UseColumnLayout;
        TimelineColumnsHeader.Margin = new Thickness(0, column ? header : 0, 0, 0);
        ArticleListView.Padding = new Thickness(0,
            header + (column ? TimelineColumnsHeader.ActualHeight : 0), 0, column ? 0 : 4);
    }

    private void ApplyColumnTimelineHeight()
    {
        if (ArticleContentGrid.ActualHeight <= 0) return;
        double height = _lastColumnTimelineHeight > 0 ? _lastColumnTimelineHeight : Math.Floor(ArticleContentGrid.ActualHeight * 0.4);
        ColumnTimelineRow.Height = new GridLength(Math.Clamp(height, 120, MaximumColumnTimelineHeight));
        _lastColumnTimelineHeight = ColumnTimelineRow.Height.Value;
    }

    private void ArticleContentGrid_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_applyingMainLayout && !_restoringLayout && !_shuttingDown && _appliedColumnLayout && !_appliedReadingOnly)
            ApplyColumnTimelineHeight();
    }

    private void TimelineHorizontalScroll_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_appliedColumnLayout || _appliedReadingOnly || args.NewSize.Width <= 0) return;
        double others = _timelineColumns.Where(pair => pair.Key != ArticleSortKey.Title).Sum(pair => pair.Value.Width.Value);
        TimelineTitleColumn.Width = new GridLength(Math.Clamp(args.NewSize.Width - others, TimelineTitleColumn.MinWidth, TimelineTitleColumn.MaxWidth));
        UpdateTimelineTableWidth();
    }
}
