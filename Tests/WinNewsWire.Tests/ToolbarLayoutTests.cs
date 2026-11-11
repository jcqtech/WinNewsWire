using WinNewsWire.AppShared.Toolbar;
using Xunit;

namespace WinNewsWire.AppShared.Tests;

public sealed class ToolbarLayoutTests
{
    private static readonly IReadOnlyDictionary<string, double> Widths = ToolbarConfiguration.AllowedItems
        .ToDictionary(id => id, id => id == "search" ? 140d : 30d);

    [Fact]
    public void FlexibleSpaceExpandsRatherThanActingAsACommand()
    {
        var configuration = ToolbarConfiguration.Default.WithItems(["markRead", "flexibleSpace", "markStar", "flexibleSpace", "search"]);
        var result = ToolbarLayout.Calculate(configuration, Widths, 400);
        Assert.Empty(result.OverflowItems);
        Assert.All(result.Slots.Where(slot => slot.Identifier == "flexibleSpace"), slot => Assert.Equal(100, slot.Width));
        Assert.Equal(400, result.Slots.Sum(slot => slot.Width));
    }

    [Fact]
    public void TrackingSeparatorsAlignWithPaneEdgesWhenThereIsRoom()
    {
        var result = ToolbarLayout.Calculate(ToolbarConfiguration.Default, Widths, 1000, sidebarBoundary: 200, timelineBoundary: 480);
        Assert.Empty(result.OverflowItems);
        double SeparatorCenter(string id)
        {
            int index = result.Slots.ToList().FindIndex(slot => slot.Identifier == id);
            return result.Slots.Take(index + 1).Sum(slot => slot.Width) - 6;
        }
        Assert.Equal(200, SeparatorCenter("sidebarTrackingSeparator"));
        Assert.Equal(480, SeparatorCenter("timelineTrackingSeparator"));
        Assert.Equal(1000, result.Slots.Sum(slot => slot.Width));
    }

    [Theory]
    [InlineData(false, true, false, true, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, false, false, false, true)]
    [InlineData(false, true, true, false, false)]
    public void PaneSpecificSeparatorsAreHiddenWithoutChangingTheConfiguration(bool column, bool sidebar, bool readingOnly,
        bool expectedSidebar, bool expectedTimeline)
    {
        var configuration = ToolbarConfiguration.Default.WithItems(
            ["toggleSidebar", "sidebarTrackingSeparator", "markAllAsRead", "timelineTrackingSeparator", "markRead"]);
        var before = configuration.Items.ToArray();
        var result = ToolbarLayout.Calculate(configuration, Widths, 1000, column, sidebar, readingOnly);
        Assert.Equal(expectedSidebar, result.Slots.Any(slot => slot.Identifier == "sidebarTrackingSeparator"));
        Assert.Equal(expectedTimeline, result.Slots.Any(slot => slot.Identifier == "timelineTrackingSeparator"));
        Assert.Equal(before, configuration.Items);
        Assert.Empty(result.OverflowItems);
    }

    [Fact]
    public void OverflowContainsRealCommandsInSavedOrderNotPlaceholderSeparators()
    {
        var configuration = ToolbarConfiguration.Default.WithItems(
            ["refresh", "sidebarTrackingSeparator", "markRead", "timelineTrackingSeparator", "markStar", "flexibleSpace", "search"]);
        var result = ToolbarLayout.Calculate(configuration, Widths, 60);
        Assert.Equal(new[] { "markRead", "markStar", "search" }, result.OverflowItems);
        Assert.DoesNotContain(result.Slots, slot => ToolbarConfiguration.IsSeparator(slot.Identifier));
        Assert.Contains(result.Slots, slot => slot.Identifier == "refresh");
        Assert.All(result.OverflowItems, id => Assert.False(ToolbarConfiguration.IsStructural(id)));
    }

    [Fact]
    public void FocusedSearchRemainsReachableInsteadOfBeingPutBackInOverflow()
    {
        var configuration = ToolbarConfiguration.Default.WithItems(["refresh", "markRead", "search"]);
        var result = ToolbarLayout.Calculate(configuration, Widths, 100, priorityItem: "search");
        var search = Assert.Single(result.Slots);
        Assert.Equal("search", search.Identifier);
        Assert.Equal(100, search.Width);
        Assert.Equal(new[] { "refresh", "markRead" }, result.OverflowItems);
        Assert.Equal(new[] { "refresh", "markRead", "search" }, configuration.Items);
    }

    [Fact]
    public void ReorderingChangesOverflowAndDoesNotCreateLeadingOrRepeatedDividers()
    {
        var configuration = ToolbarConfiguration.Default.WithItems(
            ["sidebarTrackingSeparator", "markStar", "timelineTrackingSeparator", "search", "refresh"]);
        var result = ToolbarLayout.Calculate(configuration, Widths, 70);
        Assert.DoesNotContain(result.Slots, slot => ToolbarConfiguration.IsSeparator(slot.Identifier));
        Assert.Equal("markStar", Assert.Single(result.Slots).Identifier);
        Assert.Equal(new[] { "search", "refresh" }, result.OverflowItems);
    }

