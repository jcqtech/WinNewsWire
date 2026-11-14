using System.Diagnostics;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared;

public static class AppDefaultsSharedExtensions
{
    public const string UnreadCountDisplayKey = "unreadCountDisplay";
    public const string TimelineThumbnailsDisplayKey = "timelineThumbnailsDisplay";
    public const string TimelineFeedIconsDisplayKey = "timelineFeedIconsDisplay";
    public const string TimelineShowSeparatorsKey = "timelineShowSeparators";

    public static bool GetTimelineShowSeparators(this AppDefaults defaults) =>
        defaults.GetValue(TimelineShowSeparatorsKey, false);

    public static void SetTimelineShowSeparators(this AppDefaults defaults, bool show) =>
        defaults.SetValue(TimelineShowSeparatorsKey, show);

    public static TimelineFeedIconsDisplay GetTimelineFeedIconsDisplay(this AppDefaults defaults)
    {
        int raw = defaults.GetValue(TimelineFeedIconsDisplayKey, (int)TimelineFeedIconsDisplay.Automatic);
        if (Enum.IsDefined((TimelineFeedIconsDisplay)raw)) return (TimelineFeedIconsDisplay)raw;
        Trace.TraceWarning($"Unknown timeline feed-icon display value {raw}; using automatic display.");
        return TimelineFeedIconsDisplay.Automatic;
    }

    public static void SetTimelineFeedIconsDisplay(this AppDefaults defaults, TimelineFeedIconsDisplay display)
    {
        if (!Enum.IsDefined(display)) throw new ArgumentOutOfRangeException(nameof(display));
        defaults.SetValue(TimelineFeedIconsDisplayKey, (int)display);
    }

    public static TimelineThumbnailsDisplay GetTimelineThumbnailsDisplay(this AppDefaults defaults)
    {
        int? raw = defaults.GetValue<int?>(TimelineThumbnailsDisplayKey);
        if (raw is null)
            return defaults.GetValue<bool?>("timelineShowsLargeThumbnails") switch
            {
                true => TimelineThumbnailsDisplay.Large,
                false => TimelineThumbnailsDisplay.Small,
                null => TimelineThumbnailsDisplay.None
            };
        if (Enum.IsDefined((TimelineThumbnailsDisplay)raw.Value)) return (TimelineThumbnailsDisplay)raw.Value;
        Trace.TraceWarning($"Unknown timeline thumbnail display value {raw}; hiding thumbnails.");
        return TimelineThumbnailsDisplay.None;
    }

    public static void SetTimelineThumbnailsDisplay(this AppDefaults defaults, TimelineThumbnailsDisplay display)
    {
        if (!Enum.IsDefined(display)) throw new ArgumentOutOfRangeException(nameof(display));
        defaults.SetValue(TimelineThumbnailsDisplayKey, (int)display);
    }

    public static UnreadCountDisplay GetUnreadCountDisplay(this AppDefaults defaults)
    {
        var raw = defaults.GetValue(UnreadCountDisplayKey, (int)UnreadCountDisplay.Count);
        if (Enum.IsDefined((UnreadCountDisplay)raw)) return (UnreadCountDisplay)raw;
        Trace.TraceWarning($"Unknown unread-count display value {raw}; showing counts.");
        return UnreadCountDisplay.Count;
    }

    public static void SetUnreadCountDisplay(this AppDefaults defaults, UnreadCountDisplay display)
    {
        if (!Enum.IsDefined(display)) throw new ArgumentOutOfRangeException(nameof(display));
        defaults.SetValue(UnreadCountDisplayKey, (int)display);
    }
}
