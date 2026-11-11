namespace WinNewsWire.AppShared.Timer;

/// <summary>Port of <c>RefreshInterval</c>.</summary>
public enum RefreshInterval
{
    Manually = 1,
    Every30Minutes = 3,
    EveryHour = 4,
    Every2Hours = 5,
    Every4Hours = 6,
    Every8Hours = 7,
}

public static class RefreshIntervalExtensions
{
    public static TimeSpan? Interval(this RefreshInterval i) => i switch
    {
        RefreshInterval.Manually => null,
        RefreshInterval.Every30Minutes => TimeSpan.FromMinutes(30),
        RefreshInterval.EveryHour => TimeSpan.FromHours(1),
        RefreshInterval.Every2Hours => TimeSpan.FromHours(2),
        RefreshInterval.Every4Hours => TimeSpan.FromHours(4),
        RefreshInterval.Every8Hours => TimeSpan.FromHours(8),
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };

    public static string DisplayString(this RefreshInterval i) => i switch
    {
        RefreshInterval.Manually => "Manually",
        RefreshInterval.Every30Minutes => "Every 30 minutes",
        RefreshInterval.EveryHour => "Every hour",
        RefreshInterval.Every2Hours => "Every 2 hours",
        RefreshInterval.Every4Hours => "Every 4 hours",
        RefreshInterval.Every8Hours => "Every 8 hours",
        _ => throw new ArgumentOutOfRangeException(nameof(i)),
    };
}
