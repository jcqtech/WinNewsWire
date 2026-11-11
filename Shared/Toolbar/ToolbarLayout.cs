namespace WinNewsWire.AppShared.Toolbar;

public sealed record ToolbarSlot(int ConfigurationIndex, string Identifier, double Width);
public sealed record ToolbarLayoutResult(IReadOnlyList<ToolbarSlot> Slots, IReadOnlyList<string> OverflowItems);
public enum ToolbarSection { Sidebar, Timeline, Article, Combined, Reading }

/// <summary>Adaptive toolbar geometry without platform controls or changes to the saved item order.</summary>
public static class ToolbarLayout
{
    private const double SeparatorWidth = 12;

    public static IReadOnlyList<string> HeaderItems(ToolbarConfiguration configuration, ToolbarSection section)
    {
        if (!Enum.IsDefined(section)) throw new ArgumentOutOfRangeException(nameof(section));
        return configuration.Items.Where(ToolbarConfiguration.IsHeaderItem).Where(id => section switch
        {
            ToolbarSection.Combined => true,
            ToolbarSection.Reading => SectionFor(id) != ToolbarSection.Sidebar,
            _ => SectionFor(id) == section
        }).ToArray();
    }

    private static ToolbarSection SectionFor(string id) => id switch
    {
        "refresh" or "newSidebarItemMenu" => ToolbarSection.Sidebar,
        "markAllAsRead" or "toggleReadArticlesFilter" or "cleanUp" => ToolbarSection.Timeline,
        _ => ToolbarSection.Article
    };

    public static ToolbarLayoutResult CalculateHeader(ToolbarConfiguration configuration, ToolbarSection section,
        IReadOnlyDictionary<string, double> itemWidths, double availableWidth, string? priorityItem = null)
        => configuration.IsVisible
            ? Calculate(configuration.WithItems(HeaderItems(configuration, section)), itemWidths, availableWidth, priorityItem: priorityItem)
            : new([], []);

    public static ToolbarLayoutResult Calculate(ToolbarConfiguration configuration,
        IReadOnlyDictionary<string, double> itemWidths, double availableWidth, bool columnLayout = false,
        bool sidebarVisible = true, bool readingOnly = false, double? sidebarBoundary = null,
        double? timelineBoundary = null, string? priorityItem = null)
    {
        availableWidth = double.IsFinite(availableWidth) ? Math.Max(0, availableWidth) : 0;
        var items = configuration.Items;
        var visible = Enumerable.Range(0, items.Count).Where(index => !ToolbarConfiguration.IsStructural(items[index])).ToHashSet();
        double Width(string id) => itemWidths.TryGetValue(id, out double width) && double.IsFinite(width) && width > 0
            ? width : id == "search" ? 220 : 36;

        List<ToolbarSlot> MakeSlots()
        {
            var slots = new List<ToolbarSlot>();
            bool lastWasSeparator = true;
            for (int index = 0; index < items.Count; index++)
            {
                var id = items[index];
                if (visible.Contains(index))
                {
                    slots.Add(new(index, id, Width(id)));
                    lastWasSeparator = false;
                }
                else if (id == "flexibleSpace") slots.Add(new(index, id, 0));
                else if (ToolbarConfiguration.IsSeparator(id) && !lastWasSeparator
                    && visible.Any(other => other > index)
                    && (id == "sidebarTrackingSeparator" ? sidebarVisible && !readingOnly : !columnLayout && !readingOnly))
                {
                    slots.Add(new(index, id, SeparatorWidth));
                    lastWasSeparator = true;
                }
            }
            return slots;
        }

        var slots = MakeSlots();
        while (slots.Sum(slot => slot.Width) > availableWidth)
        {
            int remove = visible.Where(index => items[index] != priorityItem).DefaultIfEmpty(-1).Max();
            if (remove < 0) break;
            visible.Remove(remove);
            slots = MakeSlots();
        }
        // Search (or a focused command) stays reachable even in a window narrower than its preferred width.
        if (slots.Sum(slot => slot.Width) > availableWidth)
        {
            for (int index = 0; index < slots.Count; index++)
                if (!ToolbarConfiguration.IsStructural(slots[index].Identifier))
                    slots[index] = slots[index] with { Width = availableWidth };
        }

        double spare = Math.Max(0, availableWidth - slots.Sum(slot => slot.Width));
        int lastAnchor = -1;
        for (int index = 0; index < slots.Count; index++)
        {
            double? boundary = slots[index].Identifier switch
            {
                "sidebarTrackingSeparator" => sidebarBoundary,
                "timelineTrackingSeparator" => timelineBoundary,
                _ => null
            };
            if (boundary is not { } target || !double.IsFinite(target) || target < 0) continue;
            double position = slots.Take(index).Sum(slot => slot.Width) + SeparatorWidth / 2;
            double gap = Math.Min(spare, Math.Max(0, target - position));
            var spaces = Enumerable.Range(lastAnchor + 1, index - lastAnchor - 1)
                .Where(other => slots[other].Identifier == "flexibleSpace").ToArray();
            if (spaces.Length > 0)
                foreach (int space in spaces) slots[space] = slots[space] with { Width = slots[space].Width + gap / spaces.Length };
            else slots[index] = slots[index] with { Width = slots[index].Width + gap };
            spare -= gap;
            lastAnchor = index;
        }
        var remainingSpaces = Enumerable.Range(lastAnchor + 1, slots.Count - lastAnchor - 1)
            .Where(index => slots[index].Identifier == "flexibleSpace").ToArray();
        foreach (int index in remainingSpaces)
            slots[index] = slots[index] with { Width = slots[index].Width + spare / remainingSpaces.Length };

        return new(slots.AsReadOnly(), Array.AsReadOnly(Enumerable.Range(0, items.Count)
            .Where(index => !ToolbarConfiguration.IsStructural(items[index]) && !visible.Contains(index))
            .Select(index => items[index]).ToArray()));
    }
}
