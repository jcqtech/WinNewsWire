using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Helpers;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private readonly HashSet<IconView> _timelineFeedIcons = [];
    private bool _timelineFeedIconsQueued;
    private bool _showsTimelineFeedIcons = true;

    private void TimelineFeedIconArticlesChanged(object? sender, EventArgs args) => QueueTimelineFeedIconsUpdate();

    private void TimelineFeedIcon_Loaded(object sender, RoutedEventArgs args)
    {
        if (_shuttingDown || sender is not IconView icon) return;
        _timelineFeedIcons.Add(icon);
        icon.Visibility = _showsTimelineFeedIcons ? Visibility.Visible : Visibility.Collapsed;
        QueueTimelineFeedIconsUpdate();
    }

    private void TimelineFeedIcon_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is IconView icon) _timelineFeedIcons.Remove(icon);
    }

    private void QueueTimelineFeedIconsUpdate()
    {
        if (_shuttingDown || _timelineFeedIconsQueued) return;
        _timelineFeedIconsQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _timelineFeedIconsQueued = false;
            if (_shuttingDown) return;
            _showsTimelineFeedIcons = ViewModel.ShowsTimelineFeedIcons;
            foreach (var icon in _timelineFeedIcons.ToArray())
            {
                icon.Visibility = _showsTimelineFeedIcons ? Visibility.Visible : Visibility.Collapsed;
                if (icon.Parent is Grid { Tag: "Feed" } column) column.ColumnSpacing = _showsTimelineFeedIcons ? 5 : 0;
            }
            foreach (var view in _timelineThumbnails.Keys.ToArray()) UpdateThumbnailSize(view);
        }))
        {
            _timelineFeedIconsQueued = false;
            throw new InvalidOperationException("The timeline feed-icon dispatcher is unavailable.");
        }
    }
}
