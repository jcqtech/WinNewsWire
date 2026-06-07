using WinNewsWire.Account;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.SmartFeeds;

/// <summary>Port of <c>SmartFeedsController</c>.</summary>
public sealed class SmartFeedsController
{
    public static SmartFeedsController Shared { get; } = new();
    public string NameForDisplay => "Smart Feeds";

    public SmartFeed TodayFeed { get; }
    public SmartFeed UnreadFeed { get; }
    public SmartFeed StarredFeed { get; }
    public IReadOnlyList<IPseudoFeed> SmartFeeds { get; }

    private SmartFeedsController()
    {
        TodayFeed = new SmartFeed(new TodayFeedDelegate());
        UnreadFeed = new SmartFeed(new UnreadFeedDelegate());
        StarredFeed = new SmartFeed(new StarredFeedDelegate());
        SmartFeeds = new IPseudoFeed[] { TodayFeed, UnreadFeed, StarredFeed };
    }

    public async Task RefreshAllAsync()
    {
        await TodayFeed.RefreshUnreadCountAsync();
        await UnreadFeed.RefreshUnreadCountAsync();
        await StarredFeed.RefreshUnreadCountAsync();
    }
}

/// <summary>Port of <c>SearchFeedDelegate</c> / <c>SearchTimelineFeedDelegate</c>.</summary>
public sealed class SearchFeed : IPseudoFeed
{
    public string Query { get; }
    public string NameForDisplay => $"Search: {Query}";
    public int UnreadCount => 0;
    public event EventHandler? UnreadCountChanged;

    public SearchFeed(string query) { Query = query; _ = UnreadCountChanged; }

    public async Task<HashSet<Article>> FetchArticlesAsync()
    {
        var set = new HashSet<Article>();
        foreach (var a in AccountManager.Shared.ActiveAccounts)
            set.UnionWith(await a.Database.SearchAsync(Query));
        return set;
    }

    public async Task<HashSet<Article>> FetchUnreadArticlesAsync()
    {
        var all = await FetchArticlesAsync();
        all.RemoveWhere(x => x.Status.Read);
        return all;
    }
}
