using WinNewsWire.Account;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.SmartFeeds;

/// <summary>Base implementation of <c>SmartFeed</c>.</summary>
public sealed class SmartFeed : IPseudoFeed
{
    private readonly ISmartFeedDelegate _delegate;
    private int _unreadCount;
    public string NameForDisplay => _delegate.NameForDisplay;
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
        foreach (var a in AccountManager.Shared.ActiveAccounts)
            set.UnionWith(await a.FetchUnreadAsync());
        return set;
    }

    public Task<HashSet<Article>> FetchUnreadArticlesAsync() => FetchArticlesAsync();

    public Task<int> FetchUnreadCountAsync(Account.Account account)
        => account.Database.UnreadCountAsync(account.FlattenedFeeds().Select(f => f.FeedID));
}

/// <summary>Port of <c>TodayFeedDelegate</c>.
/// Shows every article — read or unread — published since 12:00 AM in the
/// system's local time zone.</summary>
public sealed class TodayFeedDelegate : ISmartFeedDelegate
{
    public string NameForDisplay => "Today";
    public SmartFeedFetchType FetchType => SmartFeedFetchType.Today;

    private static DateTime StartOfTodayUtc()
        => DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Local).ToUniversalTime();

    public async Task<HashSet<Article>> FetchArticlesAsync()
    {
        var cutoff = StartOfTodayUtc();
        var set = new HashSet<Article>();
        foreach (var a in AccountManager.Shared.ActiveAccounts)
        {
            var all = await a.Database.FetchArticlesAsync(a.FlattenedFeeds().Select(f => f.FeedID));
            foreach (var art in all)
                if (art.DatePublished is { } d && d >= cutoff) set.Add(art);
        }
        return set;
    }

    public async Task<HashSet<Article>> FetchUnreadArticlesAsync()
    {
        var all = await FetchArticlesAsync();
        all.RemoveWhere(a => a.Status.Read);
        return all;
    }

    public async Task<int> FetchUnreadCountAsync(Account.Account account)
    {
        var articles = await account.Database.FetchUnreadArticlesAsync(account.FlattenedFeeds().Select(f => f.FeedID));
        var cutoff = StartOfTodayUtc();
        return articles.Count(a => a.DatePublished is { } d && d >= cutoff);
    }
}

/// <summary>Port of <c>StarredFeedDelegate</c>.</summary>
public sealed class StarredFeedDelegate : ISmartFeedDelegate
{
    public string NameForDisplay => "Starred";
    public SmartFeedFetchType FetchType => SmartFeedFetchType.Starred;

    public async Task<HashSet<Article>> FetchArticlesAsync()
    {
        var set = new HashSet<Article>();
        foreach (var a in AccountManager.Shared.ActiveAccounts)
            set.UnionWith(await a.Database.FetchStarredAsync(a.FlattenedFeeds().Select(f => f.FeedID)));
        return set;
    }

    public async Task<HashSet<Article>> FetchUnreadArticlesAsync()
    {
        var all = await FetchArticlesAsync();
        all.RemoveWhere(x => x.Status.Read);
        return all;
    }

    public async Task<int> FetchUnreadCountAsync(Account.Account account)
    {
        var s = await account.Database.FetchStarredAsync(account.FlattenedFeeds().Select(f => f.FeedID));
        return s.Count(a => !a.Status.Read);
    }
}
