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
    private bool _timelineAppearanceQueued;

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
                DependencyObject? parent = separator;
                while (parent is not null && parent is not ListViewItem) parent = VisualTreeHelper.GetParent(parent);
                int index = parent is ListViewItem container ? ArticleListView.IndexFromContainer(container) : -1;
                separator.Visibility = enabled && index >= 0 && index < ArticleListView.Items.Count - 1
                    && parent is ListViewItem { IsSelected: false } ? Visibility.Visible : Visibility.Collapsed;
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
    }
}
