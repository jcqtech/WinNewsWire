namespace WinNewsWire.AppShared;

public enum TimelineIconSize
{
    Small = 1,
    Medium = 2,
    Large = 3
}

public static class TimelineIconSizeExtensions
{
    public static double Dimension(this TimelineIconSize size) => size switch
    {
        TimelineIconSize.Small => 24,
        TimelineIconSize.Medium => 36,
        TimelineIconSize.Large => 48,
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };
}
