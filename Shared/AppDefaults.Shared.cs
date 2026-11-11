using System.Diagnostics;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared;

public static class AppDefaultsSharedExtensions
{
    public const string UnreadCountDisplayKey = "unreadCountDisplay";

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
