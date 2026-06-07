using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNewsWire.AppRuntime;
using WinNewsWire.AppShared.ArticleExtractor;
using WinNewsWire.AppShared.ArticleRendering;
using WinNewsWire.AppShared.Favicons;
using WinNewsWire.AppShared.SmartFeeds;
using WinNewsWire.Articles;
using WinNewsWire.Models;
using AccountNs = WinNewsWire.Account;

namespace WinNewsWire.ViewModels;

/// <summary>
/// Primary view-model for <see cref="MainContent"/>. Replaces the legacy
/// <c>FeedStorageService</c>/<c>RssFeedService</c> pipeline with a live binding against
/// <see cref="AccountNs.AccountManager"/> so feeds added via Preferences (or any other account
/// type) show up in the sidebar, and reading-state changes flow through
/// <see cref="AccountNs.Account.MarkAsync"/> into SQLite + pending-sync queues.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    // Sidebar
    public ObservableCollection<SidebarItem> SidebarItems { get; } = new();

    // Article list
    [ObservableProperty]
    private ObservableCollection<FeedItem> _articleItems = new();

    [ObservableProperty]
    private FeedItem? _selectedArticle;

    [ObservableProperty]
    private SidebarItem? _selectedSidebarItem;

    [ObservableProperty]
    private string _articleListTitle = "All Articles";

    [ObservableProperty]
    private int _articleListUnreadCount;

    // Content pane
    [ObservableProperty]
    private string _articleHtml = string.Empty;

    // UI state
    [ObservableProperty]
    private bool _isSidebarVisible = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _sidebarStatus = "No feeds";

    [ObservableProperty]
    private string _timelineStatus = "No articles";

    [ObservableProperty]
    private string _detailStatus = "";

    [ObservableProperty]
    private bool _isUnifiedLayout = WinNewsWire.Core.AppDefaults.Shared.UnifiedLayout;

    partial void OnIsUnifiedLayoutChanged(bool value) => WinNewsWire.Core.AppDefaults.Shared.UnifiedLayout = value;

    public MainViewModel()
    {
        // Re-render the visible article whenever the article text-size preference
        // changes, so the new size takes effect immediately without requiring the
        // user to navigate away and back.
        WinNewsWire.Core.AppDefaults.Shared.Changed += (_, key) =>
        {
            if (key == WinNewsWire.Core.AppDefaults.Key.ArticleTextSize && SelectedArticle is { } a)
                ArticleHtml = _readerModeExtracted is { } extracted
                    ? BuildReaderHtml(a, extracted)
                    : BuildArticleHtml(a);
        };
    }

    /// <summary>Body font-size in px for each preference index. Index 0..3 maps to
    /// Small / Medium / Large / Very Large; everything else falls back to Medium.</summary>
    private static int ArticleBodyFontSizePx() =>
        WinNewsWire.Core.AppDefaults.Shared.ArticleTextSizeRaw switch
        {
            0 => 14,
            1 => 16,
            2 => 19,
            3 => 23,
            _ => 16,
        };

    [RelayCommand]
    private void ToggleUnifiedLayout() => IsUnifiedLayout = !IsUnifiedLayout;

    partial void OnSelectedArticleChanged(FeedItem? value)
    {
        if (value is null) { ArticleHtml = string.Empty; _readerModeExtracted = null; UpdateSectionStatus(); return; }
        if (!value.IsRead)
        {
            value.IsRead = true;
            AdjustFeedUnreadCount(value, delta: -1);
            _ = MarkArticleInStoreAsync(value, ArticleStatus.Key.Read, true);
            UpdateSidebarUnreadCounts();
        }
        _readerModeExtracted = null;
        ArticleHtml = BuildArticleHtml(value);
        // Per-feed "Always Use Reader View" matches the global default in priority —
        // either trumps the existing in-session toggle and forces reader mode on.
        var feedReaderAlways = AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == value.AccountID)?
            .FlattenedFeeds().FirstOrDefault(f => f.FeedID == value.FeedId)?.ReaderViewAlwaysEnabled
            ?? false;
        if (IsReaderMode || WinNewsWire.Core.AppDefaults.Shared.ReaderModeDefault || feedReaderAlways)
        {
            IsReaderMode = true;
            _ = RunReaderModeAsync(value);
        }
        UpdateSectionStatus();
    }

    [ObservableProperty]
    private bool _isReaderMode;

    private ExtractedArticle? _readerModeExtracted;

    partial void OnIsReaderModeChanged(bool value)
    {
        if (SelectedArticle is null) { UpdateSectionStatus(); return; }
        if (value) { _ = RunReaderModeAsync(SelectedArticle); }
        else { _readerModeExtracted = null; ArticleHtml = BuildArticleHtml(SelectedArticle); }
        UpdateSectionStatus();
    }

    [RelayCommand]
    private void ToggleReaderMode() => IsReaderMode = !IsReaderMode;

    /// <summary>Re-render the currently selected article into <see cref="ArticleHtml"/> —
    /// invoked by the Article Theme submenu after the user picks a new <c>.nnwtheme</c> so
    /// the WebView swaps over immediately without requiring a new article selection.</summary>
    public void RefreshArticleHtml()
    {
        if (SelectedArticle is null) return;
        ArticleHtml = IsReaderMode && _readerModeExtracted is not null
            ? BuildReaderHtml(SelectedArticle, _readerModeExtracted)
            : BuildArticleHtml(SelectedArticle);
    }

    private async Task RunReaderModeAsync(FeedItem item)
    {
        try
        {
            StatusMessage = "Extracting reader view…";
            var account = AppService.Shared.Accounts.Accounts
                .FirstOrDefault(a => a.AccountID == item.AccountID);
            if (account is null) return;
            var articles = await account.Database.FetchArticlesAsync(item.FeedId);
            var article = articles.FirstOrDefault(a => a.ArticleID == item.Id);
            if (article is null) return;
            var extracted = await SmartReaderArticleExtractor.Shared.ExtractAsync(article);
            if (extracted is null) { StatusMessage = "Reader view unavailable."; return; }
            _readerModeExtracted = extracted;
            if (SelectedArticle == item) ArticleHtml = BuildReaderHtml(item, extracted);
            StatusMessage = "Reader view ready.";
        }
        catch (Exception ex) { StatusMessage = $"Reader error: {ex.Message}"; }
    }

    private static string BuildReaderHtml(FeedItem item, ExtractedArticle extracted)
    {
        var title = WebUtility.HtmlEncode(extracted.Title ?? item.Title);
        var author = WebUtility.HtmlEncode(extracted.Author ?? item.Author ?? string.Empty);
        var when = extracted.DatePublished ?? item.PublishedDate.ToString("MMMM d, yyyy");
        var link = WebUtility.HtmlEncode(extracted.Url ?? item.Link);
        // Apply per-site mojibake fixes to the reader-mode body too. The
        // extractor pulls bytes straight from the publisher so the same
        // garbled-quote cleanup the WebView pass uses is appropriate here.
        var body = WinNewsWire.AppShared.ArticleRendering.ArticleRenderingSpecialCases
            .FilterHtmlIfNeeded(extracted.Url ?? item.Link, extracted.Content ?? string.Empty);
        var baseHref = ResolveBaseHref(item, extracted.Url);
        var baseTag = string.IsNullOrEmpty(baseHref)
            ? string.Empty
            : $"<base href='{WebUtility.HtmlEncode(baseHref)}' />";
        var bodyPx = ArticleBodyFontSizePx();
        return $@"<!DOCTYPE html><html><head><meta charset='utf-8'/>
{baseTag}
<meta name='viewport' content='width=device-width, initial-scale=1'/>
<style>
:root {{ color-scheme: light dark; }}
body {{ font-family: 'Segoe UI Variable','Segoe UI',serif; font-size:{bodyPx}px; line-height:1.7;
       max-width:720px; margin:0 auto; padding:24px 32px; }}
h1 {{ font-size:2em; font-weight:700; line-height:1.25; margin:0.4em 0 0.5em; }}
.meta {{ color:#888; font-size:0.875em; margin-bottom:1.6em; }}
img {{ max-width:100%; height:auto; border-radius:8px; margin:1em 0; }}
a {{ color:#0066cc; }}
@media (prefers-color-scheme: dark) {{ body {{ color:#e0e0e0; }} a {{ color:#6cb6ff; }} }}
</style></head><body>
<h1>{title}</h1>
<div class='meta'>{author}{(string.IsNullOrEmpty(author) || string.IsNullOrEmpty(when) ? "" : " &middot; ")}{WebUtility.HtmlEncode(when)}{(string.IsNullOrEmpty(link) ? "" : $" &middot; <a href='{link}'>Open in browser</a>")}</div>
{body}
</body></html>";
    }

    partial void OnSelectedSidebarItemChanged(SidebarItem? value)
    {
        // Switching sections clears any active search so the user lands on a
        // fresh, unfiltered article list. The setter triggers
        // OnSearchQueryChanged → FilterArticlesAsync, so we don't need to call
        // FilterArticlesAsync ourselves when the query actually changes.
        if (!string.IsNullOrEmpty(SearchQuery))
            SearchQuery = string.Empty;
        else
            _ = FilterArticlesAsync();
    }
    partial void OnSearchQueryChanged(string value) => _ = FilterArticlesAsync();

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private async Task ReloadFeedsAsync()
    {
        IsLoading = true;
        StatusMessage = "Refreshing feeds…";
        try
        {
            await AppService.Shared.Accounts.RefreshAllAsync();
            BuildSidebar();
            await FilterArticlesAsync();
            StatusMessage = $"Refreshed {AppService.Shared.Accounts.ActiveAccounts.Count()} account(s)";
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task AddFeedAsync(AddFeedRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Url)) return;
        var account = request.Account ?? AppService.Shared.Accounts.DefaultAccount;
        if (account is null) { StatusMessage = "No account available."; return; }
        IsLoading = true;
        StatusMessage = $"Adding feed: {request.Url}";
        try
        {
            var folder = request.FolderName is { Length: > 0 } fn
                ? account.Folders.FirstOrDefault(f => string.Equals(f.Name, fn, StringComparison.OrdinalIgnoreCase))
                  ?? account.AddFolder(fn)
                : null;
            var feed = await account.CreateFeedAsync(request.Url, request.Name, folder);
            BuildSidebar();
            if (feed is not null)
            {
                // Fetch articles + feed metadata (HomePageUrl drives favicon lookup)
                // immediately so the user doesn't have to hit Refresh.
                StatusMessage = $"Fetching: {feed.NameForDisplay}";
                try { await account.RefreshAllAsync(); } catch { /* best-effort initial fetch */ }
                BuildSidebar();
                await FilterArticlesAsync();
                StatusMessage = $"Added: {feed.NameForDisplay}";
            }
            else
            {
                StatusMessage = "Could not add feed.";
            }
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ToggleStarredAsync()
    {
        if (SelectedArticle is null) return;
        SelectedArticle.IsStarred = !SelectedArticle.IsStarred;
        await MarkArticleInStoreAsync(SelectedArticle, ArticleStatus.Key.Starred, SelectedArticle.IsStarred);
        ArticleHtml = BuildArticleHtml(SelectedArticle);
    }

    [RelayCommand]
    private async Task MarkSelectedReadAsync()
    {
        if (SelectedArticle is null) return;
        var newRead = !SelectedArticle.IsRead;
        SelectedArticle.IsRead = newRead;
        AdjustFeedUnreadCount(SelectedArticle, delta: newRead ? -1 : +1);
        await MarkArticleInStoreAsync(SelectedArticle, ArticleStatus.Key.Read, newRead);
        UpdateSidebarUnreadCounts();
    }

    [RelayCommand]
    private async Task MarkAllReadAsync()
    {
        var byAccount = ArticleItems
            .Where(i => !i.IsRead)
            .GroupBy(i => i.AccountID);
        var unreadByFeed = ArticleItems
            .Where(i => !i.IsRead)
            .GroupBy(i => (i.AccountID, i.FeedId))
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var group in byAccount)
        {
            var account = AppService.Shared.Accounts.Accounts
                .FirstOrDefault(a => a.AccountID == group.Key);
            if (account is null) continue;
            await account.MarkAsync(group.Select(g => g.Id), ArticleStatus.Key.Read, true);
        }
        foreach (var item in ArticleItems) item.IsRead = true;
        // Drop each feed's unread count by exactly the number of items we just
        // flipped — keeps the sidebar in sync without a DB roundtrip.
        foreach (var ((accountId, feedId), count) in unreadByFeed)
        {
            var feed = AppService.Shared.Accounts.Accounts
                .FirstOrDefault(a => a.AccountID == accountId)?
                .FlattenedFeeds().FirstOrDefault(f => f.FeedID == feedId);
            if (feed is not null)
            {
                feed.UnreadCount = Math.Max(0, feed.UnreadCount - count);
                feed.OnUnreadCountChanged();
            }
        }
        UpdateSidebarUnreadCounts();
        ArticleListUnreadCount = 0;
    }

    [RelayCommand]
    private void NextArticle()
    {
        if (ArticleItems.Count == 0) return;
        var idx = SelectedArticle is null ? -1 : ArticleItems.IndexOf(SelectedArticle);
        if (idx < ArticleItems.Count - 1) SelectedArticle = ArticleItems[idx + 1];
    }

    [RelayCommand]
    private void PreviousArticle()
    {
        if (ArticleItems.Count == 0) return;
        var idx = SelectedArticle is null ? ArticleItems.Count : ArticleItems.IndexOf(SelectedArticle);
        if (idx > 0) SelectedArticle = ArticleItems[idx - 1];
    }

    // --- Initialization ---

    public async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading…";
        try
        {
            BuildSidebar();
            await FilterArticlesAsync();
            StatusMessage = $"Loaded {AppService.Shared.Accounts.ActiveAccounts.Count()} account(s)";
            // Background refresh of any stale feeds. Marshal the sidebar rebuild back to
            // the UI thread because SidebarItems is an ObservableCollection bound to UI.
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = Task.Run(async () => {
                try
                {
                    await AppService.Shared.Accounts.RefreshAllAsync();
                    if (dispatcher is not null)
                        dispatcher.TryEnqueue(BuildSidebar);
                    else
                        BuildSidebar();
                }
                catch { /* errors surface via ErrorLog */ }
            });
        }
        catch (Exception ex) { StatusMessage = $"Error: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    /// <summary>Builds the sidebar tree from <see cref="AccountNs.AccountManager"/>:
    /// Smart Feeds at the top, followed by one section per active account, each with its
    /// folders and flat feeds.</summary>
    public void BuildSidebar()
    {
        SidebarItems.Clear();

        // --- Smart Feeds section ---
        var smartHeader = new SidebarItem
        {
            Title = "Smart Feeds",
            ItemType = SidebarItemType.SectionHeader,
            IsExpanded = true,
        };
        foreach (var sf in SmartFeedsController.Shared.SmartFeeds)
        {
            smartHeader.Children.Add(new SidebarItem
            {
                Title = sf.NameForDisplay,
                Icon = sf.NameForDisplay switch
                {
                    "Today" => "\uE787",
                    "All Unread" => "\uE73C",
                    "Starred" => "\uE734",
                    _ => "\uE7C3",
                },
                ItemType = SidebarItemType.SmartFeed,
                Tag = sf,
                UnreadCount = sf.UnreadCount,
            });
        }
        SidebarItems.Add(smartHeader);

        // --- Per-account sections ---
        foreach (var account in AppService.Shared.Accounts.ActiveAccounts)
        {
            var accountHeader = new SidebarItem
            {
                Title = account.NameForDisplay,
                ItemType = SidebarItemType.SectionHeader,
                IsExpanded = true,
                Tag = account,
            };

            // Top-level feeds (no folder).
            foreach (var feed in account.TopLevelFeeds.OrderBy(f => f.NameForDisplay, StringComparer.OrdinalIgnoreCase))
            {
                accountHeader.Children.Add(FeedSidebarItem(feed));
            }

            // Folders.
            foreach (var folder in account.Folders.OrderBy(f => f.NameForDisplay, StringComparer.OrdinalIgnoreCase))
            {
                var folderItem = new SidebarItem
                {
                    Title = folder.NameForDisplay,
                    Icon = "\uE8B7",
                    ItemType = SidebarItemType.Folder,
                    IsExpanded = true,
                    Tag = folder,
                };
                foreach (var feed in folder.Feeds.OrderBy(f => f.NameForDisplay, StringComparer.OrdinalIgnoreCase))
                    folderItem.Children.Add(FeedSidebarItem(feed));
                folderItem.UnreadCount = folderItem.Children.Sum(c => c.UnreadCount);
                accountHeader.Children.Add(folderItem);
            }

            accountHeader.UnreadCount = accountHeader.Children.Sum(c => c.UnreadCount);
            SidebarItems.Add(accountHeader);
        }

        UpdateSidebarUnreadCounts();
        _ = PopulateSidebarFaviconsAsync();
        SidebarRebuilt?.Invoke();
    }

    /// <summary>Raised at the end of <see cref="BuildSidebar"/> so the view can re-mirror
    /// <see cref="SidebarItems"/> into its TreeView. Without this, a sidebar rebuild
    /// triggered from a background task (e.g. the post-launch refresh in
    /// <see cref="InitializeAsync"/>) would replace SidebarItem instances behind the
    /// TreeView's back, leaving its RootNodes pointing at stale items so per-feed
    /// unread-count updates never reach the UI until something else forces the view
    /// to re-bind.</summary>
    public event Action? SidebarRebuilt;

    private async Task PopulateSidebarFaviconsAsync()
    {
        try
        {
            foreach (var section in SidebarItems.ToList())
                await PopulateFaviconsForSectionAsync(section);
        }
        catch { /* favicons are best-effort */ }
    }

    private static async Task PopulateFaviconsForSectionAsync(SidebarItem item)
    {
        if (item.Tag is AccountNs.Feed feed)
        {
            try
            {
                var path = await FaviconDownloader.Shared.FaviconPathAsync(feed);
                if (!string.IsNullOrEmpty(path)) item.FaviconPath = path;
            }
            catch { }
        }
        foreach (var child in item.Children.ToList())
            await PopulateFaviconsForSectionAsync(child);
    }

    private static SidebarItem FeedSidebarItem(AccountNs.Feed feed) => new()
    {
        Title = feed.NameForDisplay,
        Icon = "\uE774",
        ItemType = SidebarItemType.Feed,
        Tag = feed,
        UnreadCount = feed.UnreadCount,
    };

    /// <summary>Load articles matching the current sidebar selection + search query.</summary>
    public async Task FilterArticlesAsync()
    {
        var selected = SelectedSidebarItem;
        HashSet<Article> results = new();
        string title = "All Articles";

        if (selected is { Tag: IPseudoFeed smart })
        {
            foreach (var a in await smart.FetchArticlesAsync()) results.Add(a);
            title = smart.NameForDisplay;
        }
        else if (selected is { Tag: AccountNs.Feed feed })
        {
            var account = AppService.Shared.Accounts.Accounts.FirstOrDefault(a => a.AccountID == feed.AccountID);
            if (account is not null)
                foreach (var a in await account.Database.FetchArticlesAsync(feed.FeedID)) results.Add(a);
            title = feed.NameForDisplay;
        }
        else if (selected is { Tag: AccountNs.Folder folder })
        {
            var account = AppService.Shared.Accounts.Accounts.FirstOrDefault(a => a.AccountID == folder.AccountID);
            if (account is not null)
            {
                var feedIds = folder.Feeds.Select(f => f.FeedID);
                foreach (var a in await account.Database.FetchArticlesAsync(feedIds)) results.Add(a);
            }
            title = folder.NameForDisplay;
        }
        else if (selected is { Tag: AccountNs.Account accountForHeader })
        {
            var feedIds = accountForHeader.FlattenedFeeds().Select(f => f.FeedID);
            foreach (var a in await accountForHeader.Database.FetchArticlesAsync(feedIds)) results.Add(a);
            title = accountForHeader.NameForDisplay;
        }
        else
        {
            // Default: all unread across all active accounts.
            foreach (var account in AppService.Shared.Accounts.ActiveAccounts)
                foreach (var a in await account.FetchUnreadAsync()) results.Add(a);
            title = "All Unread";
        }

        IEnumerable<Article> articles = results;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            articles = articles.Where(a =>
                (a.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (a.Summary?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var ordered = articles.OrderByDescending(a => a.DatePublished ?? DateTime.MinValue).ToList();
        var feedMap = AppService.Shared.Accounts.Accounts.SelectMany(a => a.FlattenedFeeds())
            .GroupBy(f => (f.AccountID, f.FeedID)).ToDictionary(g => g.Key, g => g.First());

        var items = ordered.Select(a => MapArticleToFeedItem(a, feedMap)).ToList();
        ArticleItems = new ObservableCollection<FeedItem>(items);
        ArticleListTitle = string.IsNullOrWhiteSpace(SearchQuery)
            ? title
            : $"{title} — Search Results";
        ArticleListUnreadCount = items.Count(i => !i.IsRead);
        UpdateSectionStatus();
        _ = PopulateArticleFaviconsAsync(items, feedMap);
    }

    private static async Task PopulateArticleFaviconsAsync(
        IReadOnlyList<FeedItem> items,
        Dictionary<(string, string), AccountNs.Feed> feedMap)
    {
        try
        {
            var cache = new Dictionary<(string, string), string?>();
            foreach (var item in items)
            {
                var key = (item.AccountID, item.FeedId);
                if (!cache.TryGetValue(key, out var path))
                {
                    if (feedMap.TryGetValue(key, out var feed))
                        path = await FaviconDownloader.Shared.FaviconPathAsync(feed);
                    cache[key] = path;
                }
                if (!string.IsNullOrEmpty(path)) item.FaviconPath = path;
            }
        }
        catch { /* favicons are best-effort */ }
    }

    private static FeedItem MapArticleToFeedItem(Article a, Dictionary<(string, string), AccountNs.Feed> feedMap)
    {
        var feed = feedMap.TryGetValue((a.AccountID, a.FeedID), out var f) ? f : null;
        return new FeedItem
        {
            Id = a.ArticleID,
            AccountID = a.AccountID,
            FeedId = a.FeedID,
            Title = a.Title ?? "",
            Summary = a.Summary ?? a.ContentHtml ?? a.ContentText ?? "",
            Content = a.ContentHtml ?? a.ContentText ?? "",
            Link = a.RawLink ?? a.RawExternalLink ?? "",
            ExternalLink = a.RawExternalLink ?? "",
            Author = "",
            PublishedDate = a.DatePublished is { } d ? new DateTimeOffset(d, TimeSpan.Zero) : DateTimeOffset.MinValue,
            IsRead = a.Status.Read,
            IsStarred = a.Status.Starred,
            ImageUrl = a.RawImageLink ?? "",
            FeedTitle = feed?.NameForDisplay ?? "",
            FeedIconUrl = feed?.FaviconUrl ?? feed?.IconUrl ?? "",
        };
    }

    private static string? StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var s = System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", " ");
        s = WebUtility.HtmlDecode(s);
        s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
        return s.Length > 280 ? s[..280] + "…" : s;
    }

    private async Task MarkArticleInStoreAsync(FeedItem item, ArticleStatus.Key key, bool value)
    {
        var account = AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == item.AccountID);
        if (account is null) return;
        try { await account.MarkAsync(new[] { item.Id }, key, value); } catch { /* swallow */ }
    }

    /// <summary>Bulk-mark a set of articles read/unread (or starred/unstarred) and keep
    /// the sidebar counts and visible list in sync. Mirrors NNW Mac's
    /// <c>MarkStatusCommand</c>-driven contextual menu actions.</summary>
    public async Task MarkArticlesAsync(IEnumerable<FeedItem> articles, ArticleStatus.Key key, bool flag)
    {
        var changed = articles.Where(a =>
            key == ArticleStatus.Key.Read ? a.IsRead != flag : a.IsStarred != flag).ToList();
        if (changed.Count == 0) return;

        foreach (var group in changed.GroupBy(a => a.AccountID))
        {
            var account = AppService.Shared.Accounts.Accounts
                .FirstOrDefault(a => a.AccountID == group.Key);
            if (account is null) continue;
            try { await account.MarkAsync(group.Select(a => a.Id), key, flag); } catch { }
        }

        foreach (var a in changed)
        {
            if (key == ArticleStatus.Key.Read)
            {
                AdjustFeedUnreadCount(a, delta: flag ? -1 : +1);
                a.IsRead = flag;
            }
            else
            {
                a.IsStarred = flag;
            }
        }
        if (key == ArticleStatus.Key.Read) UpdateSidebarUnreadCounts();
    }

    /// <summary>Mark every article above <paramref name="article"/> in the current
    /// timeline as read.</summary>
    public Task MarkAboveAsReadAsync(FeedItem article)
    {
        var idx = ArticleItems.IndexOf(article);
        if (idx <= 0) return Task.CompletedTask;
        var slice = ArticleItems.Take(idx).ToList();
        return MarkArticlesAsync(slice, ArticleStatus.Key.Read, true);
    }

    /// <summary>Mark every article below <paramref name="article"/> in the current
    /// timeline as read.</summary>
    public Task MarkBelowAsReadAsync(FeedItem article)
    {
        var idx = ArticleItems.IndexOf(article);
        if (idx < 0 || idx >= ArticleItems.Count - 1) return Task.CompletedTask;
        var slice = ArticleItems.Skip(idx + 1).ToList();
        return MarkArticlesAsync(slice, ArticleStatus.Key.Read, true);
    }

    /// <summary>Mark every unread article in a feed as read (used by the timeline
    /// contextual menu's "Mark All as Read in 'Feed Name'" command).</summary>
    public async Task MarkAllInFeedAsReadAsync(string accountId, string feedId)
    {
        var account = AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == accountId);
        if (account is null) return;
        var unread = await account.Database.FetchUnreadArticlesAsync(new[] { feedId });
        if (unread.Count == 0) return;
        var ids = unread.Select(a => a.ArticleID).ToList();
        try { await account.MarkAsync(ids, ArticleStatus.Key.Read, true); } catch { }

        // Reflect in current timeline view + sidebar
        foreach (var a in ArticleItems)
        {
            if (!a.IsRead && a.AccountID == accountId && a.FeedId == feedId)
            {
                AdjustFeedUnreadCount(a, delta: -1);
                a.IsRead = true;
            }
        }
        var feed = account.FlattenedFeeds().FirstOrDefault(f => f.FeedID == feedId);
        if (feed is not null)
        {
            feed.UnreadCount = 0;
            feed.OnUnreadCountChanged();
        }
        UpdateSidebarUnreadCounts();
    }

    /// <summary>Locate the sidebar item backed by <paramref name="feedId"/> within
    /// <paramref name="accountId"/> and select it. Returns the matching item if found.</summary>
    public SidebarItem? FindSidebarItemForFeed(string accountId, string feedId)
    {
        foreach (var section in SidebarItems)
        {
            foreach (var child in section.Children)
            {
                if (child.Tag is AccountNs.Feed f && f.AccountID == accountId && f.FeedID == feedId)
                    return child;
                foreach (var grand in child.Children)
                {
                    if (grand.Tag is AccountNs.Feed gf && gf.AccountID == accountId && gf.FeedID == feedId)
                        return grand;
                }
            }
        }
        return null;
    }

    /// <summary>Adjust the in-memory unread count on the article's owning Feed so the
    /// sidebar reflects the change instantly. <see cref="Account.RecalculateUnreadCountsAsync"/>
    /// otherwise only runs at refresh time, leaving sidebar counts stale until then.</summary>
    private static void AdjustFeedUnreadCount(FeedItem item, int delta)
    {
        if (delta == 0) return;
        var feed = AppService.Shared.Accounts.Accounts
            .FirstOrDefault(a => a.AccountID == item.AccountID)?
            .FlattenedFeeds().FirstOrDefault(f => f.FeedID == item.FeedId);
        if (feed is null) return;
        feed.UnreadCount = Math.Max(0, feed.UnreadCount + delta);
        feed.OnUnreadCountChanged();
    }

    /// <summary>After a mark-read/unread the sidebar counts are stale; recompute without
    /// rebuilding the tree so selection is preserved.</summary>
    public void UpdateSidebarUnreadCounts()
    {
        // Per-feed and per-folder counts come straight from in-memory Feed.UnreadCount,
        // which AdjustFeedUnreadCount keeps current. Smart-feed counts (Today / All
        // Unread / Starred) require a DB query so we kick off a refresh and update the
        // sidebar when it returns.
        foreach (var section in SidebarItems)
        {
            foreach (var child in section.Children)
            {
                switch (child.Tag)
                {
                    case AccountNs.Feed feed:
                        child.UnreadCount = feed.UnreadCount;
                        break;
                    case AccountNs.Folder folder:
                        child.UnreadCount = folder.Feeds.Sum(f => f.UnreadCount);
                        break;
                    case IPseudoFeed sf:
                        child.UnreadCount = sf.UnreadCount;
                        break;
                }
                // Update grandchildren (feeds inside folders).
                foreach (var grand in child.Children)
                {
                    if (grand.Tag is AccountNs.Feed f2)
                        grand.UnreadCount = f2.UnreadCount;
                }
            }

            // The account-header section itself ("On My PC", etc.) carries the
            // rolled-up unread count for its account. Recompute from current children.
            if (section.Tag is AccountNs.Account)
                section.UnreadCount = section.Children.Sum(c => c.UnreadCount);
        }

        ArticleListUnreadCount = ArticleItems.Count(i => !i.IsRead);
        UpdateSectionStatus();

        // Refresh smart feed counts off the DB and re-tally once they land.
        _ = RefreshSmartFeedCountsAsync();
    }

    private async Task RefreshSmartFeedCountsAsync()
    {
        try { await SmartFeedsController.Shared.RefreshAllAsync(); }
        catch { return; }
        foreach (var section in SidebarItems)
            foreach (var child in section.Children)
                if (child.Tag is IPseudoFeed sf)
                    child.UnreadCount = sf.UnreadCount;
        UpdateSectionStatus();
    }

    /// <summary>Refresh the three region-specific status strings shown at the bottom of
    /// each pane (sidebar/timeline/detail) — mirrors Mac NNW's per-pane status footer.</summary>
    public void UpdateSectionStatus()
    {
        int accounts = AppService.Shared.Accounts.ActiveAccounts.Count();
        int feeds = AppService.Shared.Accounts.ActiveAccounts.SelectMany(a => a.FlattenedFeeds()).Count();
        int totalUnread = SidebarItems.SelectMany(s => s.Children).Sum(c =>
            c.Tag is AccountNs.Feed f ? f.UnreadCount :
            c.Tag is AccountNs.Folder fo ? fo.Feeds.Sum(x => x.UnreadCount) : 0);
        SidebarStatus = $"{accounts} account{Plural(accounts)} · {feeds} feed{Plural(feeds)} · {totalUnread} unread";

        int total = ArticleItems.Count;
        int unread = ArticleItems.Count(i => !i.IsRead);
        int starred = ArticleItems.Count(i => i.IsStarred);
        TimelineStatus = total == 0 ? "No articles" : $"{total} article{Plural(total)} · {unread} unread · {starred} starred";

        if (SelectedArticle is null)
        {
            DetailStatus = "";
        }
        else
        {
            var d = SelectedArticle.PublishedDate.LocalDateTime.ToString("MMM d, yyyy h:mm tt");
            var parts = new List<string> { SelectedArticle.FeedTitle };
            if (!string.IsNullOrEmpty(SelectedArticle.Author)) parts.Add(SelectedArticle.Author);
            parts.Add(d);
            if (IsReaderMode) parts.Add("Reader view");
            DetailStatus = string.Join(" · ", parts);
        }

        static string Plural(int n) => n == 1 ? "" : "s";
    }

    // --- Article HTML rendering ---

    private static string BuildArticleHtml(FeedItem item)
    {
        var content = !string.IsNullOrEmpty(item.Content) ? item.Content : item.Summary;
        // Per-site mojibake cleanup (e.g. theverge.com). Wired through the
        // shared ArticleRenderingSpecialCases helper so both BuildArticleHtml
        // and BuildReaderHtml apply the same transforms as Mac NetNewsWire.
        if (!string.IsNullOrEmpty(content))
            content = WinNewsWire.AppShared.ArticleRendering.ArticleRenderingSpecialCases
                .FilterHtmlIfNeeded(item.Link, content);
        // If the feed supplies only a short summary (many mainstream feeds truncate
        // <description> to a teaser), fall back to any lead image so the user isn't
        // staring at a blank pane. Full content + images render via the WebView below.
        if (!string.IsNullOrEmpty(item.ImageUrl) &&
            (string.IsNullOrEmpty(content) ||
             !content.Contains("<img", StringComparison.OrdinalIgnoreCase)))
        {
            content = $"<p><img src='{WebUtility.HtmlEncode(item.ImageUrl)}' alt=''/></p>" + (content ?? string.Empty);
        }
        var baseHref = ResolveBaseHref(item);
        var baseTag = string.IsNullOrEmpty(baseHref)
            ? string.Empty
            : $"<base href='{WebUtility.HtmlEncode(baseHref)}' />";
        var bodyPx = ArticleBodyFontSizePx();
        return $@"<!DOCTYPE html>
<html><head><meta charset='utf-8' />
{baseTag}
<meta name='viewport' content='width=device-width, initial-scale=1' />
<style>
:root {{ color-scheme: light dark; }}
body {{ font-family: 'Segoe UI Variable','Segoe UI',sans-serif; font-size:{bodyPx}px; line-height:1.6; max-width:800px;
       margin:0 auto; padding:24px 32px; color:#1a1a1a; background:transparent; }}
@media (prefers-color-scheme: dark) {{ body {{ color:#e0e0e0; }} a {{ color:#6cb6ff; }} }}
.feed-info {{ font-size:0.8125em; color:#666; margin-bottom:4px; }}
h1 {{ font-size:1.75em; font-weight:700; line-height:1.3; margin:0.5em 0 0.4em; }}
.meta {{ font-size:0.8125em; color:#888; margin-bottom:1.5em; }}
img {{ max-width:100%; height:auto; border-radius:8px; margin:1em 0; }}
a {{ color:#0066cc; }}
blockquote {{ border-left:3px solid #ddd; margin:1em 0; padding:0.5em 1em; color:#555; }}
pre,code {{ font-family:'Cascadia Code','Consolas',monospace; font-size:0.875em;
            background:#f5f5f5; border-radius:4px; }}
@media (prefers-color-scheme: dark) {{ pre,code {{ background:#2a2a2a; }} }}
pre {{ padding:1em; overflow-x:auto; }} code {{ padding:0.125em 0.375em; }}
</style></head><body>
<div class='feed-info'>{WebUtility.HtmlEncode(item.FeedTitle)}{(string.IsNullOrEmpty(item.Author) ? "" : $" &middot; {WebUtility.HtmlEncode(item.Author)}")}</div>
<h1>{WebUtility.HtmlEncode(item.Title)}</h1>
<div class='meta'>{item.PublishedDate.LocalDateTime:MMMM d, yyyy 'at' h:mm tt}{(string.IsNullOrEmpty(item.Link) ? "" : $" &middot; <a href='{WebUtility.HtmlEncode(item.Link)}'>Open in browser</a>")}</div>
{content}
</body></html>";
    }

    /// <summary>Pick a base URL for relative &lt;img&gt; / &lt;a&gt; resolution in
    /// <see cref="BuildArticleHtml"/> and <see cref="BuildReaderHtml"/>. Without
    /// this, WebView2.NavigateToString leaves the document at about:blank so any
    /// relative image (e.g. "/img/foo.png" from Daring Fireball) fails to load.</summary>
    private static string ResolveBaseHref(FeedItem item, string? preferred = null)
    {
        foreach (var candidate in new[] { preferred, item.Link, item.FeedIconUrl })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            {
                return u.GetLeftPart(UriPartial.Path);
            }
        }
        return string.Empty;
    }
}

/// <summary>Payload for <see cref="MainViewModel.AddFeedCommand"/>.</summary>
public sealed class AddFeedRequest
{
    public string Url { get; init; } = "";
    public string? Name { get; init; }
    public string? FolderName { get; init; }
    public AccountNs.Account? Account { get; init; }
}
