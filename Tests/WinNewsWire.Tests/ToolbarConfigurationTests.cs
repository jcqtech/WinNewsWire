using System.Text.Json;
using WinNewsWire.AppShared.Toolbar;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ToolbarConfigurationTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(".test-artifacts", "toolbar-" + Guid.NewGuid().ToString("N")));
    private string PreferencesPath => Path.Combine(_directory, "defaults.json");

    [Fact]
    public void DefaultsUseTheNativeSharedIdentifierAllowedItemsAndOrder()
    {
        Assert.Equal("MainWindowToolbar", ToolbarConfiguration.PreferenceKey);
        Assert.Equal(new[]
        {
            "toggleSidebar", "refresh", "newSidebarItemMenu", "sidebarTrackingSeparator", "markAllAsRead",
            "toggleReadArticlesFilter", "timelineTrackingSeparator", "flexibleSpace", "nextUnread", "markRead",
            "markStar", "readerView", "openInWebView", "openInBrowser", "share", "articleThemeMenu", "search", "cleanUp"
        }, ToolbarConfiguration.AllowedItems);
        Assert.Equal(new[]
        {
            "toggleSidebar", "flexibleSpace", "refresh", "newSidebarItemMenu", "sidebarTrackingSeparator",
            "markAllAsRead", "toggleReadArticlesFilter", "timelineTrackingSeparator", "markRead", "markStar",
            "nextUnread", "readerView", "share", "openInWebView", "openInBrowser", "flexibleSpace", "search"
        }, ToolbarConfiguration.Default.Items);
        Assert.True(ToolbarConfiguration.Default.IsVisible);
        Assert.Equal(ToolbarDisplayMode.IconsOnly, ToolbarConfiguration.Default.DisplayMode);
        Assert.All(ToolbarConfiguration.AllowedItems, id => Assert.False(string.IsNullOrWhiteSpace(ToolbarConfiguration.Label(id))));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"version":3,"items":["markRead"],"visible":false}""")]
    [InlineData("""{"version":"1","items":[]}""")]
    [InlineData("""{"version":1.5,"items":[]}""")]
    public void UnsupportedOrMalformedEnvelopesUseSafeDefaults(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        var configuration = ToolbarConfiguration.FromJson(parsed.RootElement);
        Assert.Equal(ToolbarConfiguration.DefaultItems, configuration.Items);
        Assert.True(configuration.IsVisible);
        Assert.Equal(ToolbarDisplayMode.IconsOnly, configuration.DisplayMode);
    }

    [Fact]
    public void UnknownItemsAndDuplicateActionsAreIgnoredButFlexibleSpacesCanRepeat()
    {
        using var parsed = JsonDocument.Parse("""
            {"version":1,"visible":false,"displayMode":"future","items":
                ["future", "markStar", 4, "flexibleSpace", "markStar", "search", "flexibleSpace", null, "SEARCH"]}
            """);
        var configuration = ToolbarConfiguration.FromJson(parsed.RootElement);
        Assert.Equal(new[] { "markStar", "flexibleSpace", "search", "flexibleSpace" }, configuration.Items);
        Assert.False(configuration.IsVisible);
        Assert.Equal(ToolbarDisplayMode.IconsOnly, configuration.DisplayMode);
    }

    [Theory]
    [InlineData("""{"version":1,"items":null}""")]
    [InlineData("""{"version":1,"items":{}}""")]
    [InlineData("""{"version":1,"items":["future"]}""")]
    [InlineData("""{"version":1,"items":[4,null]}""")]
    public void InvalidOrdersFallBackRatherThanCreatingAnUnusableToolbar(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(ToolbarConfiguration.DefaultItems, ToolbarConfiguration.FromJson(parsed.RootElement).Items);
    }

    [Fact]
    public void EmptyOrdersAreIntentionalAndSpacersAreBounded()
    {
        using var parsed = JsonDocument.Parse("""{"version":1,"items":[]}""");
        Assert.Empty(ToolbarConfiguration.FromJson(parsed.RootElement).Items);
        var spaces = ToolbarConfiguration.Default.WithItems(Enumerable.Repeat("flexibleSpace", 1000));
        Assert.Equal(ToolbarConfiguration.MaximumFlexibleSpaces, spaces.Items.Count);
    }

    [Theory]
    [InlineData("""["share","openInBrowser","search"]""", """["share","openInWebView","openInBrowser","search"]""")]
    [InlineData("""["share","search"]""", """["share","openInWebView","search"]""")]
    [InlineData("""["openInBrowser"]""", """["openInWebView","openInBrowser"]""")]
    [InlineData("""["share","openInWebView","openInBrowser"]""", """["share","openInWebView","openInBrowser"]""")]
    [InlineData("""["markStar","search"]""", """["markStar","search"]""")]
    [InlineData("[]", "[]")]
    public void OriginalConfigurationsGainWebViewBesideTheirExistingBrowserCommands(string items, string expected)
    {
        using var saved = JsonDocument.Parse("""{"version":1,"visible":false,"displayMode":"text","items":""" + items + "}");
        var configuration = ToolbarConfiguration.FromJson(saved.RootElement);
        Assert.Equal(JsonSerializer.Deserialize<string[]>(expected), configuration.Items);
        Assert.False(configuration.IsVisible);
        Assert.Equal(ToolbarDisplayMode.TextOnly, configuration.DisplayMode);
    }

    [Fact]
    public void RemovingWebViewAfterMigrationRemainsRemovedWhenReopened()
    {
        var defaults = new AppDefaults(PreferencesPath);
        defaults.SetValue(ToolbarConfiguration.PreferenceKey, new { version = 1, items = new[] { "share", "openInBrowser" } });
        var migrated = ToolbarConfiguration.Load(defaults);
        Assert.Equal(["share", "openInWebView", "openInBrowser"], migrated.Items);
        migrated.WithItems(["share", "openInBrowser"]).Save(defaults);
        Assert.Equal(2, defaults.GetValue<JsonElement>(ToolbarConfiguration.PreferenceKey).GetProperty("version").GetInt32());
        Assert.Equal(["share", "openInBrowser"], ToolbarConfiguration.Load(new AppDefaults(PreferencesPath)).Items);
    }

    [Fact]
    public void DraftChangesDoNotMutateTheCurrentConfigurationOrCallerOwnedItems()
    {
        string[] order = ["markStar", "search", "markRead"];
        var current = ToolbarConfiguration.Default.WithItems(order);
        order[0] = "refresh";
        var draft = current.WithItems(current.Items.Reverse()).WithVisibility(false).WithDisplayMode(ToolbarDisplayMode.TextOnly);
        Assert.Equal(new[] { "markRead", "search", "markStar" }, draft.Items);
        Assert.Equal(new[] { "markStar", "search", "markRead" }, current.Items);
        Assert.True(current.IsVisible);
        Assert.Equal(ToolbarDisplayMode.IconsOnly, current.DisplayMode);
        Assert.Equal(ToolbarConfiguration.DefaultItems, ToolbarConfiguration.Default.Items);
        Assert.Throws<ArgumentOutOfRangeException>(() => current.WithDisplayMode((ToolbarDisplayMode)100));
    }

    [Fact]
    public void SearchRestoresVisibilityAndMissingControlWithoutResettingOtherChoices()
    {
        var current = ToolbarConfiguration.Default.WithItems(["markStar", "flexibleSpace", "markRead"])
            .WithVisibility(false).WithDisplayMode(ToolbarDisplayMode.TextOnly);
        var searchable = current.WithSearchVisible();
        Assert.Equal(new[] { "markStar", "flexibleSpace", "markRead", "search" }, searchable.Items);
        Assert.True(searchable.IsVisible);
        Assert.Equal(ToolbarDisplayMode.TextOnly, searchable.DisplayMode);
        Assert.Equal(searchable.Items, searchable.WithSearchVisible().Items);
        Assert.DoesNotContain("search", current.Items);
        Assert.False(current.IsVisible);
    }

    [Theory]
    [InlineData(ToolbarDisplayMode.IconsOnly)]
    [InlineData(ToolbarDisplayMode.IconsAndText)]
    [InlineData(ToolbarDisplayMode.TextOnly)]
    public void ConfigurationIsAtomicPersistentAndNotifiesAfterCommit(ToolbarDisplayMode mode)
    {
        var defaults = new AppDefaults(PreferencesPath);
        defaults.SetValue("existingProfilePreference", new { value = 7 });
        var saved = ToolbarConfiguration.Default.WithItems(["search", "flexibleSpace", "markStar"])
            .WithVisibility(false).WithDisplayMode(mode);
        int changes = 0;
        defaults.Changed += (_, key) =>
        {
            Assert.Equal(ToolbarConfiguration.PreferenceKey, key);
            var reloaded = ToolbarConfiguration.Load(new AppDefaults(PreferencesPath));
            Assert.Equal(saved.Items, reloaded.Items);
            Assert.Equal(mode, reloaded.DisplayMode);
            Assert.False(reloaded.IsVisible);
            changes++;
        };
        saved.Save(defaults);
        Assert.Equal(1, changes);
        Assert.Equal(7, new AppDefaults(PreferencesPath).GetValue<JsonElement>("existingProfilePreference").GetProperty("value").GetInt32());
    }

    [Fact]
    public void LoadingAFutureConfigurationDoesNotRewriteIt()
    {
        var defaults = new AppDefaults(PreferencesPath);
        defaults.SetValue(ToolbarConfiguration.PreferenceKey, new { version = 10, future = "preserve me" });
        string original = File.ReadAllText(PreferencesPath);
        Assert.Equal(ToolbarConfiguration.DefaultItems, ToolbarConfiguration.Load(defaults).Items);
        Assert.Equal(original, File.ReadAllText(PreferencesPath));
    }

    [Fact]
    public void FailedWritesRetainPreviousMemoryDiskAndNotificationState()
    {
        var defaults = new AppDefaults(PreferencesPath);
        var original = ToolbarConfiguration.Default.WithItems(["markStar", "search"]).WithDisplayMode(ToolbarDisplayMode.TextOnly);
        original.Save(defaults);
        int changes = 0;
        defaults.Changed += (_, _) => changes++;
        using (var locked = new FileStream(PreferencesPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => original.WithVisibility(false).WithItems(["cleanUp"]).Save(defaults));
            Assert.True(error is IOException or UnauthorizedAccessException);
            var current = ToolbarConfiguration.Load(defaults);
            Assert.Equal(original.Items, current.Items);
            Assert.Equal(original.DisplayMode, current.DisplayMode);
            Assert.True(current.IsVisible);
        }
        var reopened = ToolbarConfiguration.Load(new AppDefaults(PreferencesPath));
        Assert.Equal(original.Items, reopened.Items);
        Assert.True(reopened.IsVisible);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void FailedFirstWriteDoesNotInsertAPreference()
    {
        Directory.CreateDirectory(PreferencesPath);
        var defaults = new AppDefaults(PreferencesPath);
        var error = Record.Exception(() => ToolbarConfiguration.Default.WithVisibility(false).Save(defaults));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.False(defaults.ContainsKey(ToolbarConfiguration.PreferenceKey));
        Assert.True(ToolbarConfiguration.Load(defaults).IsVisible);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
