using System;
using System.Collections.Concurrent;
using System.Linq;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using WinNewsWire.Account;
using WinNewsWire.Articles;

namespace WinNewsWire.Services;

/// <summary>
/// Port of NetNewsWire's <c>UserNotificationManager</c>. Subscribes to
/// <see cref="Account.NewArticlesDownloaded"/> and posts a Windows toast for every
/// new unread article whose feed has <see cref="Feed.NewArticleNotificationsEnabled"/>
/// set. Listens for <see cref="Account.StatusesChanged"/> and dismisses any pending
/// toasts when the user marks an article read (mirrors the iOS/Mac behavior).
/// </summary>
public sealed class NewArticleNotifier : IDisposable
{
    private readonly AccountManager _accounts;
    private readonly ConcurrentDictionary<string, byte> _liveTags = new();
    private bool _registered;

    public NewArticleNotifier(AccountManager accounts)
    {
        _accounts = accounts;
    }

    public void Start()
    {
        if (_registered) return;
        try
        {
            // AppNotificationManager.Register sets up the COM activator that lets
            // unpackaged apps post toasts. It's a no-op for packaged apps too.
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            // If registration fails (e.g. no AppId on a heavily sandboxed runtime),
            // skip notifications entirely. We must not crash the app over a missing
            // notification platform.
            _registered = false;
            return;
        }

        foreach (var account in _accounts.Accounts) Hook(account);
        _accounts.AccountsDidChange += OnAccountsChanged;
    }

    public void Stop()
    {
        _accounts.AccountsDidChange -= OnAccountsChanged;
        foreach (var account in _accounts.Accounts) Unhook(account);
        if (_registered)
        {
            try { AppNotificationManager.Default.Unregister(); } catch { }
            _registered = false;
        }
    }

    public void Dispose() => Stop();

    private void OnAccountsChanged(object? sender, EventArgs e)
    {
        foreach (var account in _accounts.Accounts) Hook(account);
    }

    private void Hook(Account.Account account)
    {
        // Re-subscription is harmless: -=/+= ensures we never end up double-firing.
        account.NewArticlesDownloaded -= OnNewArticles;
        account.NewArticlesDownloaded += OnNewArticles;
        account.StatusesChanged -= OnStatusesChanged;
        account.StatusesChanged += OnStatusesChanged;
    }

    private void Unhook(Account.Account account)
    {
        account.NewArticlesDownloaded -= OnNewArticles;
        account.StatusesChanged -= OnStatusesChanged;
    }

    private void OnNewArticles(object? sender, NewArticlesEventArgs e)
    {
        if (sender is not Account.Account account) return;

        // Honor the app-wide toggle (Preferences > Notifications). When disabled,
        // every per-feed flag is treated as off so the user has a single switch.
        if (!WinNewsWire.Core.AppDefaults.Shared.NotificationsEnabled) return;

        // Build a feedID -> Feed lookup so we can consult per-feed settings without
        // re-flattening the account on every article.
        var feeds = account.FlattenedFeeds().ToDictionary(f => f.FeedID);

        foreach (var article in e.NewArticles)
        {
            if (article.Status.Read) continue;
            if (!feeds.TryGetValue(article.FeedID, out var feed)) continue;
            if (!feed.NewArticleNotificationsEnabled) continue;

            try { Send(feed, article); } catch { /* notifications are best-effort */ }
        }
    }

    private void OnStatusesChanged(object? sender, StatusesChangedEventArgs e)
    {
        if (e.Key != ArticleStatus.Key.Read || !e.Flag) return;
        foreach (var id in e.ArticleIDs)
        {
            if (!_liveTags.TryRemove(id, out _)) continue;
            try { _ = AppNotificationManager.Default.RemoveByTagAsync(TagFor(id)); } catch { }
        }
    }

    private void Send(Feed feed, Article article)
    {
        var title = feed.NameForDisplay;
        var subtitle = StripTags(article.Title);
        var body = TruncatedSummary(article);

        var builder = new AppNotificationBuilder()
            .AddText(title)
            .SetTag(TagFor(article.ArticleID))
            .SetGroup("WinNewsWire.NewArticle");

        if (!string.IsNullOrEmpty(subtitle))
            builder.AddText(subtitle);
        if (!string.IsNullOrEmpty(body))
            builder.AddText(body);

        // Launch arg lets a future activation handler open the article. We use a
        // pipe-delimited "articleID|link" payload to mirror NetNewsWire's
        // pathUserInfo idea without inventing a JSON schema yet.
        builder.AddArgument("articleID", article.ArticleID);
        builder.AddArgument("accountID", article.AccountID);
        builder.AddArgument("feedID", article.FeedID);

        var notification = builder.BuildNotification();
        AppNotificationManager.Default.Show(notification);
        _liveTags[article.ArticleID] = 0;
    }

    private static string TagFor(string articleID)
    {
        // Tags must be <=64 characters; hash collisions are extremely unlikely
        // but we still use a stable truncation so dismissal matches.
        if (articleID.Length <= 64) return "art:" + articleID;
        return "art:" + articleID.GetHashCode().ToString("x");
    }

    private static string StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        return System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
    }

    private static string TruncatedSummary(Article article)
    {
        var raw = article.Summary ?? article.ContentText ?? article.ContentHtml ?? "";
        var s = StripTags(raw);
        if (s.Length > 280) s = s[..280] + "\u2026";
        return s;
    }
}
