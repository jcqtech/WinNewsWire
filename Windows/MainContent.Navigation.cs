using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.Models;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private readonly CancellationTokenSource _navigationCancellation = new();
    private Task _nextUnreadTask = Task.CompletedTask;
    private FeedItem? _pendingTimelineReveal;
    private FeedItem? _timelineRevealSelection;
    private bool _centerPendingTimelineReveal;
    private FeedItem? _timelineContextArticle;
    private int _timelineContextDirection;
    private int _pendingTimelineContextDirection;

    private void ScrollTimelineArticleIntoView(FeedItem article, int contextDirection = 0)
    {
        CancelTimelineReveal();
        _pendingTimelineReveal = article;
        _timelineRevealSelection = ViewModel.SelectedArticle;
        _pendingTimelineContextDirection = contextDirection;
        // Template changes finish arranging after selection notifications.
        CompositionTarget.Rendering += TimelineReveal_Rendering;
    }

    private bool TryRevealTimelineRow(FeedItem article, bool center = false)
    {
        if (_articleScrollViewer is not { } scroll || TimelineRowBounds(article) is not { } bounds
            || ArticleListView.ActualHeight <= ArticleListView.Padding.Top + ArticleListView.Padding.Bottom)
            return false;
        double delta = TimelineViewport.ScrollDelta(bounds.Top, bounds.Bottom,
            ArticleListView.Padding.Top, ArticleListView.ActualHeight - ArticleListView.Padding.Bottom, center);
        return delta == 0
            || scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + delta, 0, scroll.ScrollableHeight), null);
    }

    private void TimelineReveal_LayoutUpdated(object? sender, object args)
    {
        ArticleListView.LayoutUpdated -= TimelineReveal_LayoutUpdated;
        CompositionTarget.Rendering += TimelineReveal_Rendering;
    }

    private void TimelineReveal_Rendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= TimelineReveal_Rendering;
        if (_shuttingDown || !ReferenceEquals(_timelineRevealSelection, ViewModel.SelectedArticle)
            || _pendingTimelineReveal is not { } article)
        {
            CancelTimelineReveal();
            return;
        }
        bool visible = IsTimelineRowFullyVisible(article);
        if (visible && _pendingTimelineContextDirection != 0)
        {
            int neighbor = ViewModel.ArticleItems.IndexOf(article) + _pendingTimelineContextDirection;
            if (neighbor >= 0 && neighbor < ViewModel.ArticleItems.Count)
            {
                article = ViewModel.ArticleItems[neighbor];
                _pendingTimelineReveal = article;
                visible = IsTimelineRowFullyVisible(article);
            }
        }
        _pendingTimelineContextDirection = 0;
        if ((visible && !_centerPendingTimelineReveal) || TryRevealTimelineRow(article, _centerPendingTimelineReveal))
        {
            CancelTimelineReveal();
            return;
        }
        _centerPendingTimelineReveal = ArticleListView.ItemsPanelRoot is ItemsStackPanel panel
            && ViewModel.ArticleItems.IndexOf(article) > panel.FirstVisibleIndex;
        ArticleListView.LayoutUpdated += TimelineReveal_LayoutUpdated;
        ArticleListView.ScrollIntoView(article);
    }

    private void CancelTimelineReveal()
    {
        ArticleListView.LayoutUpdated -= TimelineReveal_LayoutUpdated;
        CompositionTarget.Rendering -= TimelineReveal_Rendering;
        _pendingTimelineReveal = null;
        _timelineRevealSelection = null;
        _centerPendingTimelineReveal = false;
        _pendingTimelineContextDirection = 0;
    }

    private void SelectAdjacentArticle(bool next)
    {
        var previous = ViewModel.SelectedArticle;
        _timelineContextArticle = null;
        if (next) ViewModel.NextArticleCommand.Execute(null);
        else ViewModel.PreviousArticleCommand.Execute(null);
        if (!ReferenceEquals(previous, ViewModel.SelectedArticle))
        {
            _timelineContextArticle = ViewModel.SelectedArticle;
            _timelineContextDirection = next ? 1 : -1;
        }
    }

    private void RevealTimelineSelection(FeedItem article)
    {
        int direction = ReferenceEquals(_timelineContextArticle, article) ? _timelineContextDirection : 0;
        _timelineContextArticle = null;
        ScrollTimelineArticleIntoView(article, direction);
    }

    private bool IsTimelineRowFullyVisible(FeedItem article)
        => TimelineRowBounds(article) is { } bounds
            && TimelineViewport.IsRowFullyVisible(bounds.Top, bounds.Bottom,
                ArticleListView.Padding.Top, ArticleListView.ActualHeight - ArticleListView.Padding.Bottom);

    private Windows.Foundation.Rect? TimelineRowBounds(FeedItem article)
        => ArticleListView.ContainerFromItem(article) is FrameworkElement { ActualHeight: > 0 } row
            ? row.TransformToVisual(ArticleListView).TransformBounds(
                new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight))
            : null;

    public bool CanSelectNextUnread => !_shuttingDown
        && (NextUnreadArticleRow(wrapping: true) is not null || NextUnreadSidebarRow() is not null);

    private int SelectedTimelineRow() => ViewModel.SelectedArticles.FirstOrDefault() is { } first
        ? ViewModel.ArticleItems.IndexOf(first) : -1;

    private int? NextUnreadArticleRow(bool wrapping)
        => ArticleArray.RowOfNextUnreadArticle(ViewModel.ArticleItems, SelectedTimelineRow(), wrapping, item => item.IsRead);

    private SidebarItem? NextUnreadSidebarRow()
    {
        var rows = _sidebarRows.ToArray();
        var selected = ViewModel.SelectedSidebarItems.Select(item => item.Identity).ToHashSet();
        int selectedRow = Array.FindIndex(rows, item => item.Identity is not null && selected.Contains(item.Identity));
        foreach (var index in ArticleArray.OrderedRowIndexes(rows.Length, selectedRow + 1, wrappingToTop: true))
        {
            var item = rows[index];
            if (item.Identity is not null && item.UnreadCount > 0 && item.ItemType != SidebarItemType.SectionHeader
                && item.Tag is not Account.Account && !(item.IsFolder && item.IsExpanded))
                return item;
        }
        return null;
    }

    public Task SelectNextUnreadAsync()
    {
        if (_shuttingDown) return Task.CompletedTask;
        return _nextUnreadTask = NavigateNextUnreadAsync(_nextUnreadTask);
    }

    private async Task NavigateNextUnreadAsync(Task previous)
    {
        await Task.Yield();
        var cancellationToken = _navigationCancellation.Token;
        try
        {
            await previous;
            cancellationToken.ThrowIfCancellationRequested();
            var scopes = ViewModel.SelectedSidebarItems.Select(item => item.Identity).ToArray();
            var articles = ViewModel.SelectedArticles.Select(ViewModels.MainViewModel.Identity).ToArray();
            bool SelectionUnchanged() => scopes.SequenceEqual(ViewModel.SelectedSidebarItems.Select(item => item.Identity))
                && articles.SequenceEqual(ViewModel.SelectedArticles.Select(ViewModels.MainViewModel.Identity));

            await ViewModel.PendingAutomaticReads.WaitAsync(cancellationToken);
            if (!SelectionUnchanged()) return;
            if (NextUnreadArticleRow(wrapping: false) is { } next)
            {
                SelectUnreadArticle(next);
                return;
            }

            await ViewModel.RefreshNavigationUnreadCountsAsync(cancellationToken);
            if (!SelectionUnchanged()) return;
            BuildSidebarUI();
            var destination = NextUnreadSidebarRow();
            if (destination is null) return;
            bool sameScope = scopes.Length == 1 && scopes[0] == destination.Identity;
            ViewModel.SetSelectedSidebarItems([destination]);
            var expectedArticles = ViewModel.SelectedArticles.Select(ViewModels.MainViewModel.Identity).ToArray();
            await ViewModel.FilterArticlesAsync();
            await ViewModel.WaitForCurrentTimelineAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ViewModel.SelectedSidebarItems.Count != 1 || ViewModel.SelectedSidebarItem?.Identity != destination.Identity
                || !expectedArticles.SequenceEqual(ViewModel.SelectedArticles.Select(ViewModels.MainViewModel.Identity)))
                return;

            int? row = sameScope ? NextUnreadArticleRow(wrapping: true)
                : ArticleArray.RowOfNextUnreadArticle(ViewModel.ArticleItems, -1, true, item => item.IsRead);
            if (row is { } unread) SelectUnreadArticle(unread);
        }
        catch (OperationCanceledException) when (_navigationCancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.IO.IOException
            or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or OperationCanceledException
            or System.Runtime.InteropServices.COMException)
        {
            App.LogException("Next unread", error);
            if (!_shuttingDown) ViewModel.StatusMessage = "Could not select the next unread article: " + error.Message;
        }
    }

    private void SelectUnreadArticle(int row)
    {
        if (_shuttingDown) return;
        var article = ViewModel.ArticleItems[row];
        ViewModel.SelectedArticle = article;
        ScrollTimelineArticleIntoView(article);
        ArticleListView.Focus(FocusState.Programmatic);
    }
}
