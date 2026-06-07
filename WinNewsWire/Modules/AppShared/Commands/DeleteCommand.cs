using WinNewsWire.Account;
using WinNewsWire.Core;
using WinNewsWire.Tree;

namespace WinNewsWire.AppShared.Commands;

/// <summary>Port of <c>DeleteCommand</c>. Captures enough state to restore feeds/folders on undo.</summary>
public sealed class DeleteCommand : IUndoableCommand
{
    private readonly List<Spec> _specs;
    private readonly TreeController? _tree;

    public string UndoActionName { get; }
    public string RedoActionName { get; }

    private sealed record Spec(Account.Account Account, Folder? ParentFolder, Feed? Feed, Folder? Folder);

    public static DeleteCommand? Create(IEnumerable<Node> nodesToDelete, TreeController? tree = null)
    {
        var specs = new List<Spec>();
        int feeds = 0, folders = 0;
        foreach (var n in nodesToDelete)
        {
            if (n.RepresentedObject is Feed f)
            {
                var acct = AccountManager.Shared.Accounts.FirstOrDefault(a => a.AccountID == f.AccountID);
                if (acct is null) return null;
                var parent = n.Parent?.RepresentedObject as Folder;
                specs.Add(new Spec(acct, parent, f, null));
                feeds++;
            }
            else if (n.RepresentedObject is Folder fl)
            {
                var acct = AccountManager.Shared.Accounts.FirstOrDefault(a => a.AccountID == fl.AccountID);
                if (acct is null) return null;
                specs.Add(new Spec(acct, null, null, fl));
                folders++;
            }
            else return null;
        }
        if (specs.Count == 0) return null;
        var name = NameFor(feeds, folders);
        return new DeleteCommand(specs, name, tree);
    }

    private DeleteCommand(List<Spec> specs, string name, TreeController? tree)
    {
        _specs = specs; UndoActionName = name; RedoActionName = name; _tree = tree;
    }

    public void Perform()
    {
        foreach (var s in _specs)
        {
            if (s.Feed is not null) s.Account.RemoveFeed(s.Feed);
            else if (s.Folder is not null) s.Account.RemoveFolder(s.Folder);
        }
        _tree?.Rebuild();
    }

    public void Undo()
    {
        foreach (var s in _specs)
        {
            if (s.Feed is not null) s.Account.RestoreFeed(s.Feed, s.ParentFolder);
            else if (s.Folder is not null) s.Account.RestoreFolder(s.Folder);
        }
        _tree?.Rebuild();
    }

    public void Redo() => Perform();

    private static string NameFor(int feeds, int folders)
    {
        if (folders == 0) return feeds == 1 ? "Delete Feed" : "Delete Feeds";
        if (feeds == 0) return folders == 1 ? "Delete Folder" : "Delete Folders";
        return "Delete Feeds and Folders";
    }
}
