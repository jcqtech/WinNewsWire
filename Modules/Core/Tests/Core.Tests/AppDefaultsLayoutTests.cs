using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.Tests;

public sealed class AppDefaultsLayoutTests
{
    [Fact]
    public void ColumnLayoutUsesTheNativeKeyAndPersistsAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "layout-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "defaults.json");
        try
        {
            var defaults = new AppDefaults(path);
            Assert.False(defaults.UseColumnLayout);
            var keys = new List<string>();
            defaults.Changed += (_, key) => keys.Add(key);
            defaults.UseColumnLayout = true;
            Assert.Equal(["useColumnLayout"], keys);
            Assert.True(new AppDefaults(path).UseColumnLayout);
            defaults.UseColumnLayout = false;
            Assert.False(new AppDefaults(path).UseColumnLayout);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }
}
