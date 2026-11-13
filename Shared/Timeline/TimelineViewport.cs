namespace WinNewsWire.AppShared.Timeline;

public static class TimelineViewport
{
    public static bool IsRowFullyVisible(double rowTop, double rowBottom, double visibleTop, double visibleBottom)
        => rowTop >= visibleTop && rowBottom <= visibleBottom;

    public static double ScrollDelta(double rowTop, double rowBottom, double visibleTop, double visibleBottom)
    {
        if (rowTop < visibleTop) return rowTop - visibleTop;
        if (rowBottom > visibleBottom) return rowBottom - visibleBottom;
        return 0;
    }
}
