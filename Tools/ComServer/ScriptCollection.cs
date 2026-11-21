using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using WinNewsWire.AppShared.Scripting;

namespace WinNewsWire.Automation;

[ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IObjectCollection))]
public sealed class ScriptCollection : IObjectCollection
{
    internal AutomationSession Session { get; }
    private readonly Func<ScriptingReference[]> _references;
    private readonly Func<int>? _count;
    private readonly Func<int, int, ScriptingReference[]>? _page;
    internal ScriptCollection(AutomationSession session, Func<ScriptingReference[]> references,
        Func<int>? count = null, Func<int, int, ScriptingReference[]>? page = null)
        => (Session, _references, _count, _page) = (session, references, count, page);
    internal ScriptingReference[] References() => _references();

    public int Count => _count?.Invoke() ?? References().Length;
    public object this[object indexOrName]
    {
        get
        {
            if (indexOrName is string name) return ByName(name);
            int index = indexOrName switch
            {
                byte number => number, short number => number, int number => number,
                long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
                double number when double.IsFinite(number) && number == Math.Truncate(number)
                    && number is >= int.MinValue and <= int.MaxValue => (int)number,
                _ => throw new COMException("A collection index must be a one-based integer or a name.", Errors.InvalidArgument)
            };
            if (index >= 1 && _page is not null)
            {
                var page = _page(index - 1, 1);
                if (page.Length == 1) return Session.Wrap(page[0]);
                throw new COMException("The collection index is out of range (indices start at 1).", Errors.NotFound);
            }
            var items = References();
            if (index < 1 || index > items.Length)
                throw new COMException("The collection index is out of range (indices start at 1).", Errors.NotFound);
            return Session.Wrap(items[index - 1]);
        }
    }

    public object ByID(object id)
    {
        var items = References();
        var match = items.FirstOrDefault(reference => reference.Kind == ScriptingObjectKind.Folder
            ? id is byte or short or int or long or double && Equals(reference.FolderID, NormalizeFolderID(id))
            : id is string text && text == (reference.Kind switch
            {
                ScriptingObjectKind.Account => reference.AccountID,
                ScriptingObjectKind.Feed => reference.FeedID,
                ScriptingObjectKind.Article => reference.ArticleID,
                ScriptingObjectKind.Author => reference.AuthorID,
                _ => throw new COMException("This collection does not support ID lookup.", Errors.InvalidArgument)
            }));
        return match is null ? throw new COMException("No object with that ID exists in this collection.", Errors.NotFound)
            : Session.Wrap(match);
    }

    private static object? NormalizeFolderID(object value)
    {
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        return double.IsFinite(number) && number == Math.Truncate(number)
            && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;
    }

    public object ByName(string name)
    {
        var match = References().FirstOrDefault(reference =>
            string.Equals(Session.Get(reference, "name") as string, name, StringComparison.Ordinal));
        return match is null ? throw new COMException("No object with that name exists in this collection.", Errors.NotFound)
            : Session.Wrap(match);
    }

    public bool Exists(object indexOrName)
    {
        try { _ = this[indexOrName]; return true; }
        catch (COMException error) when (error.HResult == Errors.NotFound) { return false; }
    }

    public object?[] Get(string property) => Session.GetMany(References(), property);

    public object Where(string property, object value)
    {
        // Freeze membership, but retain live object references just like an evaluated script specifier.
        var items = References();
        var values = Session.GetMany(items, property);
        var matches = items.Where((reference, index) => Equals(values[index], value)).ToArray();
        return Session.Track(new ScriptCollection(Session, () => matches));
    }

    public object GetPage(int start, int count)
    {
        if (start < 1 || count is < 1 or > 4096)
            throw new COMException("Pages use a one-based start and a count of 1 through 4096.", Errors.InvalidArgument);
        var items = _page?.Invoke(start - 1, count) ?? References().Skip(start - 1).Take(count).ToArray();
        return Session.Track(new ScriptCollection(Session, () => items));
    }

    public void Delete()
    {
        var items = References();
        if (items.Any(item => item.Kind is not (ScriptingObjectKind.Feed or ScriptingObjectKind.Folder)))
            throw new COMException("Only feeds and folders can be deleted.", Errors.InvalidArgument);
        foreach (var item in items) Session.Send(new("delete", item));
    }

    public IEnumerator GetEnumerator() => References().Select(reference => Session.Wrap(reference)).ToArray().GetEnumerator();
    public object _NewEnum => Session.Track(new ScriptEnumerator(Session, References().Select(reference => (object)Session.Wrap(reference)).ToArray()));
}
