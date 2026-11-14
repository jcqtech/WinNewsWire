namespace WinNewsWire.AppShared;

public enum TimelineTextSize
{
    Small = 1,
    Medium = 2,
    Large = 3,
    ExtraLarge = 4,
    ExtraExtraLarge = 5
}

public static class TimelineTextSizeExtensions
{
    public static double PointSize(this TimelineTextSize size) => size switch
    {
        TimelineTextSize.Small => 12,
        TimelineTextSize.Medium => 14,
        TimelineTextSize.Large => 16,
        TimelineTextSize.ExtraLarge => 19,
        TimelineTextSize.ExtraExtraLarge => 22,
        _ => throw new ArgumentOutOfRangeException(nameof(size))
    };
}
