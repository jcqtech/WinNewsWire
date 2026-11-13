using WinNewsWire.AppShared.StatusBar;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public class StatusBarTests
{
    [Theory]
    [InlineData(StatusBarCountMode.Unread, true, "7 unread")]
    [InlineData(StatusBarCountMode.FoldersAndFeeds, true, "1 folder, 2 feeds")]
    [InlineData(StatusBarCountMode.Unread, false, "1 folder, 2 feeds")]
    public void HidingUnreadCountsUsesSubscriptionCounts(StatusBarCountMode mode, bool showUnread, string expected)
        => Assert.Equal(expected, StatusBarPresentation.CountText(mode, showUnread, 7, 1, 2));

    [Fact]
    public void CountModesCycleWithoutRevealingHiddenUnreadCounts()
    {
        Assert.Equal(StatusBarCountMode.FoldersAndFeeds, StatusBarPresentation.NextMode(StatusBarCountMode.Unread, true));
        Assert.Equal(StatusBarCountMode.Unread, StatusBarPresentation.NextMode(StatusBarCountMode.FoldersAndFeeds, true));
        Assert.Equal(StatusBarCountMode.FoldersAndFeeds, StatusBarPresentation.NextMode(StatusBarCountMode.FoldersAndFeeds, false));
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
