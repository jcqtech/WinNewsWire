#if PARITY_PROBE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppShared.Sidebar;
using WinNewsWire.Core;
using WinNewsWire.Models;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckSidebarDraggingAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var defaults = AppDefaults.Shared;
        bool hadOrder = defaults.ContainsKey(SidebarFeedOrder.PreferenceKey);
        var previousOrder = defaults.GetValue<JsonElement>(SidebarFeedOrder.PreferenceKey);
        var account = AccountManager.Shared.DefaultAccount;
        var feeds = new[]
        {
            account.AddFeed(serverUrl + "/drag-a", "Drag Alpha"),
            account.AddFeed(serverUrl + "/drag-b", "Drag Bravo"),
            account.AddFeed(serverUrl + "/drag-c", "Drag Charlie")
        };
        var folder = account.AddFolder("Drag destination");
        MainWindow? other = null;
        try
        {
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            model.IsSidebarReadFiltered = false;
            model.BuildSidebar();
            await model.ProbeDrainViewUpdatesAsync();
            var list = SidebarList(content);
            SidebarItem Row(Feed feed, Folder? parent = null) => list.Items.OfType<SidebarItem>()
                .Single(item => ReferenceEquals(item.Tag, feed) && ReferenceEquals(item.ParentFolder, parent));
            SidebarItem FolderRow() => list.Items.OfType<SidebarItem>().Single(item => ReferenceEquals(item.Tag, folder));
            string[] DisplayOrder(MainContent owner, Folder? parent = null)
                => SidebarList(owner).Items.OfType<SidebarItem>()
                    .Where(item => item.Tag is Feed feed && feeds.Contains(feed) && ReferenceEquals(item.ParentFolder, parent))
                    .Select(item => ((Feed)item.Tag!).Url).ToArray();
            async Task Order(params Feed[] expected)
            {
                await WaitFor(() => Task.FromResult(DisplayOrder(content).SequenceEqual(expected.Select(feed => feed.Url))), "Displayed feed order");
            }
            await WaitFor(() => Task.FromResult(list.ContainerFromItem(FolderRow()) is ListViewItem { IsLoaded: true }), "Visible drag targets");
            var headings = list.Items.OfType<SidebarItem>().Where(item => item.ItemType == SidebarItemType.SectionHeader)
                .ToDictionary(item => item, item => list.ContainerFromItem(item));
            Require(headings.Keys.All(item => !item.IsSelectable), "A static heading remains selectable.");
            int starts = content.ProbeSidebarDragStarts;
            await DragSidebarRowAsync(window, Row(feeds[2]), Row(feeds[0]), after: false);
            await Order(feeds[2], feeds[0], feeds[1]);
            Require(headings.All(pair => ReferenceEquals(list.ContainerFromItem(pair.Key), pair.Value)),
                "Reordering feeds recreated a static heading.");
            var reopened = SidebarFeedOrder.Load(new AppDefaults(Path.Combine(AppConfig.DataDirectory, "AppDefaults.json")));
            Require(reopened.OrderedFeeds(account).Where(feeds.Contains).SequenceEqual(new[] { feeds[2], feeds[0], feeds[1] }),
                "Reordering did not persist the visible order.");

            await InvokeDisclosureAsync(content, FolderRow);
            await WaitFor(() => Task.FromResult(!FolderRow().IsExpanded), "Collapsed folder drop target");
            await DragSidebarRowAsync(window, Row(feeds[0]), FolderRow(), after: false);
            await WaitFor(() => Task.FromResult(folder.Feeds.Contains(feeds[0]) && !account.TopLevelFeeds.Contains(feeds[0])), "Native drop into folder");
            await WaitFor(() => Task.FromResult(list.Items.OfType<SidebarItem>().Any(item => ReferenceEquals(item.Tag, feeds[0])
                && ReferenceEquals(item.ParentFolder, folder))), "Moved feed revealed in folder");
            await DragSidebarRowAsync(window, Row(feeds[1]), Row(feeds[0], folder), after: false);
            await WaitFor(() => Task.FromResult(DisplayOrder(content, folder).SequenceEqual(new[] { feeds[1].Url, feeds[0].Url })), "Cross-container insertion");
            await model.UndoAsync();
            Require(account.TopLevelFeeds.Contains(feeds[1]) && !folder.Feeds.Contains(feeds[1]), "Undo did not restore the moved feed.");
            await model.UndoAsync();
            await Order(feeds[2], feeds[0], feeds[1]);
            Require(folder.Feeds.Count == 0, "Undo left a moved subscription in the folder.");
            await model.RedoAsync();
            await model.RedoAsync();
            await WaitFor(() => Task.FromResult(DisplayOrder(content, folder).SequenceEqual(new[] { feeds[1].Url, feeds[0].Url })), "Redo folder placement");

            other = App.OpenMainWindow();
            await WaitFor(() => Task.FromResult(other.ProbeContent.IsLoaded && other.ProbeContent.ProbeWebViewReady), "Ordered companion sidebar");
            await other.ProbeContent.ViewModel.ProbeDrainViewUpdatesAsync();
            Require(DisplayOrder(other.ProbeContent, folder).SequenceEqual(new[] { feeds[1].Url, feeds[0].Url }), "Another window did not load the saved order.");
            window.Activate();
            await DragSidebarRowAsync(window, Row(feeds[0], folder), Row(feeds[1], folder), after: false);
            await WaitFor(() => Task.FromResult(DisplayOrder(other.ProbeContent, folder).SequenceEqual(new[] { feeds[0].Url, feeds[1].Url })),
                "Order synchronized to another window");
            await DragSidebarRowAsync(window, Row(feeds[1], folder), Row(feeds[2]), after: true);
            await WaitFor(() => Task.FromResult(account.TopLevelFeeds.Contains(feeds[1]) && !folder.Feeds.Contains(feeds[1])), "Native drop out of folder");
            await Order(feeds[2], feeds[1]);
            Require(content.ProbeSidebarDragStarts - starts == 5 && content.ProbeSidebarDragCompletions == content.ProbeSidebarDragStarts
                && content.ProbeSidebarInsertionTargets > 0 && content.ProbeSidebarFolderTargets > 0,
                "Native drag events or insertion/folder feedback did not execute.");
            Require(((FrameworkElement)content.FindName("SidebarDropIndicator")).Visibility == Visibility.Collapsed,
                "The completed drag left an insertion marker behind.");
            await SaveWindowScreenshotAsync(window, "sidebar-drag-order");
            return new { NativeMouseDrags = 5, RootOrderPersisted = true, FolderMoves = true, BeforeAndAfterInsertion = true,
                UndoRedo = true, SharedWindowOrder = true, StableHeadings = true, DropFeedback = true };
        }
        finally
        {
            if (other is not null) await CloseWindowAsync(other);
            model.UndoManager.RemoveAll();
            foreach (var feed in feeds) account.RemoveFeed(feed);
            account.RemoveFolder(folder);
            if (hadOrder) defaults.SetValue(SidebarFeedOrder.PreferenceKey, previousOrder);
            else defaults.RemoveValue(SidebarFeedOrder.PreferenceKey);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task DragSidebarRowAsync(MainWindow window, SidebarItem source, SidebarItem target, bool after)
    {
        var list = SidebarList(window.ProbeContent);
        list.ScrollIntoView(source);
        list.ScrollIntoView(target);
        await WaitFor(() => Task.FromResult(list.ContainerFromItem(source) is ListViewItem { IsLoaded: true }
            && list.ContainerFromItem(target) is ListViewItem { IsLoaded: true }), "Realized native drag rows");
        var sourceRow = (ListViewItem)list.ContainerFromItem(source);
        var targetRow = (ListViewItem)list.ContainerFromItem(target);
        var handle = WindowNative.GetWindowHandle(window);
        window.Activate();
        SetForegroundWindow(handle);
        await WaitFor(() => Task.FromResult(GetForegroundWindow() == handle), "Owned drag foreground");
        await Task.Delay(150);
        var origin = new CapturePoint();
        Require(ClientToScreen(handle, ref origin), "The private drag window has no screen position.");
        CapturePoint Point(FrameworkElement row, double y)
        {
            var position = row.TransformToVisual((UIElement)window.Content).TransformPoint(new(row.ActualWidth * 0.6, y));
            return new() { X = origin.X + (int)Math.Round(position.X * list.XamlRoot.RasterizationScale),
                Y = origin.Y + (int)Math.Round(position.Y * list.XamlRoot.RasterizationScale) };
        }
        var start = Point(sourceRow, sourceRow.ActualHeight / 2);
        var end = Point(targetRow, target.IsFolder ? targetRow.ActualHeight / 2 : after ? targetRow.ActualHeight - 5 : 5);
        Require(GetCursorPos(out var original), "The original cursor position is unavailable.");
        int left = BrowserInputSystemMetric(76), top = BrowserInputSystemMetric(77);
        int width = BrowserInputSystemMetric(78), height = BrowserInputSystemMetric(79);
        Require(width > 1 && height > 1, "The input desktop has no usable bounds.");
        int completions = window.ProbeContent.ProbeSidebarDragCompletions;
        var last = start;
        try
        {
            await Task.Factory.StartNew(() =>
            {
                bool pressed = false;
                try
                {
                    Require(GetForegroundWindow() == handle && GetAncestor(WindowFromPoint(start), 2) == handle
                        && SetCursorPos(start.X, start.Y), "The private drag source is unavailable.");
                    Thread.Sleep(150);
                    Require(SendInput(1, [new() { Mouse = new() { Flags = 2 } }], Marshal.SizeOf<ScrollInput>()) == 1, "The drag press failed.");
                    pressed = true;
                    Thread.Sleep(200);
                    for (int step = 1; step <= 20; step++)
                    {
                        Require(GetForegroundWindow() == handle && GetCursorPos(out var current)
                            && Math.Abs(current.X - last.X) <= 1 && Math.Abs(current.Y - last.Y) <= 1,
                            "Input ownership changed; the sidebar drag stopped.");
                        last = new() { X = start.X + (end.X - start.X) * step / 20, Y = start.Y + (end.Y - start.Y) * step / 20 };
                        var move = new ScrollInput { Mouse = new()
                        {
                            X = (int)Math.Round((last.X - left) * 65535d / (width - 1)),
                            Y = (int)Math.Round((last.Y - top) * 65535d / (height - 1)),
                            Flags = 0xC001
                        } };
                        Require(SendInput(1, [move], Marshal.SizeOf<ScrollInput>()) == 1, "The native drag movement failed.");
                        Thread.Sleep(25);
                    }
                    Thread.Sleep(250);
                }
                finally
                {
                    bool released = !pressed || SendInput(1, [new() { Mouse = new() { Flags = 4 } }], Marshal.SizeOf<ScrollInput>()) == 1;
                    Require(released, "The native drag release failed.");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await WaitFor(() => Task.FromResult(window.ProbeContent.ProbeSidebarDragCompletions > completions), "Completed native sidebar drag");
        }
        finally
        {
            if (GetCursorPos(out var current) && Math.Abs(current.X - last.X) <= 1 && Math.Abs(current.Y - last.Y) <= 1)
                SetCursorPos(original.X, original.Y);
        }
    }
}
#endif
