using System.Collections.Generic;
using WinNewsWire.Articles;

namespace WinNewsWire.Account;

/// <summary>Carries newly inserted articles raised by
/// <see cref="Account.NewArticlesDownloaded"/>. Mirrors NetNewsWire's
/// <c>AccountDidDownloadArticles</c> userInfo payload.</summary>
public sealed class NewArticlesEventArgs : System.EventArgs
{
    public IReadOnlyCollection<Article> NewArticles { get; }
    public NewArticlesEventArgs(IReadOnlyCollection<Article> newArticles) => NewArticles = newArticles;
}

/// <summary>Carries the article IDs whose status just changed. Mirrors NetNewsWire's
/// <c>StatusesDidChange</c> notification — consumed by the notifier to dismiss toasts
/// for articles the user has marked read.</summary>
public sealed class StatusesChangedEventArgs : System.EventArgs
{
    public IReadOnlyCollection<string> ArticleIDs { get; }
    public ArticleStatus.Key Key { get; }
    public bool Flag { get; }
    public StatusesChangedEventArgs(IReadOnlyCollection<string> articleIDs, ArticleStatus.Key key, bool flag)
    {
        ArticleIDs = articleIDs;
        Key = key;
        Flag = flag;
    }
}
