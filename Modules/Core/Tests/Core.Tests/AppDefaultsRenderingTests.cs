using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class AppDefaultsRenderingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rendering-preferences-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "defaults.json");

    [Fact]
    public void CompatibilityRenderingIsOptInAndPersistsAcrossRestarts()
    {
        var defaults = new AppDefaults(FilePath);
        Assert.False(defaults.UseSoftwareRasterization);
        Assert.False(File.Exists(FilePath));
        string? changed = null;
        defaults.Changed += (_, key) => changed = key;
        defaults.UseSoftwareRasterization = true;
        Assert.Equal(AppDefaults.Key.UseSoftwareRasterization, changed);
        Assert.True(new AppDefaults(FilePath).UseSoftwareRasterization);
        defaults.UseSoftwareRasterization = false;
        Assert.False(new AppDefaults(FilePath).UseSoftwareRasterization);
    }

    [Fact]
    public void FailedRenderingPreferenceWritePreservesTheChoiceAndDoesNotNotify()
    {
        var defaults = new AppDefaults(FilePath);
        defaults.UseSoftwareRasterization = false;
        int events = 0;
        defaults.Changed += (_, _) => events++;
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => defaults.UseSoftwareRasterization = true);
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.False(defaults.UseSoftwareRasterization);
        Assert.False(new AppDefaults(FilePath).UseSoftwareRasterization);
        Assert.Equal(0, events);
    }

    [Fact]
    public void WheelInertiaIsOptInPersistentAndIndependentOfRasterization()
    {
        var defaults = new AppDefaults(FilePath);
        Assert.False(defaults.UseWheelInertia);
        defaults.UseSoftwareRasterization = true;
        string? changed = null;
        defaults.Changed += (_, key) => changed = key;
        defaults.UseWheelInertia = true;
        Assert.Equal(AppDefaults.Key.UseWheelInertia, changed);
        var reopened = new AppDefaults(FilePath);
        Assert.True(reopened.UseWheelInertia);
        Assert.True(reopened.UseSoftwareRasterization);
        reopened.UseWheelInertia = false;
        Assert.False(new AppDefaults(FilePath).UseWheelInertia);
        Assert.True(new AppDefaults(FilePath).UseSoftwareRasterization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingBrowserWorkaroundDoesNotEnableWheelInertia(bool legacyDirectScrolling)
    {
        var defaults = new AppDefaults(FilePath);
        defaults.SetValue("useDirectBrowserScrolling", legacyDirectScrolling);
        defaults.UseSoftwareRasterization = true;
        var reopened = new AppDefaults(FilePath);
        Assert.False(reopened.UseWheelInertia);
        Assert.True(reopened.UseSoftwareRasterization);
        Assert.Equal(legacyDirectScrolling, reopened.GetValue("useDirectBrowserScrolling", false));
    }

    [Fact]
    public void FailedScrollingPreferenceWritePreservesTheChoiceAndDoesNotNotify()
    {
        var defaults = new AppDefaults(FilePath);
        defaults.UseWheelInertia = false;
        int events = 0;
        defaults.Changed += (_, _) => events++;
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => defaults.UseWheelInertia = true);
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.False(defaults.UseWheelInertia);
        Assert.False(new AppDefaults(FilePath).UseWheelInertia);
        Assert.Equal(0, events);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
