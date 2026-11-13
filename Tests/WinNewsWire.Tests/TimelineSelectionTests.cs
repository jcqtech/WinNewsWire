using WinNewsWire.Account;
using WinNewsWire.AppShared.Timeline;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public class TimelineSelectionTests
{
    [Theory]
    [InlineData(0, 40, 52, 200, false)]
    [InlineData(40, 80, 52, 200, false)]
    [InlineData(52, 92, 52, 200, true)]
    [InlineData(160, 200, 52, 200, true)]
    [InlineData(170, 210, 52, 200, false)]
    public void FullRowVisibilityExcludesTheObscuringHeader(
        double top, double bottom, double visibleTop, double visibleBottom, bool visible)
        => Assert.Equal(visible, TimelineViewport.IsRowFullyVisible(top, bottom, visibleTop, visibleBottom));

    private sealed record Item(string Account, string ID, string Title);
    private static ArticleIdentityKey Identity(Item item) => new(item.Account, item.ID);

    [Fact]
    public void SelectionRestoresByAccountAndArticleIdentityRatherThanContent()
    {
        var old = new Item("one", "same", "Old");
        var otherAccount = new Item("two", "same", "Other");
        var updated = old with { Title = "Updated" };
        var restored = ArticleSelection.InTimelineOrder(new[] { otherAccount, updated }, new[] { Identity(old) }, Identity);
        Assert.Same(updated, Assert.Single(restored));
    }

    [Fact]
    public void SelectionUsesTimelineOrderAndDropsRemovedRows()
    {
        var first = new Item("a", "1", "One");
        var second = new Item("a", "2", "Two");
        var selection = ArticleSelection.InTimelineOrder(new[] { first, second },
            new[] { Identity(second), new ArticleIdentityKey("a", "deleted"), Identity(first) }, Identity);
        Assert.Equal(new[] { first, second }, selection);
    }

    [Fact]
    public void ContextMenuUsesSelectionOnlyWhenClickedRowIsSelected()
    {
        var first = new Item("a", "1", "One");
        var second = new Item("a", "2", "Two");
        var third = new Item("a", "3", "Three");
        Item[] timeline = [first, second, third];
        Assert.Equal(new[] { first, third }, ArticleSelection.ForContextMenu(timeline, new[] { third, first }, first, Identity));
        Assert.Equal(new[] { second }, ArticleSelection.ForContextMenu(timeline, new[] { first, third }, second, Identity));
    }

    [Fact]
    public void ReadFilterDefaultsMatchSidebarTypes()
    {
        var state = new ReadFilterState();
        Assert.False(state.IsEnabled(SidebarItemIdentifier.Feed("a", "feed"), ReadFilterType.None));
        Assert.True(state.IsEnabled(SidebarItemIdentifier.Folder("a", "Folder"), ReadFilterType.Read));
        Assert.True(state.IsEnabled(SidebarItemIdentifier.SmartFeed("Unread"), ReadFilterType.AlwaysRead));
    }

    [Fact]
    public void ReadFilterOverridesAreScopedAndPersistent()
    {
        var state = new ReadFilterState();
        var first = SidebarItemIdentifier.Feed("a", "same");
        var second = SidebarItemIdentifier.Feed("b", "same");
        state.SetEnabled(first, true);
        var restored = new ReadFilterState(state.Snapshot());
        Assert.True(restored.IsEnabled(first, ReadFilterType.None));
        Assert.False(restored.IsEnabled(second, ReadFilterType.None));
    }

    [Fact]
    public void AllUnreadCannotDisableItsReadFilter()
    {
        var state = new ReadFilterState();
        var id = SidebarItemIdentifier.SmartFeed("Unread");
        state.SetEnabled(id, false);
        Assert.True(state.IsEnabled(id, ReadFilterType.AlwaysRead));
    }

    [Fact]
    public void SidebarIdentifiersRoundTripAndReadLegacyFeedKey()
    {
        foreach (var id in new[]
        {
            SidebarItemIdentifier.Feed("a", "f"), SidebarItemIdentifier.Folder("a", "Folder"),
            SidebarItemIdentifier.SmartFeed("Today")
        }) Assert.Equal(id, SidebarItemIdentifier.FromUserInfo(id.UserInfo));
        Assert.Equal(SidebarItemIdentifier.Feed("a", "f"), SidebarItemIdentifier.FromUserInfo(
            new Dictionary<string, string> { ["type"] = "feed", ["accountID"] = "a", ["webFeedID"] = "f" }));
    }
}
