using WinNewsWire.AppShared.Timer;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class RefreshIntervalTests
{
    [Theory]
    [InlineData(RefreshInterval.Manually, 1, null)]
    [InlineData(RefreshInterval.Every30Minutes, 3, 1800)]
    [InlineData(RefreshInterval.EveryHour, 4, 3600)]
    [InlineData(RefreshInterval.Every2Hours, 5, 7200)]
    [InlineData(RefreshInterval.Every4Hours, 6, 14400)]
    [InlineData(RefreshInterval.Every8Hours, 7, 28800)]
    public void NativeRawValuesRetainTheirDurations(RefreshInterval interval, int raw, int? seconds)
    {
        Assert.Equal(raw, (int)interval);
        Assert.Equal(seconds is { } value ? TimeSpan.FromSeconds(value) : (TimeSpan?)null, interval.Interval());
        Assert.NotEmpty(interval.DisplayString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void UnsupportedValuesCannotSilentlyDisableTheTimer(int raw)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((RefreshInterval)raw).Interval());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((RefreshInterval)raw).DisplayString());
    }
}
