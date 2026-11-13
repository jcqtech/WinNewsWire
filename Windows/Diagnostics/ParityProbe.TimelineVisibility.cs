#if PARITY_PROBE
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Core;
using WinNewsWire.Models;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckTimelineVisibilityAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        bool oldLayout = AppDefaults.Shared.UseColumnLayout;
        var account = AccountManager.Shared.DefaultAccount;
        var feed = account.AddFeed(serverUrl + "/visibility-feed", "Timeline visibility");
        var now = DateTime.UtcNow;
        await account.Database.UpdateArticlesAsync(Enumerable.Range(0, 80).Select(index =>
        {
            string id = "visibility-" + index;
            return new Article(account.AccountID, id, feed.FeedID, id, "Navigation article " + index,
                "<p>A short navigation fixture.</p>", null, null, serverUrl + "/" + id, null, null, null,
                now.AddMinutes(-index), null, null, null, new ArticleStatus(id, true, false, now));
        }).ToArray());
        var list = (ListView)content.FindName("ArticleListView");
        Rect? Bounds(FeedItem article) => list.ContainerFromItem(article) is FrameworkElement { ActualHeight: > 0 } row
            ? row.TransformToVisual(list).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight)) : null;
        bool Visible(FeedItem article) => Bounds(article) is { } bounds
            && bounds.Top >= list.Padding.Top - 1
            && bounds.Bottom <= list.ActualHeight - list.Padding.Bottom + 1;
        async Task Settled(FeedItem article, bool centered)
        {
            try
            {
                await WaitFor(() => Task.FromResult(Visible(article) && (!centered || Bounds(article) is { } bounds
                    && Math.Abs((bounds.Top + bounds.Bottom - list.Padding.Top
                        - list.ActualHeight + list.Padding.Bottom) / 2) < 2)), "Fully visible timeline navigation");
            }
            catch (TimeoutException error)
            {
                var scroll = Descendants(list).OfType<ScrollViewer>().First();
                throw new InvalidOperationException($"Row {article.Id}: bounds={Bounds(article)}, header={list.Padding.Top}, "
                    + $"height={list.ActualHeight}, offset={scroll.VerticalOffset}, extent={scroll.ExtentHeight}, "
                    + $"column={model.UseColumnLayout}, centered={centered}.", error);
            }
        }
        try
        {
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with { Width = 1400, Height = 900 }));
            model.IsUnifiedLayout = false;
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.FilterArticlesAsync();
            if (model.IsReadFiltered) await model.ToggleReadFilterCommand.ExecuteAsync(null);
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            Require(model.ArticleItems.Count == 80, "Navigation fixtures were filtered out.");
            foreach (bool column in new[] { false, true })
            {
                AppDefaults.Shared.UseColumnLayout = column;
                await WaitFor(() => Task.FromResult(model.UseColumnLayout == column && list.ActualHeight > list.Padding.Top + 100),
                    "Timeline navigation layout");
                var middle = model.ArticleItems[30];
                model.SelectedArticle = middle;
                await Settled(middle, centered: true);
                var scroll = Descendants(list).OfType<ScrollViewer>().First();
                double hiddenTop = list.Padding.Top - 6;
                scroll.ChangeView(null, scroll.VerticalOffset + Bounds(middle)!.Value.Top - hiddenTop, null, disableAnimation: true);
                await WaitFor(() => Task.FromResult(Bounds(middle) is { } bounds && Math.Abs(bounds.Top - hiddenTop) < 2),
                    "Partly obscured timeline row");
                content.SelectPrev();
                var previous = model.ArticleItems[29];
                await Settled(previous, centered: false);
                Require(Bounds(previous)!.Value.Top >= list.Padding.Top - 1
                    && Bounds(previous)!.Value.Top < list.Padding.Top + 2,
                    "Upward navigation centered the selected row or scrolled twice instead of revealing it minimally.");

                var below = model.ArticleItems[60];
                model.SelectedArticle = below;
                await Settled(below, centered: true);
                double offset = scroll.VerticalOffset;
                content.SelectNext();
                await Settled(model.ArticleItems[61], centered: false);
                Require(Math.Abs(scroll.VerticalOffset - offset) < 1,
                    "A fully visible selected row and its following row caused unnecessary scrolling.");
                model.SelectedArticle = below;
                await Settled(below, centered: false);
                var belowBounds = Bounds(below)!.Value;
                double nearBottom = list.ActualHeight - list.Padding.Bottom - 2 * belowBounds.Height - 2;
                scroll.ChangeView(null, scroll.VerticalOffset + belowBounds.Top - nearBottom, null, disableAnimation: true);
                await WaitFor(() => Task.FromResult(Bounds(below) is { } bounds && Math.Abs(bounds.Top - nearBottom) < 2),
                    "Visible selection near the viewport bottom");
                content.SelectNext();
                await Settled(model.ArticleItems[62], centered: true);
                Require(ReferenceEquals(model.SelectedArticle, model.ArticleItems[61]) && Visible(model.ArticleItems[61]),
                    "Showing the following row changed or obscured the selected article.");
            }
            return new { PinnedHeaderExcluded = true, MinimumUpwardScroll = true,
                CenteredDownwardScroll = true, VirtualizedRows = true, FollowingRowContext = true, BothLayouts = true };
        }
        finally
        {
            AppDefaults.Shared.UseColumnLayout = oldLayout;
            account.RemoveFeed(feed);
            await account.RecalculateUnreadCountsAsync();
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
        }
    }
}
#endif