    [Fact]
    public void MissingOrInvalidWidthsAndOffscreenTrackingTargetsRemainBounded()
    {
        var invalid = new Dictionary<string, double> { ["search"] = double.NaN, ["refresh"] = -1, ["markRead"] = double.PositiveInfinity };
        foreach (double width in new[] { 0, 1, 30, 75, 200, 500, 900 })
        {
            var result = ToolbarLayout.Calculate(ToolbarConfiguration.Default, invalid, width,
                sidebarBoundary: double.NaN, timelineBoundary: 5000);
            Assert.All(result.Slots, slot => Assert.True(double.IsFinite(slot.Width) && slot.Width >= 0));
            Assert.True(result.Slots.Sum(slot => slot.Width) <= width + 0.001);
            var visibleActions = result.Slots.Where(slot => !ToolbarConfiguration.IsStructural(slot.Identifier)).Select(slot => slot.Identifier);
            Assert.Equal(ToolbarConfiguration.DefaultItems.Count(id => !ToolbarConfiguration.IsStructural(id)),
                visibleActions.Concat(result.OverflowItems).Distinct().Count());
        }
    }

    [Fact]
    public void AnIntentionallyEmptyToolbarHasNoFakeOverflowCommands()
    {
        var result = ToolbarLayout.Calculate(ToolbarConfiguration.Default.WithItems([]), Widths, 500);
        Assert.Empty(result.Slots);
        Assert.Empty(result.OverflowItems);
    }

    [Fact]
    public void PaneHeadersKeepEveryCommandButLeaveTitleBarItemsAndLegacySpacersAlone()
    {
        var configuration = ToolbarConfiguration.Default.WithItems(ToolbarConfiguration.AllowedItems);
        Assert.Equal(["refresh", "newSidebarItemMenu"], ToolbarLayout.HeaderItems(configuration, ToolbarSection.Sidebar));
        Assert.Equal(["markAllAsRead", "toggleReadArticlesFilter", "cleanUp"], ToolbarLayout.HeaderItems(configuration, ToolbarSection.Timeline));
        var groups = new[] { ToolbarSection.Sidebar, ToolbarSection.Timeline, ToolbarSection.Article }
            .SelectMany(section => ToolbarLayout.HeaderItems(configuration, section)).ToArray();
        Assert.Equal(configuration.Items.Where(ToolbarConfiguration.IsHeaderItem).Order(), groups.Order());
        Assert.Equal(groups.Length, groups.Distinct().Count());
        Assert.Equal(configuration.Items.Where(ToolbarConfiguration.IsHeaderItem),
            ToolbarLayout.HeaderItems(configuration, ToolbarSection.Combined));
    }

    [Theory]
    [InlineData(ToolbarSection.Sidebar)]
    [InlineData(ToolbarSection.Timeline)]
    [InlineData(ToolbarSection.Article)]
    [InlineData(ToolbarSection.Combined)]
    [InlineData(ToolbarSection.Reading)]
    public void HeaderOverflowRetainsCommandsWithoutMovingSearchIntoIt(ToolbarSection section)
    {
        foreach (double width in new[] { 0, 1, 36, 100, 800 })
        {
            var result = ToolbarLayout.CalculateHeader(ToolbarConfiguration.Default, section, Widths, width);
            Assert.True(result.Slots.Sum(slot => slot.Width) <= width);
            Assert.Equal(ToolbarLayout.HeaderItems(ToolbarConfiguration.Default, section),
                result.Slots.Select(slot => slot.Identifier).Concat(result.OverflowItems));
            Assert.DoesNotContain("search", result.OverflowItems);
            Assert.DoesNotContain("toggleSidebar", result.OverflowItems);
        }
    }

    [Fact]
    public void HiddenHeaderCommandsDoNotChangeSavedTitleBarChoices()
    {
        var hidden = ToolbarConfiguration.Default.WithVisibility(false);
        var result = ToolbarLayout.CalculateHeader(hidden, ToolbarSection.Combined, Widths, 800);
        Assert.Empty(result.Slots);
        Assert.Empty(result.OverflowItems);
        Assert.Equal(ToolbarConfiguration.DefaultItems, hidden.Items);
    }

    [Fact]
    public void HeaderCustomizationPreservesLegacyAndFixedItemPositions()
    {
        var original = ToolbarConfiguration.Default;
        var changed = original.WithHeaderItems(["articleThemeMenu", "markStar"]);
        Assert.Equal(["articleThemeMenu", "markStar"], ToolbarLayout.HeaderItems(changed, ToolbarSection.Combined));
        Assert.Equal(original.Items.Where(id => !ToolbarConfiguration.IsHeaderItem(id)),
            changed.Items.Where(id => !ToolbarConfiguration.IsHeaderItem(id)));
        Assert.Empty(ToolbarLayout.HeaderItems(changed.WithHeaderItems([]), ToolbarSection.Combined));
        Assert.Throws<ArgumentException>(() => original.WithHeaderItems(["search"]));
        Assert.Throws<ArgumentException>(() => original.WithHeaderItems(["unknown"]));
    }

    [Fact]
    public void CombinedReadingHeaderExcludesSidebarCommandsWithoutRemovingThemFromCustomization()
    {
        var configuration = ToolbarConfiguration.Default;
        var reading = ToolbarLayout.HeaderItems(configuration, ToolbarSection.Reading);
        var sidebar = ToolbarLayout.HeaderItems(configuration, ToolbarSection.Sidebar);
        Assert.DoesNotContain("refresh", reading);
        Assert.DoesNotContain("newSidebarItemMenu", reading);
        Assert.Contains("markAllAsRead", reading);
        Assert.Contains("toggleReadArticlesFilter", reading);
        Assert.Contains("openInWebView", reading);
        Assert.Equal(ToolbarLayout.HeaderItems(configuration, ToolbarSection.Combined).Order(),
            sidebar.Concat(reading).Order());
        var hiddenSidebar = ToolbarLayout.CalculateHeader(configuration, ToolbarSection.Sidebar, Widths, 0);
        Assert.Empty(hiddenSidebar.Slots);
        Assert.Equal(sidebar, hiddenSidebar.OverflowItems);
    }
}
