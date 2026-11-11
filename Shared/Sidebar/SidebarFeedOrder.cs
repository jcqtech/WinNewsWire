using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WinNewsWire.Account;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Sidebar;

public sealed class SidebarFeedOrder
{
    public const string PreferenceKey = "sidebarFeedOrder";
    private readonly Dictionary<string, string[]> _containers;

    private SidebarFeedOrder(Dictionary<string, string[]> containers) => _containers = containers;

    public static SidebarFeedOrder Load(AppDefaults defaults)
    {
        var containers = defaults.GetValue<Dictionary<string, string[]>>(PreferenceKey) ?? [];
        foreach (var key in containers.Keys.ToArray())
        {
            if (containers[key] is { } order && order.All(url => !string.IsNullOrWhiteSpace(url))) continue;
            Trace.TraceWarning("Ignoring an invalid saved sidebar feed order.");
            containers.Remove(key);
        }
        return new(containers);
    }

    private static string Key(Account.Account account, Folder? folder)
        => JsonSerializer.Serialize(new[] { account.AccountID, folder?.FolderID.ToString(CultureInfo.InvariantCulture) ?? "" });

    public IReadOnlyList<Feed> OrderedFeeds(Account.Account account, Folder? folder = null)
    {
        var saved = SavedOrder(account, folder) ?? [];
        var ranks = saved.Distinct(StringComparer.Ordinal).Select((url, index) => (url, index))
            .ToDictionary(item => item.url, item => item.index, StringComparer.Ordinal);
        return (folder?.Feeds ?? account.TopLevelFeeds)
            .OrderBy(feed => ranks.GetValueOrDefault(feed.Url, int.MaxValue))
            .ThenBy(feed => feed.NameForDisplay, LocalizedStandardComparer.Shared)
            .ThenBy(feed => feed.Url, StringComparer.Ordinal).ToArray();
    }

    public string[]? SavedOrder(Account.Account account, Folder? folder)
        => _containers.TryGetValue(Key(account, folder), out var order) ? order.ToArray() : null;

    public static void Save(AppDefaults defaults, Account.Account account, Folder? folder, string[]? order)
    {
        var containers = Load(defaults)._containers.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        var key = Key(account, folder);
        if (order is null) containers.Remove(key);
        else containers[key] = order.Distinct(StringComparer.Ordinal).ToArray();
        if (containers.Count == 0) defaults.RemoveValue(PreferenceKey);
        else defaults.SetValue(PreferenceKey, containers);
    }

    public static string[] PlaceBefore(IEnumerable<string> current, IEnumerable<string> moving, string? before)
    {
        var block = moving.Distinct(StringComparer.Ordinal).ToArray();
        if (block.Length == 0) throw new ArgumentException("Choose at least one feed to move.", nameof(moving));
        var moved = block.ToHashSet(StringComparer.Ordinal);
        var result = current.Where(url => !moved.Contains(url)).ToList();
        int index = before is null ? result.Count : result.IndexOf(before);
        if (index < 0) throw new InvalidOperationException("The insertion target is no longer available.");
        result.InsertRange(index, block);
        return result.ToArray();
    }
}
