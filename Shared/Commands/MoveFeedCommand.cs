using WinNewsWire.Account;
using WinNewsWire.Core;

namespace WinNewsWire.AppShared.Commands;

public sealed class MoveFeedCommand : IUndoableCommand
{
    private readonly Account.Account _account;
    private readonly Feed _feed;
    private readonly Folder? _source;
    private readonly Folder? _destination;
    private readonly bool _destinationAlreadyContainedFeed;

    public string UndoActionName => "Move Feed";
    public string RedoActionName => "Move Feed";

    public MoveFeedCommand(Account.Account account, Feed feed, Folder? source, Folder? destination)
    {
        _account = account; _feed = feed; _source = source; _destination = destination;
        _destinationAlreadyContainedFeed = (destination?.Feeds ?? account.TopLevelFeeds).Contains(feed);
    }

    public Task PerformAsync() => _account.MoveFeedAsync(_feed, _source, _destination);
    public Task RedoAsync() => PerformAsync();
    public Task UndoAsync()
    {
        var feed = _account.ExistingFeedWithUrl(_feed.Url) ?? _feed;
        return _destinationAlreadyContainedFeed || !(_destination?.Feeds ?? _account.TopLevelFeeds).Contains(feed)
            ? _account.RestoreFeedAsync(_feed, _source)
            : _account.MoveFeedAsync(feed, _destination, _source);
    }
}
