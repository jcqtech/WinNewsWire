using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Tests;

public sealed class AppDefaultsBrowserTests
{
    [Fact]
    public void BrowserSelectionAndBackgroundPreferencePersistWithoutChangingTheSystemDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "browser-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "defaults.json");
        try
        {
            var defaults = new AppDefaults(path);
            Assert.Null(defaults.DefaultBrowserID);
            Assert.False(defaults.OpenInBrowserInBackground);
            defaults.DefaultBrowserID = @"D:\Apps\Browser\browser.exe";
            defaults.OpenInBrowserInBackground = true;
            var reopened = new AppDefaults(path);
            Assert.Equal(@"D:\Apps\Browser\browser.exe", reopened.DefaultBrowserID);
            Assert.True(reopened.OpenInBrowserInBackground);
            reopened.DefaultBrowserID = null;
            Assert.Null(new AppDefaults(path).DefaultBrowserID);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
