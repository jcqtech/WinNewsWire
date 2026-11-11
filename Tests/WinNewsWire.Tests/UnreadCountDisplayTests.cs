using System.Globalization;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class UnreadCountDisplayTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "unread-display-" + Guid.NewGuid().ToString("N"));
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private string PreferencesPath => Path.Combine(_directory, "defaults.json");

    public UnreadCountDisplayTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }

    [Theory]
    [InlineData(UnreadCountDisplay.Count, -1, null, false)]
    [InlineData(UnreadCountDisplay.Count, 0, null, false)]
    [InlineData(UnreadCountDisplay.Count, 1, "1", false)]
    [InlineData(UnreadCountDisplay.Count, 1234, "1,234", false)]
    [InlineData(UnreadCountDisplay.Dot, -1, null, false)]
    [InlineData(UnreadCountDisplay.Dot, 0, null, false)]
    [InlineData(UnreadCountDisplay.Dot, 1, "\u2022", true)]
    [InlineData(UnreadCountDisplay.Dot, 1234, "\u2022", true)]
    [InlineData(UnreadCountDisplay.Hidden, -1, null, false)]
    [InlineData(UnreadCountDisplay.Hidden, 0, null, false)]
    [InlineData(UnreadCountDisplay.Hidden, 1, null, false)]
    [InlineData(UnreadCountDisplay.Hidden, 1234, null, false)]
    public void DisplayModesKeepNativePositiveCountBoundaries(UnreadCountDisplay mode, int count, string? text, bool dot)
    {
        Assert.Equal(text, mode.TextFor(count));
        Assert.Equal(dot, mode.ShowsDot(count));
    }

    [Fact]
    public void CountsUseTheCurrentNumberFormattingCulture()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("1.234", UnreadCountDisplay.Count.TextFor(1234));
    }

    [Theory]
    [InlineData(UnreadCountDisplay.Count, 0, null)]
    [InlineData(UnreadCountDisplay.Count, 1, "1 unread")]
    [InlineData(UnreadCountDisplay.Count, 1234, "1234 unread")]
    [InlineData(UnreadCountDisplay.Dot, 0, null)]
    [InlineData(UnreadCountDisplay.Dot, 1, "unread")]
    [InlineData(UnreadCountDisplay.Dot, 1234, "unread")]
    [InlineData(UnreadCountDisplay.Hidden, 0, null)]
    [InlineData(UnreadCountDisplay.Hidden, 1, null)]
    [InlineData(UnreadCountDisplay.Hidden, 1234, null)]
    public void AccessibilityOmitsHiddenAndZeroCounts(UnreadCountDisplay mode, int count, string? expected)
        => Assert.Equal(expected, mode.AccessibilityTextFor(count));

    [Theory]
    [InlineData(UnreadCountDisplay.Count)]
    [InlineData(UnreadCountDisplay.Dot)]
    [InlineData(UnreadCountDisplay.Hidden)]
    public void NativeIntegerPreferencePersistsBeforeNotification(UnreadCountDisplay mode)
    {
        var defaults = new AppDefaults(PreferencesPath);
        Assert.Equal(UnreadCountDisplay.Count, defaults.GetUnreadCountDisplay());
        int changes = 0;
        defaults.Changed += (_, key) =>
        {
            Assert.Equal(AppDefaultsSharedExtensions.UnreadCountDisplayKey, key);
            var reopened = new AppDefaults(PreferencesPath);
            Assert.Equal((int)mode, reopened.GetValue<int>(key));
            Assert.Equal(mode, reopened.GetUnreadCountDisplay());
            changes++;
        };
        defaults.SetUnreadCountDisplay(mode);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AnUnknownStoredModeUsesTheNativeCountFallbackWithoutRewritingIt()
    {
        var defaults = new AppDefaults(PreferencesPath);
        defaults.SetValue(AppDefaultsSharedExtensions.UnreadCountDisplayKey, 99);
        Assert.Equal(UnreadCountDisplay.Count, defaults.GetUnreadCountDisplay());
        Assert.Equal(99, defaults.GetValue<int>(AppDefaultsSharedExtensions.UnreadCountDisplayKey));
    }

    [Fact]
    public void InvalidNewModesAreRejectedBeforeWriting()
    {
        var defaults = new AppDefaults(PreferencesPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => defaults.SetUnreadCountDisplay((UnreadCountDisplay)99));
        Assert.False(File.Exists(PreferencesPath));
    }

    [Fact]
    public void FailedWritesPreserveThePreviousModeAndDoNotNotify()
    {
        var defaults = new AppDefaults(PreferencesPath);
        defaults.SetUnreadCountDisplay(UnreadCountDisplay.Dot);
        int changes = 0;
        defaults.Changed += (_, _) => changes++;
        using (var locked = new FileStream(PreferencesPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => defaults.SetUnreadCountDisplay(UnreadCountDisplay.Hidden));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(UnreadCountDisplay.Dot, defaults.GetUnreadCountDisplay());
        }
        Assert.Equal(UnreadCountDisplay.Dot, new AppDefaults(PreferencesPath).GetUnreadCountDisplay());
        Assert.Equal(0, changes);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
