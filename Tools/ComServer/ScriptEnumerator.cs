using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WinNewsWire.Automation;

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IEnumVARIANT)),
 TypeLibType(TypeLibTypeFlags.FHidden | TypeLibTypeFlags.FRestricted)]
public sealed class ScriptEnumerator : IEnumVARIANT
{
    private readonly object[] _items;
    private readonly AutomationSession _session;
    private int _index;
    internal ScriptEnumerator(AutomationSession session, object[] items, int index = 0)
        => (_session, _items, _index) = (session, items, index);

    public int Next(int count, object?[] values, nint fetched)
    {
        if (count < 0 || values.Length < count || fetched == 0 && count != 1) return Errors.InvalidArgument;
        int copied = Math.Min(count, _items.Length - _index);
        Array.Copy(_items, _index, values, 0, copied);
        _index += copied;
        if (fetched != 0) Marshal.WriteInt32(fetched, copied);
        return copied == count ? 0 : 1;
    }

    public int Skip(int count)
    {
        if (count < 0) return Errors.InvalidArgument;
        int skipped = Math.Min(count, _items.Length - _index);
        _index += skipped;
        return skipped == count ? 0 : 1;
    }

    public int Reset() { _index = 0; return 0; }
    public IEnumVARIANT Clone() => _session.Track(new ScriptEnumerator(_session, _items, _index));
}
