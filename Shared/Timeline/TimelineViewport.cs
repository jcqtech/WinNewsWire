namespace WinNewsWire.AppShared.Timeline;

public static class TimelineViewport
{
    public static bool IsRowFullyVisible(double rowTop, double rowBottom, double visibleTop, double visibleBottom)
        => rowTop >= visibleTop && rowBottom <= visibleBottom;

    public static double ScrollDelta(double rowTop, double rowBottom, double visibleTop, double visibleBottom,
        bool center = false)
    {
        if (rowBottom - rowTop >= visibleBottom - visibleTop) return rowTop - visibleTop;
        if (center || rowBottom > visibleBottom) return (rowTop + rowBottom - visibleTop - visibleBottom) / 2;
        if (rowTop < visibleTop) return rowTop - visibleTop;
        return 0;
    }
}
