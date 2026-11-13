using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class TimelineThumbnailsDisplayTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "thumbnail-display-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "defaults.json");

    [Theory]
    [InlineData(false, TimelineThumbnailsDisplay.Small)]
    [InlineData(true, TimelineThumbnailsDisplay.Large)]
    public void TheLegacyModeSurvivesUntilAnExplicitDisplayChoice(bool legacyLarge, TimelineThumbnailsDisplay expected)
    {
        var defaults = new AppDefaults(PreferencesPath);
        Assert.Equal(TimelineThumbnailsDisplay.None, defaults.GetTimelineThumbnailsDisplay());
        defaults.SetValue("timelineShowsLargeThumbnails", legacyLarge);
        Assert.Equal(expected, new AppDefaults(PreferencesPath).GetTimelineThumbnailsDisplay());
        Assert.Null(defaults.GetValue<int?>(AppDefaultsSharedExtensions.TimelineThumbnailsDisplayKey));
        int notifications = 0;
        defaults.Changed += (_, key) =>
        {
            Assert.Equal(AppDefaultsSharedExtensions.TimelineThumbnailsDisplayKey, key);
            Assert.Equal(TimelineThumbnailsDisplay.None, new AppDefaults(PreferencesPath).GetTimelineThumbnailsDisplay());
            notifications++;
        };
        defaults.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.None);
        Assert.Equal(1, notifications);
        Assert.Equal(TimelineThumbnailsDisplay.None, defaults.GetTimelineThumbnailsDisplay());
        Assert.Equal(legacyLarge, defaults.GetValue<bool>("timelineShowsLargeThumbnails"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void AnUnknownDisplayUsesNoneRatherThanTheLegacyLargePreference(int raw)
    {
        var defaults = new AppDefaults(PreferencesPath);
        defaults.SetValue("timelineShowsLargeThumbnails", true);
        defaults.SetValue(AppDefaultsSharedExtensions.TimelineThumbnailsDisplayKey, raw);
        Assert.Equal(TimelineThumbnailsDisplay.None, defaults.GetTimelineThumbnailsDisplay());
        Assert.Equal(raw, defaults.GetValue<int>(AppDefaultsSharedExtensions.TimelineThumbnailsDisplayKey));
    }

    [Fact]
    public void InvalidChoicesDoNotOverwriteTheLastValidDisplayOrNotify()
    {
        var defaults = new AppDefaults(PreferencesPath);
        defaults.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.None);
        int notifications = 0;
        defaults.Changed += (_, _) => notifications++;
        Assert.Throws<ArgumentOutOfRangeException>(() => defaults.SetTimelineThumbnailsDisplay((TimelineThumbnailsDisplay)99));
        Assert.Equal(TimelineThumbnailsDisplay.None, new AppDefaults(PreferencesPath).GetTimelineThumbnailsDisplay());
        Assert.Equal(0, notifications);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
