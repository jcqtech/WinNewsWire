using WinNewsWire.Account;

namespace WinNewsWire.AppShared.Timeline;

public sealed class ReadFilterState
{
    private readonly Dictionary<string, bool> _values;
    public ReadFilterState(IReadOnlyDictionary<string, bool>? values = null)
        => _values = values is null ? new() : new(values);

    public bool IsEnabled(SidebarItemIdentifier? identifier, ReadFilterType defaultType)
        => defaultType == ReadFilterType.AlwaysRead
            || (identifier is not null && _values.TryGetValue(identifier.StorageKey, out var enabled)
                ? enabled : defaultType == ReadFilterType.Read);

    public void SetEnabled(SidebarItemIdentifier identifier, bool enabled) => _values[identifier.StorageKey] = enabled;
    public Dictionary<string, bool> Snapshot() => new(_values);
}
