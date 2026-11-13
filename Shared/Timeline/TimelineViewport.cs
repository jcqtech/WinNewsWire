namespace WinNewsWire.AppShared.Timeline;

public static class TimelineViewport
{
    public static bool IsRowFullyVisible(double rowTop, double rowBottom, double visibleTop, double visibleBottom)
        => rowTop >= visibleTop && rowBottom <= visibleBottom;
}
