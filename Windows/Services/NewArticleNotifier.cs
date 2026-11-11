using System;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Windows.AppNotifications;
using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.AppShared.Extensions;

namespace WinNewsWire.Services;

/// <summary>
/// Port of NetNewsWire's <c>UserNotificationManager</c>. Subscribes to
/// <see cref="Account.NewArticlesDownloaded"/> and posts a Windows toast for every
/// new unread article whose feed has <see cref="Feed.NewArticleNotificationsEnabled"/>
/// set. Listens for <see cref="Account.StatusesChanged"/> and dismisses any pending
/// toasts when the user marks an article read (mirrors the iOS/Mac behavior).
/// Lifecycle calls run on the application dispatcher; native registration work does not.
/// </summary>
public sealed class NewArticleNotifier : IAsyncDisposable
{
    private readonly AccountManager _accounts;
    private readonly Action _register;
    private readonly Action _unregister;
    private bool _registered;
    private volatile bool _stopping;
    private Task? _startTask;
    private Task? _stopTask;

    public NewArticleNotifier(AccountManager accounts)
        : this(accounts, () => AppNotificationManager.Default.Register(), () => AppNotificationManager.Default.Unregister())
    {
    }

    internal NewArticleNotifier(AccountManager accounts, Action register, Action unregister)
    {
        _accounts = accounts;
        _register = register;
        _unregister = unregister;
    }

    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        return _startTask ??= StartCoreAsync();
    }

    private async Task StartCoreAsync()
    {
        try
        {
            // The package manifest supplies the COM identity; unpackaged runs register their own.
            await Task.Run(_register);
            _registered = true;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            // If registration fails (e.g. no AppId on a heavily sandboxed runtime),
            // skip notifications entirely. We must not crash the app over a missing
            // notification platform.
            _registered = false;
            App.LogException("Register article notifications", error);
            return;
        }

        if (_stopping) return;
        foreach (var account in _accounts.Accounts) Hook(account);
        _accounts.AccountsDidChange += OnAccountsChanged;
    }

    public Task StopAsync()
    {
        if (_stopTask is not null) return _stopTask;
        _stopping = true;
        _accounts.AccountsDidChange -= OnAccountsChanged;
        foreach (var account in _accounts.Accounts) Unhook(account);
        return _stopTask = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        if (_startTask is not null) await _startTask;
        if (_registered)
        {
            try { await Task.Run(_unregister); }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                App.LogException("Unregister article notifications", error);
            }
            _registered = false;
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void OnAccountsChanged(object? sender, EventArgs e)
    {
        if (_stopping) return;
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
        if (_stopping || sender is not Account.Account account || !_accounts.ActiveAccounts.Contains(account)) return;

        // Honor the app-wide toggle (Preferences > Notifications). When disabled,
        // every per-feed flag is treated as off so the user has a single switch.
        if (!WinNewsWire.Core.AppDefaults.Shared.NotificationsEnabled) return;

        // Build a feedID -> Feed lookup so we can consult per-feed settings without
        // re-flattening the account on every article.
        var feeds = account.FlattenedFeeds().ToDictionary(f => f.FeedID);

        foreach (var article in e.NewArticles)
        {
            if (_stopping) return;
            if (article.Status.Read) continue;
            if (!feeds.TryGetValue(article.FeedID, out var feed)) continue;
            if (!feed.NewArticleNotificationsEnabled) continue;

            try { Send(feed, article); }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
                App.LogException("Send article notification", error);
            }
        }
    }

    private async void OnStatusesChanged(object? sender, StatusesChangedEventArgs e)
    {
        if (_stopping || !_registered || sender is not Account.Account account || e.Key != ArticleStatus.Key.Read || !e.Flag) return;
        try
        {
            foreach (var id in e.ArticleIDs)
            {
                if (_stopping) return;
                await AppNotificationManager.Default.RemoveByTagAsync(ArticleNotificationIdentity.Tag(account.AccountID, id));
            }
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            App.LogException("Dismiss read article notifications", error);
        }
    }

    private void Send(Feed feed, Article article)
    {
        var title = feed.NameForDisplay;
        var subtitle = ArticleStringFormatter.Shared.TruncatedTitle(article);
        var body = ArticleStringFormatter.Shared.TruncatedSummary(article);
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", title));
        if (!string.IsNullOrEmpty(subtitle)) binding.Add(new XElement("text", subtitle));
        if (!string.IsNullOrEmpty(body)) binding.Add(new XElement("text", body));
        var payload = new XElement("toast",
            new XAttribute("activationType", "protocol"),
            new XAttribute("launch", ArticleNotificationIdentity.ActivationUri(article.AccountID, article.ArticleID).AbsoluteUri),
            new XElement("visual", binding));
        var notification = new AppNotification(payload.ToString(SaveOptions.DisableFormatting))
        {
            Tag = ArticleNotificationIdentity.Tag(article.AccountID, article.ArticleID),
            Group = "WinNewsWire.NewArticle"
        };
        AppNotificationManager.Default.Show(notification);
    }
}
