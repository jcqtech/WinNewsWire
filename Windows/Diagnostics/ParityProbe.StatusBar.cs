#if PARITY_PROBE
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Account;
using WinNewsWire.AppShared;
using WinNewsWire.AppShared.StatusBar;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckStatusBarAsync(MainWindow window)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var defaults = AppDefaults.Shared;
        bool oldShow = defaults.ShowStatusBar, oldThemes = defaults.ShowStatusBarThemeMenu;
        var oldMode = defaults.GetStatusBarCountMode();
        var oldUnread = defaults.GetUnreadCountDisplay();
        var original = window.CaptureState();
        MainWindow? other = null;
        Folder? fixtureFolder = null;
        var surface = (Border)content.FindName("StatusBarSurface");
        var count = (TextBlock)content.FindName("StatusBarCountText");
        var countButton = (Button)content.FindName("StatusBarCountButton");
        var link = (TextBlock)content.FindName("StatusBarLink");
        var theme = (DropDownButton)content.FindName("StatusBarThemeButton");
        var progress = (ProgressBar)content.FindName("StatusBarProgress");
        var oldRefresh = AccountManager.Shared.DefaultAccount.Settings.LastRefreshCompletedDate;
        try
        {
            await content.Initialization.WaitAsync(TimeSpan.FromSeconds(30));
            window.AppWindow.MoveAndResize(MainWindow.ClampToDisplay(original.Bounds with
                { Width = (int)Math.Ceiling(1400 * content.XamlRoot.RasterizationScale) }));
            model.IsUnifiedLayout = false;
            model.IsSidebarVisible = true;
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(AccountManager.Shared.DefaultAccount.AccountID,
                AccountManager.Shared.DefaultAccount.FlattenedFeeds().Single().FeedID);
            await model.FilterArticlesAsync();
            await model.WaitForCurrentTimelineAsync(System.Threading.CancellationToken.None);
            model.SelectedArticle = model.ArticleItems.Single(item => item.Id == "probe-article");
            defaults.ShowStatusBar = defaults.ShowStatusBarThemeMenu = true;
            defaults.SetStatusBarCountMode(StatusBarCountMode.Unread);
            defaults.SetUnreadCountDisplay(UnreadCountDisplay.Count);
            try
            {
                await WaitFor(() => Task.FromResult(surface.Visibility == Visibility.Visible && surface.ActualHeight == 32
                    && countButton.ActualWidth > 0 && countButton.IsEnabled
                    && count.Text.EndsWith(" unread", StringComparison.Ordinal)), "Status bar presentation");
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException($"Status bar: {surface.Visibility}, height={surface.ActualHeight}, "
                    + $"count={count.Text}, button={countButton.Visibility}/{countButton.ActualWidth}/{countButton.IsEnabled}, "
                    + $"sidebar={model.IsSidebarVisible}, reading={model.IsUnifiedLayout}, width={content.ActualWidth}, "
                    + $"column={((ColumnDefinition)content.FindName("SidebarColumn")).ActualWidth}, "
                    + $"statusColumn={((ColumnDefinition)content.FindName("StatusBarSidebarColumn")).Width}/"
                    + $"{((ColumnDefinition)content.FindName("StatusBarSidebarColumn")).ActualWidth}, "
                    + $"parent={((FrameworkElement)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(countButton)).ActualWidth}.", error);
            }
            Require(((Border)content.FindName("SidebarFooter")).Visibility == Visibility.Collapsed,
                "The old sidebar count duplicates the new status bar.");
            InvokeButton(countButton);
            await WaitFor(() => Task.FromResult(count.Text.Contains("feed", StringComparison.Ordinal)
                && defaults.GetStatusBarCountMode() == StatusBarCountMode.FoldersAndFeeds), "Count mode click");
            fixtureFolder = AccountManager.Shared.DefaultAccount.AddFolder("Status fixture folder");
            model.BuildSidebar();
            await WaitFor(() => Task.FromResult(count.Text.StartsWith("1 folder,", StringComparison.Ordinal)), "Changed folder count");
            defaults.SetUnreadCountDisplay(UnreadCountDisplay.Hidden);
            await WaitFor(() => Task.FromResult(countButton.IsEnabled && count.Text.Contains("feed", StringComparison.Ordinal)),
                "Hidden unread counts");
            InvokeButton(countButton);
            await WaitFor(() => Task.FromResult(defaults.GetStatusBarCountMode() == StatusBarCountMode.LastRefresh
                && count.Text == "Not updated yet"), "Last refresh count mode");
            AccountManager.Shared.DefaultAccount.Settings.LastRefreshCompletedDate = DateTime.UtcNow;
            await Task.Run(content.InvalidateStatusBarClock);
            await WaitFor(() => Task.FromResult(count.Text.StartsWith("Updated ", StringComparison.Ordinal)),
                "Worker-thread clock notification");
            foreach (uint message in new uint[] { 0x001A, 0x001E, 0x0218 })
            {
                var previousDay = DateTime.UtcNow.AddDays(-(int)(message % 3 + 1));
                AccountManager.Shared.DefaultAccount.Settings.LastRefreshCompletedDate = previousDay;
                SendWindowMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), message, IntPtr.Zero, IntPtr.Zero);
                await WaitFor(() => Task.FromResult(count.Text == "Updated "
                    + previousDay.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), "Native clock/settings/resume notification");
            }
            InvokeButton(countButton);
            await WaitFor(() => Task.FromResult(defaults.GetStatusBarCountMode() == StatusBarCountMode.FoldersAndFeeds
                && count.Text.Contains("feed", StringComparison.Ordinal)), "Hidden-count mode cycling");
            model.SetHoveredLink("https://example.com/a?b=1");
            await WaitFor(() => Task.FromResult(link.Text == "example.com/a?b=1"), "Hovered link presentation");
            Windows.ApplicationModel.DataTransfer.DataPackage? copied = null;
            content.ProbeClipboardWriter = package => copied = package;
            var linkMenu = (MenuFlyout)link.ContextFlyout;
            linkMenu.ShowAt(link);
            await WaitFor(() => Task.FromResult(linkMenu.Items.Count == 1 && linkMenu.Items[0].IsLoaded), "Link copy menu");
            InvokeMenuItem((MenuFlyoutItem)linkMenu.Items[0]);
            Require(copied is not null && await copied.GetView().GetTextAsync() == "https://example.com/a?b=1",
                "Status-bar Copy lost the full URL or its scheme.");
            linkMenu.Hide();
            model.SetHoveredLink(null);
            await WaitFor(() => Task.FromResult(link.Text == StatusBarPresentation.LinkText(model.SelectedArticle!.Link)),
                "Selected article link fallback");

            var gear = (Button)content.FindName("StatusBarGearButton");
            var gearMenu = (MenuFlyout)gear.Flyout;
            gearMenu.ShowAt(gear);
            await WaitFor(() => Task.FromResult(gearMenu.Items.Count > 3 && gearMenu.Items[0].IsLoaded), "Gear menu");
            Require(gearMenu.Items.OfType<MenuFlyoutItem>().Any(item => item.Text == "New Feed\u2026")
                && gearMenu.Items.OfType<MenuFlyoutItem>().Any(item => !item.IsEnabled && item.Text == model.SelectedSidebarItem!.Title),
                "Gear menu lost creation commands or the selected-subscription heading.");
            Require(gearMenu.Items[0] is MenuFlyoutItem { Text: "Refresh", IsEnabled: true },
                "Gear menu lost the available Refresh command.");
            var gearClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void GearClosed(object? sender, object args) => gearClosed.TrySetResult();
            gearMenu.Closed += GearClosed;
            try
            {
                InvokeMenuItem(gearMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "Hide Sidebar"));
                await WaitFor(() => Task.FromResult(!model.IsSidebarVisible && gear.ActualWidth > 0),
                    "Gear menu sidebar hiding");
                gearMenu.Hide();
                await gearClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { gearMenu.Closed -= GearClosed; }
            gearMenu.ShowAt(gear);
            try
            {
                await WaitFor(() => Task.FromResult(gearMenu.Items.OfType<MenuFlyoutItem>()
                    .Any(item => item.Text == "Show Sidebar" && item.IsLoaded)), "Gear menu sidebar restoration command");
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException($"Sidebar={model.IsSidebarVisible}, gear={gear.IsLoaded}/{gear.ActualWidth}, "
                    + $"status={surface.Visibility}/{surface.ActualWidth}, menu={HasMenuPopup(window)}, "
                    + $"items={string.Join("; ", gearMenu.Items.OfType<MenuFlyoutItem>().Select(item => $"{item.Text}/{item.IsLoaded}"))}.", error);
            }
            InvokeMenuItem(gearMenu.Items.OfType<MenuFlyoutItem>().Single(item => item.Text == "Show Sidebar"));
            await WaitFor(() => Task.FromResult(model.IsSidebarVisible), "Gear menu sidebar restoration");
            gearMenu.Hide();
            var sidebarColumn = (ColumnDefinition)content.FindName("SidebarColumn");
            var divider = (Border)content.FindName("StatusBarDivider");
            var readingPane = (Border)content.FindName("ContentContainer");
            foreach (double width in new[] { 200.0, 320.0, 400.0 })
            {
                await content.RestoreStateAsync(window.CaptureState() with { SidebarWidth = width });
                try
                {
                    await WaitFor(() => Task.FromResult(Math.Abs(sidebarColumn.ActualWidth - width) < 1
                        && Math.Abs(divider.TransformToVisual(content).TransformPoint(default).X
                            - readingPane.TransformToVisual(content).TransformPoint(default).X)
                        < 1 / content.XamlRoot.RasterizationScale), "Status-bar divider alignment after sidebar resize");
                }
                catch (TimeoutException error)
                {
                    throw new InvalidOperationException($"Requested sidebar={width}, actual={sidebarColumn.ActualWidth}, "
                        + $"status={((ColumnDefinition)content.FindName("StatusBarSidebarColumn")).ActualWidth}, "
                        + $"divider={divider.TransformToVisual(content).TransformPoint(default).X}, "
                        + $"reading={readingPane.TransformToVisual(content).TransformPoint(default).X}.", error);
                }
            }
            var themes = (MenuFlyout)theme.Flyout;
            themes.ShowAt(theme);
            await WaitFor(() => Task.FromResult(themes.Items.OfType<ToggleMenuFlyoutItem>().Count() >= 3), "Status-bar theme menu");
            Require(themes.Items.OfType<ToggleMenuFlyoutItem>().Count(item => item.IsChecked) == 1,
                "Status-bar themes do not share the selected article theme.");
            themes.Hide();
            defaults.ShowStatusBarThemeMenu = false;
            await WaitFor(() => Task.FromResult(theme.Visibility == Visibility.Collapsed), "Theme menu visibility");

            var combined = CombinedRefreshProgress.Shared;
            combined.AddAccount(AccountManager.Shared.DefaultAccount);
            var reporter = combined.GetProgressReporter(AccountManager.Shared.DefaultAccount);
            await Task.Run(() => reporter.Report(new ProgressInfo(5, 3, 8)));
            await WaitFor(() => Task.FromResult(progress.Visibility == Visibility.Visible && progress.Maximum == 8
                && progress.Value == 3 && count.Text == "3 of 8"), "Worker-thread refresh progress");
            foreach (var button in new[] { (Button)content.FindName("StatusBarActivityButton"), countButton })
            {
                var modeBeforeActivity = defaults.GetStatusBarCountMode();
                Require(!AppWindows.CurrentActivityWindow.ShouldOpenAtStartup, "Current Activity was already open.");
                try
                {
                    InvokeButton(button);
                    await WaitFor(() => Task.FromResult(AppWindows.CurrentActivityWindow.ShouldOpenAtStartup),
                        "Status-bar Current Activity action");
                    Require(defaults.GetStatusBarCountMode() == modeBeforeActivity,
                        "Clicking refresh progress changed the saved count mode.");
                }
                finally
                {
                    if (AppWindows.CurrentActivityWindow.ShouldOpenAtStartup)
                        AppWindows.CurrentActivityWindow.Show().Close();
                    window.Activate();
                }
            }
            reporter.Report(new ProgressInfo(0, 8, 8));
            await WaitFor(() => Task.FromResult(progress.Visibility == Visibility.Collapsed
                && count.Text.Contains("feed", StringComparison.Ordinal)), "Refresh completion");

            other = App.OpenMainWindow(original);
            Require(await other.Restoration.WaitAsync(TimeSpan.FromSeconds(30)), "Second status-bar window restoration failed.");
            content.SetStatusBarVisible(false);
            await WaitFor(() => Task.FromResult(surface.Visibility == Visibility.Collapsed
                && ((Border)other.ProbeContent.FindName("StatusBarSurface")).Visibility == Visibility.Collapsed
                && ((Border)content.FindName("SidebarFooter")).Visibility == Visibility.Visible), "Shared status-bar visibility");
            content.SetStatusBarVisible(true);
            await WaitFor(() => Task.FromResult(surface.Visibility == Visibility.Visible
                && ((Border)other.ProbeContent.FindName("StatusBarSurface")).Visibility == Visibility.Visible), "Restored status-bar visibility");
            return new { CountCycling = true, HiddenUnreadCounts = true, LastRefresh = true, ClockNotifications = true,
                DividerAlignment = true,
                FullLinkCopy = true, GearSelection = true,
                SharedThemes = true, WorkerProgress = true, ActivityActions = true, MultipleWindows = true, SidebarFooterFallback = true };
        }
        finally
        {
            content.ProbeClipboardWriter = null;
            model.SetHoveredLink(null);
            if (other is not null) await CloseWindowAsync(other);
            if (fixtureFolder is not null) AccountManager.Shared.DefaultAccount.RemoveFolder(fixtureFolder);
            CombinedRefreshProgress.Shared.Reset();
            AccountManager.Shared.DefaultAccount.Settings.LastRefreshCompletedDate = oldRefresh;
            defaults.ShowStatusBar = oldShow;
            defaults.ShowStatusBarThemeMenu = oldThemes;
            defaults.SetStatusBarCountMode(oldMode);
            defaults.SetUnreadCountDisplay(oldUnread);
            await content.RestoreStateAsync(original);
        }
    }
}
#endif
