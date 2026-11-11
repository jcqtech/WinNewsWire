using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class AppDefaultsAutomationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "automation-preferences-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "defaults.json");

    [Fact]
    public void AutomationIsDisabledUntilExplicitlyEnabledAndTheChoicePersists()
    {
        var defaults = new AppDefaults(FilePath);
        Assert.False(defaults.AutomationEnabled);
        Assert.False(File.Exists(FilePath));
        defaults.AutomationEnabled = true;
        Assert.True(new AppDefaults(FilePath).AutomationEnabled);
        defaults.AutomationEnabled = false;
        Assert.False(new AppDefaults(FilePath).AutomationEnabled);
    }

    [Fact]
    public void FailedOptInDoesNotChangeTheEffectiveChoiceOrPublishAnEvent()
    {
        var defaults = new AppDefaults(FilePath);
        defaults.AutomationEnabled = false;
        int events = 0;
        defaults.Changed += (_, _) => events++;
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => defaults.AutomationEnabled = true);
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.False(defaults.AutomationEnabled);
        Assert.False(new AppDefaults(FilePath).AutomationEnabled);
        Assert.Equal(0, events);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
