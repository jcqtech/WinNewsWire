using WinNewsWire.Account;
using WinNewsWire.AppShared.Commands;
using WinNewsWire.AppShared.Sidebar;
using WinNewsWire.Core;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

[Collection("Account storage")]
public sealed class SidebarFeedOrderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WinNewsWire.SidebarOrder." + Guid.NewGuid().ToString("N"));
    private readonly List<Account.Account> _accounts = [];
    private readonly AppDefaults _defaults;

    public SidebarFeedOrderTests()
    {
        AppConfig.SetDataDirectoryOverride(_directory);
        _defaults = new AppDefaults(Path.Combine(_directory, "order.json"));
    }

    private Account.Account Account()
    {
        var account = new Account.Account(Guid.NewGuid().ToString("N"), AccountType.OnMyMac, "Test", new LocalAccountDelegate());
        _accounts.Add(account);
        return account;
    }

    private string[] Order(Account.Account account, Folder? folder = null, AppDefaults? defaults = null)
        => SidebarFeedOrder.Load(defaults ?? _defaults).OrderedFeeds(account, folder).Select(feed => feed.Url).ToArray();

    [Fact]
    public void DefaultOrderIsAlphabeticalAndNewFeedsFollowTheSavedOrder()
    {
        var account = Account();
        var bravo = account.AddFeed("https://example.com/b", "Bravo");
        var alpha = account.AddFeed("https://example.com/a", "Alpha");
        Assert.Equal([alpha.Url, bravo.Url], Order(account));
        SidebarFeedOrder.Save(_defaults, account, null, [bravo.Url, alpha.Url]);
        var added = account.AddFeed("https://example.com/aa", "Aardvark");
        Assert.Equal([bravo.Url, alpha.Url, added.Url], Order(account));
    }

    [Fact]
    public void RootFoldersAndAccountsHaveIndependentPersistentOrders()
    {
        var account = Account();
        var other = Account();
        var folder = account.AddFolder("Original name");
        var first = account.AddFeed("https://example.com/a", "Alpha");
        var second = account.AddFeed("https://example.com/b", "Bravo");
        account.AddFeed(first.Url, folder: folder);
        account.AddFeed(second.Url, folder: folder);
        other.AddFeed(first.Url, "Alpha");
        other.AddFeed(second.Url, "Bravo");
        SidebarFeedOrder.Save(_defaults, account, folder, [second.Url, first.Url]);
        folder.Name = "Renamed folder";
        var reopened = new AppDefaults(Path.Combine(_directory, "order.json"));
        Assert.Equal([first.Url, second.Url], Order(account, defaults: reopened));
        Assert.Equal([second.Url, first.Url], Order(account, folder, reopened));
        Assert.Equal([first.Url, second.Url], Order(other, defaults: reopened));
    }

    [Theory]
    [InlineData("b", "d", "acbd")]
    [InlineData("d", "a", "dabc")]
    [InlineData("bd", "a", "bdac")]
    [InlineData("ab", null, "cdab")]
    public void PlacementKeepsTheDraggedBlockInItsVisibleOrder(string moving, string? before, string expected)
    {
        var result = SidebarFeedOrder.PlaceBefore("abcd".Select(character => character.ToString()),
            moving.Select(character => character.ToString()), before);
        Assert.Equal(expected.Select(character => character.ToString()), result);
    }

    [Fact]
    public async Task ReorderingDoesNotChangeMembershipAndSupportsUndoRedo()
    {
        var account = Account();
        var first = account.AddFeed("https://example.com/a", "Alpha");
        var last = account.AddFeed("https://example.com/z", "Zulu");
        var undo = new UndoManager();
        await undo.ExecuteAsync(new PlaceFeedsCommand(account, [(last, null)], null, first.Url, _defaults));
        Assert.Equal([last.Url, first.Url], Order(account));
        Assert.Equal(2, account.TopLevelFeeds.Count);
        await undo.UndoAsync();
        Assert.Equal([first.Url, last.Url], Order(account));
        Assert.False(_defaults.ContainsKey(SidebarFeedOrder.PreferenceKey));
        await undo.RedoAsync();
        Assert.Equal([last.Url, first.Url], Order(account));
    }

    [Fact]
    public async Task MovingIntoAFolderAndItsInsertionPositionUndoTogether()
    {
        var account = Account();
        var folder = account.AddFolder("Folder");
        var incoming = account.AddFeed("https://example.com/z", "Zulu");
        var first = account.AddFeed("https://example.com/a", "Alpha", folder);
        var last = account.AddFeed("https://example.com/b", "Bravo", folder);
        var undo = new UndoManager();
        await undo.ExecuteAsync(new PlaceFeedsCommand(account, [(incoming, null)], folder, last.Url, _defaults));
        Assert.Empty(account.TopLevelFeeds);
        Assert.Equal([first.Url, incoming.Url, last.Url], Order(account, folder));
        await undo.UndoAsync();
        Assert.Contains(incoming, account.TopLevelFeeds);
        Assert.Equal([first.Url, last.Url], Order(account, folder));
        await undo.RedoAsync();
        Assert.Equal([first.Url, incoming.Url, last.Url], Order(account, folder));
    }

    [Fact]
    public async Task FailedPreferenceWriteRollsBackMembershipAndDoesNotRegisterUndo()
    {
        var account = Account();
        var folder = account.AddFolder("Folder");
        var feed = account.AddFeed("https://example.com/a", "Alpha");
        _defaults.SetValue("unrelated", "preserved");
        var undo = new UndoManager();
        using (var locked = new FileStream(Path.Combine(_directory, "order.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(() => undo.ExecuteAsync(new PlaceFeedsCommand(account, [(feed, null)], folder, null, _defaults)));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Empty(folder.Feeds);
        Assert.False(undo.CanUndo);
        Assert.False(_defaults.ContainsKey(SidebarFeedOrder.PreferenceKey));
        Assert.Equal("preserved", new AppDefaults(Path.Combine(_directory, "order.json")).GetValue<string>("unrelated"));
    }

    [Fact]
    public async Task FailedUndoRestoresTheMovedStateAndCanBeRetried()
    {
        var account = Account();
        var folder = account.AddFolder("Folder");
        var feed = account.AddFeed("https://example.com/a", "Alpha");
        var undo = new UndoManager();
        await undo.ExecuteAsync(new PlaceFeedsCommand(account, [(feed, null)], folder, null, _defaults));
        using (var locked = new FileStream(Path.Combine(_directory, "order.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = await Record.ExceptionAsync(undo.UndoAsync);
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Empty(account.TopLevelFeeds);
        Assert.Contains(feed, folder.Feeds);
        Assert.True(undo.CanUndo);
        await undo.UndoAsync();
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Empty(folder.Feeds);
    }

    [Fact]
    public async Task StaleInsertionTargetsAndDuplicateMembershipsAreRejectedBeforeMoving()
    {
        var account = Account();
        var folder = account.AddFolder("Folder");
        var feed = account.AddFeed("https://example.com/a", "Alpha");
        var undo = new UndoManager();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            undo.ExecuteAsync(new PlaceFeedsCommand(account, [(feed, null)], folder, "missing", _defaults)));
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Empty(folder.Feeds);
        account.AddFeed(feed.Url, folder: folder);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            undo.ExecuteAsync(new PlaceFeedsCommand(account, [(feed, null)], folder, null, _defaults)));
        Assert.Contains(feed, account.TopLevelFeeds);
        Assert.Contains(feed, folder.Feeds);
        Assert.False(undo.CanUndo);
    }

    [Fact]
    public void InvalidSavedContainerOrderDoesNotHideFeedsOrRewritePreferences()
    {
        var account = Account();
        var feed = account.AddFeed("https://example.com/a", "Alpha");
        SidebarFeedOrder.Save(_defaults, account, null, [feed.Url]);
        var invalid = _defaults.GetValue<Dictionary<string, string[]?>>(SidebarFeedOrder.PreferenceKey)!;
        invalid[invalid.Keys.Single()] = null;
        _defaults.SetValue(SidebarFeedOrder.PreferenceKey, invalid);
        string before = File.ReadAllText(Path.Combine(_directory, "order.json"));
        Assert.Equal([feed.Url], Order(account));
        Assert.Equal(before, File.ReadAllText(Path.Combine(_directory, "order.json")));
    }

    public void Dispose()
    {
        foreach (var account in _accounts) account.Dispose();
        AppConfig.SetDataDirectoryOverride(null);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
