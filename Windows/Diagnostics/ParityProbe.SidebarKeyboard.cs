#if PARITY_PROBE
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinNewsWire.Account;
using WinNewsWire.Articles;
using WinNewsWire.Models;
using WinRT.Interop;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckSidebarDeletionIdentityAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var account = AccountManager.Shared.DefaultAccount;
        var first = account.AddFeed(serverUrl + "/identity-first", "A identity delete");
        var survivor = account.AddFeed(serverUrl + "/identity-survivor", "B identity survivor");
        var stale = account.AddFeed(serverUrl + "/identity-stale", "C identity stale");
        var folder = account.AddFolder("D identity folder");
        var child = account.AddFeed(serverUrl + "/identity-child", "Deleted folder child", folder);
        var nextFolder = account.AddFolder("E identity survivor folder");
        Feed? inserted = null;
        var sidebar = SidebarList(content);
        SidebarItem Row(object value) => sidebar.Items.OfType<SidebarItem>().Single(item => ReferenceEquals(item.Tag, value));
        ContentDialog? Dialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
        async Task Confirm(Task deletion)
        {
            InvokeDialogButton(Dialog() ?? throw new InvalidOperationException("The Delete confirmation disappeared."), "PrimaryButton");
            await deletion.WaitAsync(TimeSpan.FromSeconds(15));
        }
        try
        {
            model.BuildSidebar();
            model.SelectedSidebarItem = Row(first);
            var deletion = content.DeleteSelectedAsync();
            await WaitFor(() => Task.FromResult(Dialog()?.IsLoaded == true), "Identity Delete confirmation");
            inserted = account.AddFeed(serverUrl + "/identity-inserted", "0 inserted during confirmation");
            model.BuildSidebar();
            await Confirm(deletion);
            Require(!account.TopLevelFeeds.Contains(first) && account.TopLevelFeeds.Contains(inserted)
                && account.TopLevelFeeds.Contains(survivor), "Deleting after an insertion removed the wrong subscription.");
            Require(ReferenceEquals(model.SelectedSidebarItem?.Tag, survivor), "Deletion did not select the next surviving sidebar row.");
            model.SelectedSidebarItem = Row(folder);
            deletion = content.DeleteSelectedAsync();
            await WaitFor(() => Task.FromResult(Dialog()?.IsLoaded == true), "Folder identity Delete confirmation");
            await Confirm(deletion);
            Require(!account.Folders.Contains(folder) && ReferenceEquals(model.SelectedSidebarItem?.Tag, nextFolder),
                "Deleting a folder selected its removed child instead of the next surviving row.");
            await model.UndoAsync();
            Require(account.Folders.Contains(folder) && folder.Feeds.Contains(child), "Undo did not restore the deleted folder and child.");
            model.UndoManager.RemoveAll();
            model.SelectedSidebarItem = Row(stale);
            deletion = content.DeleteSelectedAsync();
            await WaitFor(() => Task.FromResult(Dialog()?.IsLoaded == true), "Stale identity Delete confirmation");
            account.RemoveFeed(stale);
            model.BuildSidebar();
            await Confirm(deletion);
            Require(!model.CanUndo && account.TopLevelFeeds.Contains(survivor),
                "A vanished confirmed row created a stale deletion command.");
            return new { InsertedRowPreserved = true, SurvivorSelected = true, FolderChildrenSkipped = true, StaleConfirmation = true, Undo = true };
        }
        finally
        {
            Dialog()?.Hide();
            model.UndoManager.RemoveAll();
            foreach (var feed in new[] { first, survivor, stale, child, inserted }.OfType<Feed>()) account.RemoveFeed(feed);
            account.RemoveFolder(folder);
            account.RemoveFolder(nextFolder);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckSidebarDeleteKeyAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var account = AccountManager.Shared.DefaultAccount;
        var first = account.AddFeed(serverUrl + "/delete-key-first", "Delete first");
        var second = account.AddFeed(serverUrl + "/delete-key-second", "Delete second");
        var folder = account.AddFolder("Delete folder");
        var child = account.AddFeed(serverUrl + "/delete-key-child", "Delete child", folder);
        var status = new ArticleStatus("delete-key-article", true, false, DateTime.UtcNow);
        await account.Database.UpdateArticlesAsync([new Article(account.AccountID, status.ArticleID, first.FeedID, status.ArticleID,
            "Delete focus fixture", "<p>Reading this article must never let Delete remove its feed.</p>",
            null, null, serverUrl + "/delete-key-page", null, null, null, DateTime.UtcNow, null, null, null, status)]);
        ContentDialog? Dialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot)
            .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
        bool Subscribed() => account.TopLevelFeeds.Contains(first) && account.TopLevelFeeds.Contains(second)
            && account.Folders.Contains(folder) && folder.Feeds.Contains(child);
        var sidebar = SidebarList(content);
        SidebarItem Row(object value) => sidebar.Items.OfType<SidebarItem>().Single(item => ReferenceEquals(item.Tag, value));
        async Task Focus(FrameworkElement control)
        {
            window.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(window));
            await WaitFor(() => Task.FromResult(GetForegroundWindow() == WindowNative.GetWindowHandle(window)), "Owned Delete input");
            bool focused = control switch
            {
                Control target => target.Focus(FocusState.Programmatic),
                WebView2 browser => browser.Focus(FocusState.Programmatic),
                _ => throw new InvalidOperationException("Unsupported Delete input target: " + control.GetType().Name)
            };
            Require(focused, $"The Delete target could not receive focus: {control.GetType().Name} {control.Name}, "
                + $"loaded={control.IsLoaded}, visible={control.Visibility}, size={control.ActualWidth}x{control.ActualHeight}.");
            await WaitFor(() => Task.FromResult(Descendants(control).Contains(FocusManager.GetFocusedElement(content.XamlRoot) as DependencyObject)),
                "Delete target focus");
        }
        async Task SidebarFocus(object item)
        {
            var row = Row(item);
            sidebar.ScrollIntoView(row);
            await WaitFor(() => Task.FromResult(sidebar.ContainerFromItem(row) is ListViewItem { IsLoaded: true }), "Delete row realization");
            await Focus((ListViewItem)sidebar.ContainerFromItem(row));
        }
        async Task NoDeletion(string scope)
        {
            await Task.Delay(150);
            Require(Dialog() is null && Subscribed(), "Delete escaped its sidebar focus scope: " + scope);
        }
        async Task Confirm(bool cancel)
        {
            await WaitFor(() => Task.FromResult(Dialog()?.IsLoaded == true), "Delete key confirmation");
            Require(Dialog()!.DefaultButton == ContentDialogButton.Close, "Delete did not retain the safe Cancel default.");
            Require(VisualTreeHelper.GetOpenPopupsForXamlRoot(content.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().Distinct().Count() == 1,
                "Repeated Delete opened multiple confirmation dialogs.");
            InvokeDialogButton(Dialog()!, cancel ? "CloseButton" : "PrimaryButton");
            await WaitFor(() => Task.FromResult(Dialog() is null && !content.ProbeSidebarDeleteInProgress), "Delete confirmation completed");
        }
        try
        {
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with
                { Width = (int)Math.Ceiling(1350 * content.XamlRoot.RasterizationScale) }));
            await WaitFor(() => Task.FromResult(content.ActualWidth > 1000), "Expanded Delete test layout");
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            model.IsSidebarReadFiltered = false;
            model.IsReaderMode = false;
            model.BuildSidebar();
            model.SelectedSidebarItem = Row(first);
            await model.FilterArticlesAsync();
            if (model.IsReadFiltered) await model.ToggleReadFilterCommand.ExecuteAsync(null);
            await model.WaitForCurrentTimelineAsync(CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == status.ArticleID);
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady), "Delete focus article");
            var timeline = (ListView)content.FindName("ArticleListView");
            timeline.ScrollIntoView(model.SelectedArticle);
            await WaitFor(() => Task.FromResult(timeline.ContainerFromItem(model.SelectedArticle) is ListViewItem { IsLoaded: true }),
                "Delete timeline row realization");
            await Focus((ListViewItem)timeline.ContainerFromItem(model.SelectedArticle));
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await NoDeletion("timeline");

            await Focus(content.ProbeWebView);
            await WaitFor(async () => await ScriptBool(content, "document.hasFocus()"), "Reader Delete focus");
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await NoDeletion("article reader");
            await content.OpenSelectedInWebViewAsync();
            await WaitFor(() => Task.FromResult(content.ProbeWebPageReady), "Delete focus webpage");
            await Focus(content.ProbeWebPageView);
            await content.ProbeWebPageView.ExecuteScriptAsync("""
                (() => {
                    const input = document.createElement('input');
                    input.id = 'delete-focus-input';
                    input.value = 'abcd';
                    document.body.prepend(input);
                    input.focus();
                    input.setSelectionRange(1, 2);
                })()
                """);
            await WaitFor(async () => JsonSerializer.Deserialize<bool>(await content.ProbeWebPageView.ExecuteScriptAsync("document.hasFocus()")),
                "Webpage Delete focus");
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await WaitFor(async () => JsonSerializer.Deserialize<string>(await content.ProbeWebPageView.ExecuteScriptAsync(
                "document.getElementById('delete-focus-input').value")) == "acd", "Webpage text deletion");
            await NoDeletion("embedded browser");
            await content.GoBackAsync();
            await WaitFor(() => Task.FromResult(content.ProbeArticleReady && !content.ProbeShowingWebPage), "Reader after Delete webpage check");

            window.FocusArticleSearch();
            var search = Descendants(content.ProbeToolbarSearch).OfType<TextBox>().Single();
            search.Text = "abcd";
            await Focus(search);
            search.Select(1, 1);
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await WaitFor(() => Task.FromResult(search.Text == "acd"), "Search text deletion");
            await NoDeletion("title-bar search");
            model.SearchQuery = "";
            content.ProbeToolbarSearch!.Text = "";
            await model.FilterArticlesAsync();
            sidebar.SelectedItem = Row(first);
            await content.RenameSelectedAsync();
            await WaitFor(() => Task.FromResult(Row(first).IsRenaming), "Inline rename for Delete");
            var container = (ListViewItem)sidebar.ContainerFromItem(Row(first));
            var editor = Descendants(container).OfType<TextBox>().Single();
            editor.Text = "abcd";
            await Focus(editor);
            editor.Select(1, 1);
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await WaitFor(() => Task.FromResult(editor.Text == "acd"), "Inline rename text deletion");
            await NoDeletion("inline rename");
            InvokeButton(Descendants(container).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Cancel rename"));
            await WaitFor(() => Task.FromResult(!Row(first).IsRenaming), "Inline rename cancelled");

            await SidebarFocus(first);
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await Confirm(cancel: true);
            Require(Subscribed(), "Cancelling Delete changed subscriptions.");
            await WaitFor(() => Task.FromResult(content.CanDeleteSidebarSelection), "Delete available after cancellation");
            await SidebarFocus(first);
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await Confirm(cancel: false);
            await WaitFor(() => Task.FromResult(!account.TopLevelFeeds.Contains(first) && model.CanUndo), "Single feed Delete key");
            await model.UndoAsync();
            Require(Subscribed(), "Undo did not restore the single deleted feed.");

            await SidebarFocus(second);
            sidebar.SelectedItems.Clear();
            foreach (var value in new object[] { first, second, folder }) sidebar.SelectedItems.Add(Row(value));
            Require(model.SelectedSidebarItems.Count == 3 && model.SelectedSidebarItem is null, "The Delete fixture lost its multi-selection.");
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await Confirm(cancel: false);
            await WaitFor(() => Task.FromResult(!account.TopLevelFeeds.Contains(first) && !account.TopLevelFeeds.Contains(second)
                && !account.Folders.Contains(folder) && model.CanUndo), "Multi-selection Delete key");
            await model.UndoAsync();
            Require(Subscribed(), "One undo did not restore every selected feed/folder.");
            var smart = sidebar.Items.OfType<SidebarItem>().First(item => item.ItemType == SidebarItemType.SmartFeed);
            sidebar.SelectedItem = smart;
            await SidebarFocus(smart.Tag!);
            Require(!content.CanDeleteSidebarSelection, "A smart feed is eligible for deletion.");
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await NoDeletion("smart feed");
            sidebar.SelectedItems.Add(Row(first));
            Require(model.SelectedSidebarItems.Count == 2 && !content.CanDeleteSidebarSelection,
                "A mixed smart-feed/subscription selection is eligible for deletion.");
            SendOwnedWindowKey(window, global::Windows.System.VirtualKey.Delete);
            await NoDeletion("mixed ineligible selection");
            return new { NativeDeleteInput = true, SingleAndMultipleSelection = true, ConfirmationAndUndo = true,
                RepeatKeyGuard = true, TimelineUnaffected = true, ReaderUnaffected = true, BrowserEditingPreserved = true,
                SearchEditingPreserved = true, RenameEditingPreserved = true, SmartAndMixedSelectionProtected = true };
        }
        finally
        {
            Dialog()?.Hide();
            model.UndoManager.RemoveAll();
            foreach (var feed in new[] { first, second, child }) account.RemoveFeed(feed);
            account.RemoveFolder(folder);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            window.AppWindow.MoveAndResize(new(original.Bounds.X, original.Bounds.Y, original.Bounds.Width, original.Bounds.Height));
            window.Activate();
        }
    }
}
#endif
