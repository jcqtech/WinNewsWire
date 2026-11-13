using System.Globalization;
using WinNewsWire.AppShared.StatusBar;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public class StatusBarTests
{
    [Theory]
    [InlineData(StatusBarCountMode.Unread, true, "7 unread")]
    [InlineData(StatusBarCountMode.FoldersAndFeeds, true, "1 folder, 2 feeds")]
    [InlineData(StatusBarCountMode.Unread, false, "Not updated yet")]
    [InlineData(StatusBarCountMode.LastRefresh, true, "Not updated yet")]
    public void HiddenCountsFallBackToLastRefresh(StatusBarCountMode mode, bool showUnread, string expected)
        => Assert.Equal(expected, StatusBarPresentation.CountText(mode, showUnread, 7, 1, 2));

    [Fact]
    public void CountModesCycleWithoutRevealingHiddenUnreadCounts()
    {
        Assert.Equal(StatusBarCountMode.FoldersAndFeeds, StatusBarPresentation.NextMode(StatusBarCountMode.Unread, true));
        Assert.Equal(StatusBarCountMode.LastRefresh, StatusBarPresentation.NextMode(StatusBarCountMode.FoldersAndFeeds, true));
        Assert.Equal(StatusBarCountMode.LastRefresh, StatusBarPresentation.NextMode(StatusBarCountMode.FoldersAndFeeds, false));
        Assert.Equal(StatusBarCountMode.Unread, StatusBarPresentation.NextMode(StatusBarCountMode.LastRefresh, true));
        Assert.Equal(StatusBarCountMode.FoldersAndFeeds, StatusBarPresentation.NextMode(StatusBarCountMode.LastRefresh, false));
        Assert.Equal(StatusBarCountMode.FoldersAndFeeds, StatusBarPresentation.NextMode(StatusBarCountMode.Unread, false));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void RefreshDateUsesLocalDayAndLocale(string locale)
    {
        var culture = CultureInfo.GetCultureInfo(locale);
        var completed = new DateTime(2026, 1, 2, 23, 59, 0, DateTimeKind.Local);
        Assert.Equal(completed.ToString("t", culture),
            StatusBarPresentation.RefreshDateText(completed.ToUniversalTime(), completed, culture));
        Assert.Equal(completed.ToString("g", culture),
            StatusBarPresentation.RefreshDateText(completed.ToUniversalTime(), completed.AddMinutes(2), culture));
    }

    [Theory]
    [InlineData("HTTPS://example.com/a?b=1", "example.com/a?b=1")]
    [InlineData("http://example.com", "example.com")]
    [InlineData("magnet:?xt=urn:btih:example", "magnet:?xt=urn:btih:example")]
    public void LinkPresentationOnlyStripsWebSchemes(string url, string expected)
        => Assert.Equal(expected, StatusBarPresentation.LinkText(url));

    [Fact]
    public void StatusBarSettingsPersistAndInvalidCountModesAreRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinNewsWire-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var defaults = new AppDefaults(path);
            Assert.Equal(StatusBarCountMode.LastRefresh, defaults.GetStatusBarCountMode());
            defaults.SetValue(AppDefaults.Key.StatusBarCountMode, 99);
            Assert.Equal(StatusBarCountMode.LastRefresh, defaults.GetStatusBarCountMode());
            defaults.ShowStatusBar = false;
            defaults.ShowStatusBarThemeMenu = false;
            defaults.SetStatusBarCountMode(StatusBarCountMode.FoldersAndFeeds);
            var loaded = new AppDefaults(path);
            Assert.False(loaded.ShowStatusBar);
            Assert.False(loaded.ShowStatusBarThemeMenu);
            Assert.Equal(StatusBarCountMode.FoldersAndFeeds, loaded.GetStatusBarCountMode());
            Assert.Throws<ArgumentOutOfRangeException>(() => loaded.SetStatusBarCountMode((StatusBarCountMode)99));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
