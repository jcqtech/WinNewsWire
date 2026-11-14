using System.Diagnostics;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared;

public static class AppDefaultsSharedExtensions
{
    public const string UnreadCountDisplayKey = "unreadCountDisplay";
    public const string TimelineThumbnailsDisplayKey = "timelineThumbnailsDisplay";
    public const string TimelineFeedIconsDisplayKey = "timelineFeedIconsDisplay";
    public const string TimelineShowSeparatorsKey = "timelineShowSeparators";
    public const string SidebarShowsIconsKey = "sidebarShowsIcons";
    public const string SidebarTextSizeKey = "sidebarTextSize";
    public const string ShowTodayFeedKey = "showTodayFeed";
    public const string ShowUnreadFeedKey = "showUnreadFeed";
    public const string ShowStarredFeedKey = "showStarredFeed";
    public const string TimelineIconSizeKey = "timelineIconSize";
    public const string TimelineTextSizeKey = "timelineTextSize";
    public const string TimelineNumberOfLinesKey = "timelineNumberOfLines";
    public const string TimelineShowFullTitlesKey = "timelineShowFullTitles";
    public static bool GetTimelineShowFullTitles(this AppDefaults defaults) => defaults.GetValue(TimelineShowFullTitlesKey, false);
    public static void SetTimelineShowFullTitles(this AppDefaults defaults, bool show) => defaults.SetValue(TimelineShowFullTitlesKey, show);

    public static int GetTimelineNumberOfLines(this AppDefaults defaults)
    {
        int value = defaults.GetValue(TimelineNumberOfLinesKey, 3);
        if (value is >= 1 and <= 6) return value;
        Trace.TraceWarning($"Unknown timeline line count {value}; using three lines.");
        return 3;
    }

    public static void SetTimelineNumberOfLines(this AppDefaults defaults, int lines)
    {
        if (lines is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(lines));
        defaults.SetValue(TimelineNumberOfLinesKey, lines);
    }

    public static TimelineTextSize GetTimelineTextSize(this AppDefaults defaults)
    {
        var value = (TimelineTextSize)defaults.GetValue(TimelineTextSizeKey, (int)TimelineTextSize.Medium);
        if (Enum.IsDefined(value)) return value;
        Trace.TraceWarning($"Unknown timeline text size {(int)value}; using medium.");
        return TimelineTextSize.Medium;
    }

    public static void SetTimelineTextSize(this AppDefaults defaults, TimelineTextSize size)
    {
        if (!Enum.IsDefined(size)) throw new ArgumentOutOfRangeException(nameof(size));
        defaults.SetValue(TimelineTextSizeKey, (int)size);
    }

    public static TimelineIconSize GetTimelineIconSize(this AppDefaults defaults)
    {
        var value = (TimelineIconSize)defaults.GetValue(TimelineIconSizeKey, (int)TimelineIconSize.Large);
        if (Enum.IsDefined(value)) return value;
        Trace.TraceWarning($"Unknown timeline icon size {(int)value}; using medium.");
        return TimelineIconSize.Medium;
    }

    public static void SetTimelineIconSize(this AppDefaults defaults, TimelineIconSize size)
    {
        if (!Enum.IsDefined(size)) throw new ArgumentOutOfRangeException(nameof(size));
        defaults.SetValue(TimelineIconSizeKey, (int)size);
    }

    public static bool GetShowTodayFeed(this AppDefaults defaults) => defaults.GetValue(ShowTodayFeedKey, true);
    public static bool GetShowUnreadFeed(this AppDefaults defaults) => defaults.GetValue(ShowUnreadFeedKey, true);
    public static bool GetShowStarredFeed(this AppDefaults defaults) => defaults.GetValue(ShowStarredFeedKey, true);
    public static void SetShowTodayFeed(this AppDefaults defaults, bool show) => defaults.SetValue(ShowTodayFeedKey, show);
    public static void SetShowUnreadFeed(this AppDefaults defaults, bool show) => defaults.SetValue(ShowUnreadFeedKey, show);
    public static void SetShowStarredFeed(this AppDefaults defaults, bool show) => defaults.SetValue(ShowStarredFeedKey, show);

    public static SidebarTextSize GetSidebarTextSize(this AppDefaults defaults)
    {
        var value = (SidebarTextSize)defaults.GetValue(SidebarTextSizeKey, 0);
        if (Enum.IsDefined(value)) return value;
        Trace.TraceWarning($"Unknown sidebar text size {(int)value}; following the system.");
        return SidebarTextSize.System;
    }

    public static void SetSidebarTextSize(this AppDefaults defaults, SidebarTextSize size)
    {
        if (!Enum.IsDefined(size)) throw new ArgumentOutOfRangeException(nameof(size));
        defaults.SetValue(SidebarTextSizeKey, (int)size);
    }

    public static bool GetSidebarShowsIcons(this AppDefaults defaults) =>
        defaults.GetValue(SidebarShowsIconsKey, true);

    public static void SetSidebarShowsIcons(this AppDefaults defaults, bool show) =>
        defaults.SetValue(SidebarShowsIconsKey, show);

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
