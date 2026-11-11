using WinNewsWire.Account;
using WinNewsWire.Core;
using WinNewsWire.Tree;

namespace WinNewsWire.AppShared.Commands;

/// <summary>Port of <c>DeleteCommand</c>. Captures enough state to restore feeds/folders on undo.</summary>
public sealed class DeleteCommand : IUndoableCommand
{
    private readonly List<Spec> _specs;
    private readonly TreeController? _tree;
    private readonly bool _allMemberships;
    private readonly HashSet<Spec> _restored = [];

    public string UndoActionName { get; }
    public string RedoActionName { get; }

    private sealed record Spec(Account.Account Account, Folder? ParentFolder, Feed? Feed, Folder? Folder, IReadOnlyList<Folder?> Memberships);

    public static DeleteCommand ForFeed(Account.Account account, Feed feed, Folder? parentFolder)
        => new([new(account, parentFolder, feed, null, Memberships(account, feed))], "Delete Feed", null);

    public static DeleteCommand ForFolder(Account.Account account, Folder folder)
        => new([new(account, null, null, folder, [])], "Delete Folder", null);

    public static DeleteCommand? ForAllFeedMemberships(IEnumerable<(Account.Account Account, Feed Feed)> feeds)
    {
        var specs = new List<Spec>();
        int count = 0;
        foreach (var (account, feed) in feeds.DistinctBy(item => (item.Account.AccountID, item.Feed.Url)))
        {
            if (account.AccountID != feed.AccountID)
                throw new ArgumentException("The feed does not belong to the selected account.", nameof(feeds));
            var memberships = Memberships(account, feed);
            if (memberships.Count == 0)
                throw new InvalidOperationException("The selected feed is no longer subscribed.");
            foreach (var container in memberships)
                specs.Add(new(account, container, feed, null, memberships));
            count++;
        }
        return count == 0 ? null : new DeleteCommand(specs, NameFor(count, 0), null, allMemberships: true);
    }

    public static DeleteCommand? Create(IEnumerable<Node> nodesToDelete, TreeController? tree = null)
    {
        var nodes = nodesToDelete.ToList();
        var selectedFolders = nodes.Select(n => n.RepresentedObject).OfType<Folder>().ToHashSet();
        var specs = new List<Spec>();
        int feeds = 0, folders = 0;
        foreach (var n in nodes)
        {
            if (n.RepresentedObject is Feed f)
            {
                if (n.Parent?.RepresentedObject is Folder parentFolder && selectedFolders.Contains(parentFolder)) continue;
                var acct = AccountManager.Shared.Accounts.FirstOrDefault(a => a.AccountID == f.AccountID);
                if (acct is null) return null;
                var parent = n.Parent?.RepresentedObject as Folder;
                specs.Add(new Spec(acct, parent, f, null, Memberships(acct, f)));
                feeds++;
            }
            else if (n.RepresentedObject is Folder fl)
            {
                var acct = AccountManager.Shared.Accounts.FirstOrDefault(a => a.AccountID == fl.AccountID);
                if (acct is null) return null;
                specs.Add(new Spec(acct, null, null, fl, []));
                folders++;
            }
            else return null;
        }
        if (specs.Count == 0) return null;
        var name = NameFor(feeds, folders);
        return new DeleteCommand(specs, name, tree);
    }

    private DeleteCommand(List<Spec> specs, string name, TreeController? tree, bool allMemberships = false)
    {
        _specs = specs; UndoActionName = name; RedoActionName = name; _tree = tree;
        _allMemberships = allMemberships;
    }

    public async Task PerformAsync()
    {
        var completed = new List<Spec>();
        var removedFeeds = new HashSet<(string AccountID, string Url)>();
        try
        {
            foreach (var spec in _specs)
            {
                if (spec.Feed is not null)
                {
                    var key = (spec.Account.AccountID, spec.Feed.Url);
                    if (_allMemberships && removedFeeds.Contains(key) && !HasMembership(spec.Account, spec.Feed, spec.ParentFolder))
                        continue;
                    await spec.Account.RemoveFeedAsync(spec.Feed, spec.ParentFolder);
                    removedFeeds.Add(key);
                }
                else if (spec.Folder is not null) await spec.Account.RemoveFolderAsync(spec.Folder);
                completed.Add(spec);
            }
            _restored.Clear();
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var spec in completed.AsEnumerable().Reverse())
                try { await RestoreAsync(spec); }
                catch (Exception rollback) { errors.Add(rollback); }
            if (errors.Count > 1) throw new AggregateException("Deletion and rollback could not finish.", errors);
            throw;
        }
        finally { _tree?.Rebuild(); }
    }

    public async Task UndoAsync()
    {
        try
        {
            foreach (var spec in _specs.OrderBy(spec => spec.Folder is null ? 1 : 0))
            {
                if (_restored.Contains(spec)) continue;
                await RestoreAsync(spec);
                _restored.Add(spec);
            }
        }
        finally { _tree?.Rebuild(); }
    }

    public Task RedoAsync() => PerformAsync();

    private static async Task RestoreAsync(Spec spec)
    {
        if (spec.Feed is not null)
        {
            foreach (var container in spec.Memberships)
                if (!HasMembership(spec.Account, spec.Feed, container))
                    await spec.Account.RestoreFeedAsync(spec.Feed, container);
        }
        else await spec.Account.RestoreFolderAsync(spec.Folder!);
    }

    private static IReadOnlyList<Folder?> Memberships(Account.Account account, Feed feed)
    {
        var folders = account.Folders.Where(folder => folder.Feeds.Contains(feed)).Cast<Folder?>().ToList();
        if (account.TopLevelFeeds.Contains(feed)) folders.Insert(0, null);
        return folders;
    }

    private static bool HasMembership(Account.Account account, Feed feed, Folder? folder)
        => (folder?.Feeds ?? account.TopLevelFeeds).Any(candidate => candidate.Url == feed.Url);

    private static string NameFor(int feeds, int folders)
    {
        if (folders == 0) return feeds == 1 ? "Delete Feed" : "Delete Feeds";
        if (feeds == 0) return folders == 1 ? "Delete Folder" : "Delete Folders";
        return "Delete Feeds and Folders";
    }
}
