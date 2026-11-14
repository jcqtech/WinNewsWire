using System;
using WinNewsWire.AppShared;
using WinNewsWire.Core;

namespace WinNewsWire.Models;

public partial class FeedItem
{
    private TimelineTextSize _timelineTextSize = AppDefaults.Shared.GetTimelineTextSize();
    public double TimelineFontSize => _timelineTextSize.PointSize();
    public double TimelineMetadataFontSize => Math.Floor(TimelineFontSize * 0.9);
    private int _timelineNumberOfLines = AppDefaults.Shared.GetTimelineNumberOfLines();
    public int TimelineNumberOfLines => _timelineNumberOfLines;

    internal void UpdateTimelineNumberOfLines(int lines)
    {
        if (_timelineNumberOfLines == lines) return;
        _timelineNumberOfLines = lines;
        OnPropertyChanged(nameof(TimelineNumberOfLines));
    }

    internal void UpdateTimelineTextSize(TimelineTextSize size)
    {
        if (_timelineTextSize == size) return;
        _timelineTextSize = size;
        OnPropertyChanged(nameof(TimelineFontSize));
        OnPropertyChanged(nameof(TimelineMetadataFontSize));
    }
}
