using WinNewsWire.Account;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.SmartFeeds;

/// <summary>Base implementation of <c>SmartFeed</c>.</summary>
public sealed class SmartFeed : IPseudoFeed
{
    private readonly ISmartFeedDelegate _delegate;
    private int _unreadCount;
    public string NameForDisplay => _delegate.NameForDisplay;
    public SidebarItemIdentifier SidebarItemID => SidebarItemIdentifier.SmartFeed(_delegate.GetType().Name);
    public ReadFilterType DefaultReadFilterType => _delegate.FetchType == SmartFeedFetchType.Unread
        ? ReadFilterType.AlwaysRead : ReadFilterType.None;
    public int UnreadCount { get => _unreadCount; private set { if (_unreadCount != value) { _unreadCount = value; UnreadCountChanged?.Invoke(this, EventArgs.Empty); } } }
    public event EventHandler? UnreadCountChanged;

    public SmartFeed(ISmartFeedDelegate @delegate) { _delegate = @delegate; }

    public Task<HashSet<Article>> FetchArticlesAsync() => _delegate.FetchArticlesAsync();
    public Task<HashSet<Article>> FetchUnreadArticlesAsync() => _delegate.FetchUnreadArticlesAsync();

    public async Task RefreshUnreadCountAsync()
    {
        int total = 0;
        foreach (var a in AccountManager.Shared.ActiveAccounts)
            total += await _delegate.FetchUnreadCountAsync(a);
        UnreadCount = total;
    }
}

/// <summary>Port of <c>UnreadFeed</c>.</summary>
public sealed class UnreadFeedDelegate : ISmartFeedDelegate
{
    public string NameForDisplay => "All Unread";
    public SmartFeedFetchType FetchType => SmartFeedFetchType.Unread;

    public async Task<HashSet<Article>> FetchArticlesAsync()
    {
        var set = new HashSet<Article>();
        var requests = AccountManager.Shared.ActiveAccounts.Select(account => account.FetchUnreadAsync()).ToArray();
        foreach (var articles in await Task.WhenAll(requests).ConfigureAwait(false)) set.UnionWith(articles);
        return set;
    }

    public Task<HashSet<Article>> FetchUnreadArticlesAsync() => FetchArticlesAsync();

    public Task<int> FetchUnreadCountAsync(Account.Account account)
        => account.Database.UnreadCountAsync(account.FlattenedFeeds().Select(f => f.FeedID));
}

/// <summary>Port of <c>TodayFeedDelegate</c>.
/// Shows articles published in the last 24 hours, falling back to arrival date.</summary>
public sealed class TodayFeedDelegate : ISmartFeedDelegate
{
    public string NameForDisplay => "Today";
    public SmartFeedFetchType FetchType => SmartFeedFetchType.Today;

    public async Task<HashSet<Article>> FetchArticlesAsync()
    {
        var set = new HashSet<Article>();
        var requests = AccountManager.Shared.ActiveAccounts.Select(account =>
            account.Database.FetchTodayArticlesAsync(account.FlattenedFeeds().Select(feed => feed.FeedID).ToArray())).ToArray();
        foreach (var articles in await Task.WhenAll(requests).ConfigureAwait(false)) set.UnionWith(articles);
        return set;
    }

    public async Task<HashSet<Article>> FetchUnreadArticlesAsync()
    {
        var all = await FetchArticlesAsync().ConfigureAwait(false);
        all.RemoveWhere(a => a.Status.Read);
        return all;
    }

    public Task<int> FetchUnreadCountAsync(Account.Account account)
        => account.Database.FetchUnreadCountForTodayAsync(account.FlattenedFeeds().Select(f => f.FeedID));
}

/// <summary>Port of <c>StarredFeedDelegate</c>.</summary>
public sealed class StarredFeedDelegate : ISmartFeedDelegate
{
    public string NameForDisplay => "Starred";
    public SmartFeedFetchType FetchType => SmartFeedFetchType.Starred;

    public async Task<HashSet<Article>> FetchArticlesAsync()
    {
        var set = new HashSet<Article>();
        var requests = AccountManager.Shared.ActiveAccounts.Select(account =>
            account.Database.FetchStarredAsync(account.FlattenedFeeds().Select(feed => feed.FeedID).ToArray())).ToArray();
        foreach (var articles in await Task.WhenAll(requests).ConfigureAwait(false)) set.UnionWith(articles);
        return set;
    }

    public async Task<HashSet<Article>> FetchUnreadArticlesAsync()
    {
        var all = await FetchArticlesAsync().ConfigureAwait(false);
        all.RemoveWhere(x => x.Status.Read);
        return all;
    }

    public async Task<int> FetchUnreadCountAsync(Account.Account account)
    {
        var s = await account.Database.FetchStarredAsync(account.FlattenedFeeds().Select(f => f.FeedID)).ConfigureAwait(false);
        return s.Count(a => !a.Status.Read);
    }
}
