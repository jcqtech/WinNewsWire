using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.AppShared;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private readonly HashSet<Border> _timelineRowSeparators = new();
    private readonly Dictionary<Grid, long> _timelineAppearanceRows = new();
    private bool _timelineAppearanceQueued;

    private void TimelineAppearanceRow_Loaded(object sender, RoutedEventArgs args)
    {
        if (_shuttingDown || sender is not Grid row || _timelineAppearanceRows.ContainsKey(row)) return;
        _timelineAppearanceRows.Add(row, row.RegisterPropertyChangedCallback(FrameworkElement.TagProperty,
            (_, _) => QueueTimelineAppearanceUpdate()));
        QueueTimelineAppearanceUpdate();
    }

    private void TimelineAppearanceRow_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Grid row && _timelineAppearanceRows.Remove(row, out long callback))
            row.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, callback);
    }

    private static ListViewItem? TimelineContainer(DependencyObject? element)
    {
        while (element is not null && element is not ListViewItem) element = VisualTreeHelper.GetParent(element);
        return element as ListViewItem;
    }

    private void TimelineRowSeparator_Loaded(object sender, RoutedEventArgs args)
    {
        if (_shuttingDown || sender is not Border separator || !_timelineRowSeparators.Add(separator)) return;
        separator.DataContextChanged += TimelineRowSeparator_DataContextChanged;
        QueueTimelineAppearanceUpdate();
    }

    private void TimelineRowSeparator_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Border separator) return;
        separator.DataContextChanged -= TimelineRowSeparator_DataContextChanged;
        _timelineRowSeparators.Remove(separator);
    }

    private void TimelineRowSeparator_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        QueueTimelineAppearanceUpdate();

    private void QueueTimelineAppearanceUpdate()
    {
        if (_shuttingDown || _timelineAppearanceQueued) return;
        _timelineAppearanceQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _timelineAppearanceQueued = false;
            if (_shuttingDown) return;
            bool enabled = !ViewModel.UseColumnLayout && !ViewModel.IsUnifiedLayout
                && Core.AppDefaults.Shared.GetTimelineShowSeparators();
            foreach (var separator in _timelineRowSeparators)
            {
                var parent = TimelineContainer(separator);
                int index = parent is not null ? ArticleListView.IndexFromContainer(parent) : -1;
                separator.Visibility = enabled && index >= 0 && index < ArticleListView.Items.Count - 1
                    && parent is ListViewItem { IsSelected: false } ? Visibility.Visible : Visibility.Collapsed;
            }
            bool fade = Core.AppDefaults.Shared.GetTimelineFadeReadArticles();
            foreach (var row in _timelineAppearanceRows.Keys)
            {
                double opacity = fade && row.Tag is true && TimelineContainer(row) is { IsSelected: false } ? 0.6 : 1;
                foreach (var child in row.Children)
                    if (child is not Border { Name: "TimelineRowSeparator" }) child.Opacity = opacity;
            }
        }))
        {
            _timelineAppearanceQueued = false;
            throw new InvalidOperationException("The timeline appearance dispatcher is unavailable.");
        }
    }

    private void DisposeTimelineAppearance()
    {
        foreach (var separator in _timelineRowSeparators)
            separator.DataContextChanged -= TimelineRowSeparator_DataContextChanged;
        _timelineRowSeparators.Clear();
        foreach (var (row, callback) in _timelineAppearanceRows)
            row.UnregisterPropertyChangedCallback(FrameworkElement.TagProperty, callback);
        _timelineAppearanceRows.Clear();
    }
}
