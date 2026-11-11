using System.Text.Json;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Toolbar;

public enum ToolbarDisplayMode { IconsOnly, IconsAndText, TextOnly }

/// <summary>One versioned toolbar preference, shared by both timeline layouts and all main windows.</summary>
public sealed class ToolbarConfiguration
{
    public const string PreferenceKey = "MainWindowToolbar";
    public const int MaximumFlexibleSpaces = 8;

    public static IReadOnlyList<string> AllowedItems { get; } = Array.AsReadOnly<string>(
    [
        "toggleSidebar", "refresh", "newSidebarItemMenu", "sidebarTrackingSeparator",
        "markAllAsRead", "toggleReadArticlesFilter", "timelineTrackingSeparator", "flexibleSpace",
        "nextUnread", "markRead", "markStar", "readerView", "openInWebView", "openInBrowser", "share",
        "articleThemeMenu", "search", "cleanUp"
    ]);

    public static IReadOnlyList<string> DefaultItems { get; } = Array.AsReadOnly<string>(
    [
        "toggleSidebar", "flexibleSpace", "refresh", "newSidebarItemMenu", "sidebarTrackingSeparator",
        "markAllAsRead", "toggleReadArticlesFilter", "timelineTrackingSeparator", "markRead",
        "markStar", "nextUnread", "readerView", "share", "openInWebView", "openInBrowser", "flexibleSpace", "search"
    ]);

    public static ToolbarConfiguration Default { get; } = new(true, ToolbarDisplayMode.IconsOnly, DefaultItems);
    public bool IsVisible { get; }
    public ToolbarDisplayMode DisplayMode { get; }
    public IReadOnlyList<string> Items { get; }

    private ToolbarConfiguration(bool visible, ToolbarDisplayMode displayMode, IEnumerable<string> items)
    {
        IsVisible = visible;
        DisplayMode = displayMode;
        Items = Array.AsReadOnly(items.ToArray());
    }

    public static bool IsSeparator(string id) => id is "sidebarTrackingSeparator" or "timelineTrackingSeparator";
    public static bool IsStructural(string id) => IsSeparator(id) || id == "flexibleSpace";
    public static bool IsTitleBarItem(string id) => id is "search" or "toggleSidebar";
    public static bool IsHeaderItem(string id) => AllowedItems.Contains(id) && !IsStructural(id) && !IsTitleBarItem(id);
    public static string Label(string id) => AppStrings.Get("Toolbar_" + id);

    public ToolbarConfiguration WithVisibility(bool visible) => new(visible, DisplayMode, Items);

    public ToolbarConfiguration WithDisplayMode(ToolbarDisplayMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return new(IsVisible, mode, Items);
    }

    public ToolbarConfiguration WithItems(IEnumerable<string?> items) => new(IsVisible, DisplayMode, NormalizeItems(items));

    public ToolbarConfiguration WithHeaderItems(IEnumerable<string> items)
    {
        var replacements = items.Distinct(StringComparer.Ordinal).ToArray();
        if (replacements.Any(id => !IsHeaderItem(id))) throw new ArgumentException("Header items must be known commands.", nameof(items));
        var remaining = new Queue<string>(replacements);
        var result = new List<string>();
        foreach (var id in Items)
        {
            if (!IsHeaderItem(id)) result.Add(id);
            else if (remaining.TryDequeue(out var replacement)) result.Add(replacement);
        }
        result.AddRange(remaining);
        return WithItems(result);
    }

    public ToolbarConfiguration WithSearchVisible()
        => new(true, DisplayMode, Items.Contains("search") ? Items : Items.Append("search"));

    public static ToolbarConfiguration Load(AppDefaults defaults)
        => FromJson(defaults.GetValue<JsonElement>(PreferenceKey));

    public static ToolbarConfiguration FromJson(JsonElement saved)
    {
        if (saved.ValueKind != JsonValueKind.Object
            || !saved.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int number) || number is not (1 or 2))
            return Default;

        bool visible = !saved.TryGetProperty("visible", out var visibility) || visibility.ValueKind != JsonValueKind.False;
        var mode = saved.TryGetProperty("displayMode", out var display) && display.ValueKind == JsonValueKind.String
            ? display.GetString() switch
            {
                "iconsAndText" => ToolbarDisplayMode.IconsAndText,
                "text" => ToolbarDisplayMode.TextOnly,
                _ => ToolbarDisplayMode.IconsOnly
            } : ToolbarDisplayMode.IconsOnly;
        var items = saved.TryGetProperty("items", out var order) && order.ValueKind == JsonValueKind.Array
            ? NormalizeItems(order.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null))
            : DefaultItems;
        if (number == 1 && !items.Contains("openInWebView") && (items.Contains("openInBrowser") || items.Contains("share")))
        {
            var migrated = items.ToList();
            int index = migrated.IndexOf("openInBrowser");
            migrated.Insert(index >= 0 ? index : migrated.IndexOf("share") + 1, "openInWebView");
            items = migrated;
        }
        return new(visible, mode, items);
    }

    public void Save(AppDefaults defaults)
    {
        // AppDefaults commits the whole object atomically and restores its old value if the write fails.
        defaults.SetValue(PreferenceKey, new
        {
            version = 2,
            visible = IsVisible,
            displayMode = DisplayMode switch
            {
                ToolbarDisplayMode.IconsAndText => "iconsAndText",
                ToolbarDisplayMode.TextOnly => "text",
                _ => "icons"
            },
            items = Items
        });
    }

    private static IReadOnlyList<string> NormalizeItems(IEnumerable<string?> items)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int spaces = 0;
        bool hadItems = false;
        foreach (var id in items.Take(256))
        {
            hadItems = true;
            if (id is null || !AllowedItems.Contains(id)) continue;
            if (id == "flexibleSpace")
            {
                if (++spaces > MaximumFlexibleSpaces) continue;
            }
            else if (!seen.Add(id)) continue;
            result.Add(id);
        }
        // An intentionally empty toolbar is valid; an unrecognizable saved configuration is not.
        return hadItems && result.Count == 0 ? DefaultItems : result;
    }
}
