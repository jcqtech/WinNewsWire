using WinNewsWire.Account;
using WinNewsWire.AppShared.Sidebar;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Commands;

public sealed class PlaceFeedsCommand : IUndoableCommand
{
    private readonly Account.Account _account;
    private readonly (Feed Feed, Folder? Source)[] _feeds;
    private readonly Folder? _destination;
    private readonly string? _before;
    private readonly AppDefaults _defaults;
    private readonly List<MoveFeedCommand> _moves = [];
    private string[]? _previousOrder;
    private string[]? _nextOrder;

    public string UndoActionName => "Move Feeds";
    public string RedoActionName => "Move Feeds";

    public PlaceFeedsCommand(Account.Account account, IEnumerable<(Feed Feed, Folder? Source)> feeds,
        Folder? destination, string? before, AppDefaults defaults)
    {
        _account = account;
        _feeds = feeds.DistinctBy(item => (item.Feed.Url, item.Source?.FolderID)).ToArray();
        _destination = destination;
        _before = before;
        _defaults = defaults;
        if (_feeds.Length == 0) throw new ArgumentException("Choose at least one feed to move.", nameof(feeds));
    }

    public async Task PerformAsync()
    {
        if (!_account.IsActive || _destination is not null && !_account.Folders.Contains(_destination))
            throw new InvalidOperationException("The destination account or folder is no longer available.");
        foreach (var (feed, source) in _feeds)
        {
            if (feed.AccountID != _account.AccountID || source is not null && !_account.Folders.Contains(source)
                || !(source?.Feeds ?? _account.TopLevelFeeds).Contains(feed))
                throw new InvalidOperationException("A dragged feed is no longer in its source container.");
            if (!ReferenceEquals(source, _destination) && (_destination?.Feeds ?? _account.TopLevelFeeds).Contains(feed))
                throw new InvalidOperationException("The destination already contains this feed.");
        }
        if (_nextOrder is null)
        {
            var order = SidebarFeedOrder.Load(_defaults);
            _previousOrder = order.SavedOrder(_account, _destination);
            _nextOrder = SidebarFeedOrder.PlaceBefore(order.OrderedFeeds(_account, _destination).Select(feed => feed.Url),
                _feeds.Select(item => item.Feed.Url), _before);
            _moves.AddRange(_feeds.Where(item => !ReferenceEquals(item.Source, _destination))
                .Select(item => new MoveFeedCommand(_account, item.Feed, item.Source, _destination)));
        }
        await ApplyAsync(undo: false);
    }

    public Task RedoAsync() => PerformAsync();
    public Task UndoAsync() => ApplyAsync(undo: true);

    private async Task ApplyAsync(bool undo)
    {
        if (_nextOrder is null) throw new InvalidOperationException("The feed placement has not been performed.");
        var completed = new List<MoveFeedCommand>();
        try
        {
            foreach (var move in undo ? _moves.AsEnumerable().Reverse() : _moves)
            {
                completed.Add(move);
                if (undo) await move.UndoAsync();
                else await move.PerformAsync();
            }
            SidebarFeedOrder.Save(_defaults, _account, _destination, undo ? _previousOrder : _nextOrder);
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var move in completed.AsEnumerable().Reverse())
            {
                try
                {
                    if (undo) await move.PerformAsync();
                    else await move.UndoAsync();
                }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            if (errors.Count > 1) throw new AggregateException("Moving feeds and restoring their previous placement could not finish.", errors);
            throw;
        }
    }
}
