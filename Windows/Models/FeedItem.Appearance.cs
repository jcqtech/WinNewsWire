using System;
using WinNewsWire.AppShared;
using WinNewsWire.Core;

namespace WinNewsWire.Models;

public partial class FeedItem
{
    private TimelineTextSize _timelineTextSize = AppDefaults.Shared.GetTimelineTextSize();
    public double TimelineFontSize => _timelineTextSize.PointSize();
    public double TimelineMetadataFontSize => Math.Floor(TimelineFontSize * 0.9);

    internal void UpdateTimelineTextSize(TimelineTextSize size)
    {
        if (_timelineTextSize == size) return;
        _timelineTextSize = size;
        OnPropertyChanged(nameof(TimelineFontSize));
        OnPropertyChanged(nameof(TimelineMetadataFontSize));
    }
}
