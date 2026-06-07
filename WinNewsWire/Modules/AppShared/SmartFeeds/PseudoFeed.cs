using WinNewsWire.Account;
using WinNewsWire.Articles;

namespace WinNewsWire.AppShared.SmartFeeds;

/// <summary>Port of <c>FetchType</c>.</summary>
public enum SmartFeedFetchType { Unread, Today, Starred, Search }

/// <summary>Port of <c>PseudoFeed</c>.</summary>
public interface IPseudoFeed
{
    string NameForDisplay { get; }
    int UnreadCount { get; }
    Task<HashSet<Article>> FetchArticlesAsync();
    Task<HashSet<Article>> FetchUnreadArticlesAsync();
    event EventHandler? UnreadCountChanged;
}

/// <summary>Port of <c>SmartFeedDelegate</c>.</summary>
public interface ISmartFeedDelegate
{
    string NameForDisplay { get; }
    SmartFeedFetchType FetchType { get; }
    Task<HashSet<Article>> FetchArticlesAsync();
    Task<HashSet<Article>> FetchUnreadArticlesAsync();
    Task<int> FetchUnreadCountAsync(Account.Account account);
}
