namespace WinNewsWire.AppShared.Timer;

/// <summary>Port of <c>RefreshInterval</c>.</summary>
public enum RefreshInterval
{
    Manually = 0,
    Every10Minutes = 1,
    Every30Minutes = 2,
    EveryHour = 3,
    Every2Hours = 4,
    Every4Hours = 5,
    Every8Hours = 6,
}

public static class RefreshIntervalExtensions
{
    public static TimeSpan? Interval(this RefreshInterval i) => i switch
    {
        RefreshInterval.Manually => null,
        RefreshInterval.Every10Minutes => TimeSpan.FromMinutes(10),
        RefreshInterval.Every30Minutes => TimeSpan.FromMinutes(30),
        RefreshInterval.EveryHour => TimeSpan.FromHours(1),
        RefreshInterval.Every2Hours => TimeSpan.FromHours(2),
        RefreshInterval.Every4Hours => TimeSpan.FromHours(4),
        RefreshInterval.Every8Hours => TimeSpan.FromHours(8),
        _ => null,
    };

    public static string DisplayString(this RefreshInterval i) => i switch
    {
        RefreshInterval.Manually => "Manually",
        RefreshInterval.Every10Minutes => "Every 10 minutes",
        RefreshInterval.Every30Minutes => "Every 30 minutes",
        RefreshInterval.EveryHour => "Every hour",
        RefreshInterval.Every2Hours => "Every 2 hours",
        RefreshInterval.Every4Hours => "Every 4 hours",
        RefreshInterval.Every8Hours => "Every 8 hours",
        _ => "",
    };
}
