using System.Diagnostics;
using System.Globalization;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.StatusBar;

public enum StatusBarCountMode
{
    Unread = 0,
    FoldersAndFeeds = 1
}

public static class StatusBarPresentation
{
    public static StatusBarCountMode GetStatusBarCountMode(this AppDefaults defaults)
    {
        int raw = defaults.GetValue(AppDefaults.Key.StatusBarCountMode, (int)StatusBarCountMode.Unread);
        if (Enum.IsDefined((StatusBarCountMode)raw)) return (StatusBarCountMode)raw;
        Trace.TraceWarning($"Unknown status-bar count mode {raw}; showing unread counts.");
        return StatusBarCountMode.Unread;
    }

    public static void SetStatusBarCountMode(this AppDefaults defaults, StatusBarCountMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        defaults.SetValue(AppDefaults.Key.StatusBarCountMode, (int)mode);
    }

    public static StatusBarCountMode NextMode(StatusBarCountMode mode, bool showUnreadCounts) => mode switch
    {
        StatusBarCountMode.Unread => StatusBarCountMode.FoldersAndFeeds,
        StatusBarCountMode.FoldersAndFeeds => showUnreadCounts ? StatusBarCountMode.Unread : StatusBarCountMode.FoldersAndFeeds,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static string CountText(StatusBarCountMode mode, bool showUnreadCounts, int unread, int folders, int feeds)
    {
        if (!showUnreadCounts) mode = StatusBarCountMode.FoldersAndFeeds;
        return mode switch
        {
            StatusBarCountMode.Unread => Format("StatusBarUnread", unread),
            StatusBarCountMode.FoldersAndFeeds => string.Format(CultureInfo.CurrentCulture,
                AppStrings.Get("StatusBarSubscriptions"),
                Format(folders == 1 ? "StatusBarFolder" : "StatusBarFolders", folders),
                Format(feeds == 1 ? "StatusBarFeed" : "StatusBarFeeds", feeds)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public static string LinkText(string link)
        => link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? link[8..]
            : link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? link[7..] : link;

    private static string Format(string key, int number)
        => string.Format(CultureInfo.CurrentCulture, AppStrings.Get(key), number);
}
