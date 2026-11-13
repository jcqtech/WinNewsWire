using System.Diagnostics;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared;

public static class AppDefaultsSharedExtensions
{
    public const string UnreadCountDisplayKey = "unreadCountDisplay";
    public const string TimelineThumbnailsDisplayKey = "timelineThumbnailsDisplay";

    public static TimelineThumbnailsDisplay GetTimelineThumbnailsDisplay(this AppDefaults defaults)
    {
        int? raw = defaults.GetValue<int?>(TimelineThumbnailsDisplayKey);
        if (raw is null)
            return defaults.GetValue("timelineShowsLargeThumbnails", false)
                ? TimelineThumbnailsDisplay.Large : TimelineThumbnailsDisplay.Small;
        if (Enum.IsDefined((TimelineThumbnailsDisplay)raw.Value)) return (TimelineThumbnailsDisplay)raw.Value;
        Trace.TraceWarning($"Unknown timeline thumbnail display value {raw}; showing small thumbnails.");
        return TimelineThumbnailsDisplay.Small;
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
