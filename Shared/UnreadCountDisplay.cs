using System.Globalization;

namespace WinNewsWire.AppShared;

public enum UnreadCountDisplay
{
    Count = 0,
    Dot = 1,
    Hidden = 2
}

public static class UnreadCountDisplayExtensions
{
    public static string? TextFor(this UnreadCountDisplay display, int unreadCount)
    {
        if (unreadCount <= 0) return null;
        return display switch
        {
            UnreadCountDisplay.Count => unreadCount.ToString("N0", CultureInfo.CurrentCulture),
            UnreadCountDisplay.Dot => "\u2022",
            UnreadCountDisplay.Hidden => null,
            _ => throw new ArgumentOutOfRangeException(nameof(display))
        };
    }

    public static bool ShowsDot(this UnreadCountDisplay display, int unreadCount)
        => display == UnreadCountDisplay.Dot && unreadCount > 0;

    public static string? AccessibilityTextFor(this UnreadCountDisplay display, int unreadCount)
    {
        if (unreadCount <= 0) return null;
        var unreadLabel = AppStrings.Get("Unread");
        return display switch
        {
            UnreadCountDisplay.Count => $"{unreadCount} {unreadLabel}",
            UnreadCountDisplay.Dot => unreadLabel,
            UnreadCountDisplay.Hidden => null,
            _ => throw new ArgumentOutOfRangeException(nameof(display))
        };
    }
}
