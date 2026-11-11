using System.Text.Json;
using Xunit;

namespace WinNewsWire.Core.Tests;

public sealed class AppDefaultsRefreshIntervalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "refresh-defaults-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "defaults.json");

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 4)]
    [InlineData(4, 5)]
    [InlineData(5, 6)]
    [InlineData(6, 7)]
    public void OldWindowsChoicesMigrateToTheirNativeValuesOnce(int oldValue, int nativeValue)
    {
        Write(new { refreshInterval = oldValue, unrelated = "retained" });
        var defaults = new AppDefaults(PreferencesPath);
        int changes = 0;
        defaults.Changed += (_, key) =>
        {
            var saved = new AppDefaults(PreferencesPath);
            Assert.Equal(nativeValue, saved.RefreshIntervalRaw);
            Assert.Equal(1, saved.GetValue<int>(AppDefaults.Key.RefreshIntervalVersion));
            changes++;
        };
        defaults.MigrateRefreshInterval();
        Assert.Equal(nativeValue, defaults.RefreshIntervalRaw);
        Assert.Equal("retained", defaults.GetValue<string>("unrelated"));
        Assert.Equal(2, changes);
        var after = File.ReadAllBytes(PreferencesPath);
        var reopened = new AppDefaults(PreferencesPath);
        reopened.MigrateRefreshInterval();
        Assert.Equal(nativeValue, reopened.RefreshIntervalRaw);
        Assert.Equal(after, File.ReadAllBytes(PreferencesPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void NativeValuesAreNotReinterpretedAsWindowsValues(int value)
    {
        Write(new { refreshIntervalVersion = 1, refreshInterval = value });
        var before = File.ReadAllBytes(PreferencesPath);
        var defaults = new AppDefaults(PreferencesPath);
        defaults.MigrateRefreshInterval();
        Assert.Equal(value, defaults.RefreshIntervalRaw);
        Assert.Equal(before, File.ReadAllBytes(PreferencesPath));
    }

    [Fact]
    public void FreshProfilesUseTwoHoursAndExistingImplicitWindowsProfilesRetainOneHour()
    {
        var fresh = new AppDefaults(PreferencesPath);
        Assert.Equal(5, fresh.RefreshIntervalRaw);
        fresh.MigrateRefreshInterval();
        Assert.Equal(5, fresh.RefreshIntervalRaw);
        File.Delete(PreferencesPath);
        Write(new { useColumnLayout = true });
        var existing = new AppDefaults(PreferencesPath);
        existing.MigrateRefreshInterval();
        Assert.Equal(4, existing.RefreshIntervalRaw);
        Assert.True(existing.UseColumnLayout);
    }

    [Fact]
    public void TheNativeDeprecatedTenMinuteValueMigratesToThirtyMinutes()
    {
        Write(new { refreshIntervalVersion = 1, refreshInterval = 2 });
        var defaults = new AppDefaults(PreferencesPath);
        Assert.Equal(3, defaults.RefreshIntervalRaw);
        defaults.MigrateRefreshInterval();
        Assert.Equal(3, new AppDefaults(PreferencesPath).GetValue<int>(AppDefaults.Key.RefreshInterval));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownValuesUseTheNativeTwoHourFallback(bool native)
    {
        Write(new { refreshIntervalVersion = native ? 1 : 0, refreshInterval = 99 });
        var defaults = new AppDefaults(PreferencesPath);
        defaults.MigrateRefreshInterval();
        Assert.Equal(5, defaults.RefreshIntervalRaw);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void NewSelectionsMustUseSupportedNativeValues(int value)
    {
        var defaults = new AppDefaults(PreferencesPath);
        Assert.Throws<ArgumentOutOfRangeException>(() => defaults.RefreshIntervalRaw = value);
        Assert.False(File.Exists(PreferencesPath));
    }

    [Fact]
    public void SavingASelectionPublishesBothValueAndEncodingBeforeNotifications()
    {
        var defaults = new AppDefaults(PreferencesPath);
        int changes = 0;
        defaults.Changed += (_, _) =>
        {
            var saved = new AppDefaults(PreferencesPath);
            Assert.Equal(7, saved.RefreshIntervalRaw);
            Assert.Equal(1, saved.GetValue<int>(AppDefaults.Key.RefreshIntervalVersion));
            changes++;
        };
        defaults.RefreshIntervalRaw = 7;
        Assert.Equal(2, changes);
    }

    [Fact]
    public void AFailedMigrationDoesNotAdvanceTheMarkerOrReplaceOtherPreferences()
    {
        Write(new { refreshInterval = 0, unrelated = "retained" });
        var defaults = new AppDefaults(PreferencesPath);
        var before = File.ReadAllBytes(PreferencesPath);
        int changes = 0;
        defaults.Changed += (_, _) => changes++;
        using (var locked = new FileStream(PreferencesPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(defaults.MigrateRefreshInterval);
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(before, File.ReadAllBytes(PreferencesPath));
        Assert.False(defaults.ContainsKey(AppDefaults.Key.RefreshIntervalVersion));
        Assert.Equal("retained", defaults.GetValue<string>("unrelated"));
        Assert.Equal(0, changes);
        defaults.MigrateRefreshInterval();
        Assert.Equal(1, defaults.RefreshIntervalRaw);
    }

    [Fact]
    public void AnUnreadableFileIsNotReplacedUsingAnEmptyCachedSnapshot()
    {
        Write(new { refreshInterval = 6, unrelated = "retained" });
        var before = File.ReadAllBytes(PreferencesPath);
        using (var locked = new FileStream(PreferencesPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var defaults = new AppDefaults(PreferencesPath);
            var error = Record.Exception(defaults.MigrateRefreshInterval);
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(before, File.ReadAllBytes(PreferencesPath));
    }

    [Fact]
    public void UnknownEncodingVersionsAreNotRewritten()
    {
        Write(new { refreshIntervalVersion = 2, refreshInterval = 3 });
        var before = File.ReadAllBytes(PreferencesPath);
        Assert.Throws<InvalidDataException>(() => new AppDefaults(PreferencesPath).MigrateRefreshInterval());
        Assert.Equal(before, File.ReadAllBytes(PreferencesPath));
    }

    private void Write<T>(T value)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(value));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
