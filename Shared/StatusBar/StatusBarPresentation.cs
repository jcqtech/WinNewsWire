using System.Diagnostics;
using System.Globalization;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.StatusBar;

public enum StatusBarCountMode
{
    Unread = 0,
    FoldersAndFeeds = 1,
    LastRefresh = 2
}

public static class StatusBarPresentation
{
    public static StatusBarCountMode GetStatusBarCountMode(this AppDefaults defaults)
    {
        int raw = defaults.GetValue(AppDefaults.Key.StatusBarCountMode, (int)StatusBarCountMode.LastRefresh);
        if (Enum.IsDefined((StatusBarCountMode)raw)) return (StatusBarCountMode)raw;
        Trace.TraceWarning($"Unknown status-bar count mode {raw}; showing last refresh.");
        return StatusBarCountMode.LastRefresh;
    }

    public static void SetStatusBarCountMode(this AppDefaults defaults, StatusBarCountMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        defaults.SetValue(AppDefaults.Key.StatusBarCountMode, (int)mode);
    }

    public static StatusBarCountMode NextMode(StatusBarCountMode mode, bool showUnreadCounts)
        => EffectiveMode(mode, showUnreadCounts) switch
    {
        StatusBarCountMode.Unread => StatusBarCountMode.FoldersAndFeeds,
        StatusBarCountMode.FoldersAndFeeds => StatusBarCountMode.LastRefresh,
        StatusBarCountMode.LastRefresh => showUnreadCounts ? StatusBarCountMode.Unread : StatusBarCountMode.FoldersAndFeeds,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static StatusBarCountMode EffectiveMode(StatusBarCountMode mode, bool showUnreadCounts)
        => mode == StatusBarCountMode.Unread && !showUnreadCounts ? StatusBarCountMode.LastRefresh : mode;

    public static string CountText(StatusBarCountMode mode, bool showUnreadCounts, int unread, int folders, int feeds,
        DateTime? lastRefresh = null)
    {
        return EffectiveMode(mode, showUnreadCounts) switch
        {
            StatusBarCountMode.Unread => Format("StatusBarUnread", unread),
            StatusBarCountMode.FoldersAndFeeds => string.Format(CultureInfo.CurrentCulture,
                AppStrings.Get("StatusBarSubscriptions"),
                Format(folders == 1 ? "StatusBarFolder" : "StatusBarFolders", folders),
                Format(feeds == 1 ? "StatusBarFeed" : "StatusBarFeeds", feeds)),
            StatusBarCountMode.LastRefresh => lastRefresh is { } date
                ? string.Format(CultureInfo.CurrentCulture, AppStrings.Get("StatusBarUpdated"),
                    RefreshDateText(date, DateTime.Now, CultureInfo.CurrentCulture))
                : AppStrings.Get("StatusBarNotUpdated"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public static string RefreshDateText(DateTime completed, DateTime now, CultureInfo culture)
    {
        var local = completed.ToLocalTime();
        return local.ToString(local.Date == now.ToLocalTime().Date ? "t" : "g", culture);
    }

    public static string LinkText(string link)
        => link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? link[8..]
            : link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? link[7..] : link;

    private static string Format(string key, int number)
        => string.Format(CultureInfo.CurrentCulture, AppStrings.Get(key), number);
}
