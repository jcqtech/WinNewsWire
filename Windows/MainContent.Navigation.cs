using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WinNewsWire.AppShared.Timeline;
using WinNewsWire.Models;

namespace WinNewsWire;

public sealed partial class MainContent
{
    private readonly CancellationTokenSource _navigationCancellation = new();
    private Task _nextUnreadTask = Task.CompletedTask;

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
        ArticleListView.ScrollIntoView(article);
        ArticleListView.Focus(FocusState.Programmatic);
    }
}
