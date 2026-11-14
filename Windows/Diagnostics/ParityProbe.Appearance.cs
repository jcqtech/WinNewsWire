#if PARITY_PROBE
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppShared;
using WinNewsWire.Core;
using WinNewsWire.Helpers;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task SettleAppearanceAsync(MainWindow window, ListView list)
    {
        list.UpdateLayout();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Require(window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => completion.TrySetResult()),
            "The appearance dispatcher is unavailable.");
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        list.UpdateLayout();
    }

    private static async Task<object?> CheckTimelineIconPolicyAsync(MainWindow window, string serverUrl)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var previousDisplay = AppDefaults.Shared.GetTimelineFeedIconsDisplay();
        bool previousColumns = AppDefaults.Shared.UseColumnLayout;
        var account = Account.AccountManager.Shared.DefaultAccount;
        var feed = account.AddFeed(serverUrl + "/timeline-policy", "Timeline policy");
        var list = (ListView)content.FindName("ArticleListView");
        try
        {
            model.BuildSidebar();
            model.SelectedSidebarItem = model.FindSidebarItemForFeed(account.AccountID, feed.FeedID);
            await model.WaitForCurrentTimelineAsync(System.Threading.CancellationToken.None);
            model.IsUnifiedLayout = false;
            model.ArticleItems = new ObservableCollection<Models.FeedItem>
                { new() { Id = "policy", Title = "Icon policy", FeedTitle = feed.NameForDisplay } };
            foreach (var (columns, mode, visible) in new[]
            {
                (true, TimelineFeedIconsDisplay.Never, false),
                (true, TimelineFeedIconsDisplay.Automatic, true),
                (false, TimelineFeedIconsDisplay.Automatic, false),
                (false, TimelineFeedIconsDisplay.Always, true),
                (false, TimelineFeedIconsDisplay.Never, false)
            })
            {
                AppDefaults.Shared.UseColumnLayout = columns;
                AppDefaults.Shared.SetTimelineFeedIconsDisplay(mode);
                await WaitFor(() => Task.FromResult(list.ContainerFromIndex(0) is ListViewItem { IsLoaded: true }), "Icon-policy row");
                await SettleAppearanceAsync(window, list);
                var icon = Descendants((DependencyObject)list.ContainerFromIndex(0)).OfType<IconView>().Single();
                Require((icon.Visibility == Visibility.Visible) == visible,
                    $"Timeline icon policy was ignored: columns={columns}, mode={mode}.");
                if (columns && !visible)
                    Require(icon.Parent is Grid { ColumnSpacing: 0 } grid && grid.ColumnDefinitions[0].ActualWidth == 0,
                        "The hidden column feed icon left an empty slot before the name.");
            }
            return new { Standard = true, Columns = true, Always = true, Never = true, Automatic = true, NoEmptySlot = true };
        }
        finally
        {
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(previousDisplay);
            AppDefaults.Shared.UseColumnLayout = previousColumns;
            account.RemoveFeed(feed);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckSmartFeedVisibilityAsync(MainWindow window)
    {
        var keys = new[] { "showTodayFeed", "showUnreadFeed", "showStarredFeed" };
        var saved = keys.Select(key => AppDefaults.Shared.GetValue(key, true)).ToArray();
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var controller = AppShared.SmartFeeds.SmartFeedsController.Shared;
        var smartFeeds = controller.SmartFeeds.ToArray();
        var list = SidebarList(content);
        var preferences = new AppWindows.PreferencesWindow();
        try
        {
            foreach (string key in keys) AppDefaults.Shared.SetValue(key, true);
            model.BuildSidebar();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(((FrameworkElement)preferences.Content).IsLoaded), "Smart-feed preferences");
            var toggles = new[] { "ShowTodayFeedToggle", "ShowUnreadFeedToggle", "ShowStarredFeedToggle" }
                .Select(name => Descendants(preferences.Content).OfType<ToggleSwitch>().SingleOrDefault(control => control.Name == name)).ToArray();
            Require(toggles.All(control => control is not null), "Preferences do not expose smart-feed visibility controls.");
            var menus = Descendants(window.Content).OfType<MenuBar>().SelectMany(bar => bar.Items)
                .SelectMany(item => item.Items).OfType<MenuFlyoutItem>()
                .Where(item => item.Name is "MenuGoToday" or "MenuGoUnread" or "MenuGoStarred").ToArray();
            Require(menus.Length == 3, "Smart-feed navigation commands are missing.");
            for (int i = 0; i < toggles.Length; i++)
            {
                toggles[i]!.IsOn = false;
                int hidden = i;
                await WaitFor(() => Task.FromResult(!list.Items.OfType<Models.SidebarItem>()
                    .Any(row => ReferenceEquals(row.Tag, smartFeeds[hidden]))), "Hidden smart feed removed from native outline");
                Require(!new AppDefaults().GetValue(keys[i], true), "Smart-feed visibility did not persist.");
            }
            Require(menus.All(menu => !menu.IsEnabled) && !list.Items.OfType<Models.SidebarItem>().Any(row => row.Title == controller.NameForDisplay),
                "Hidden smart feeds retained Go commands or an empty sidebar group.");
            var selection = model.SelectedSidebarItem;
            await content.GoToSmartFeedAsync(controller.TodayFeed);
            Require(ReferenceEquals(model.SelectedSidebarItem, selection), "Direct navigation selected a hidden smart feed.");
            using var peer = new ViewModels.MainViewModel();
            peer.BuildSidebar();
            Require(!peer.SidebarItems.SelectMany(row => row.Children).Any(row => row.ItemType == Models.SidebarItemType.SmartFeed),
                "A new timeline ignored persisted smart-feed visibility.");
            foreach (string key in keys) await Task.Run(() => AppDefaults.Shared.SetValue(key, true));
            await WaitFor(() => Task.FromResult(toggles.All(toggle => toggle!.IsOn)
                && menus.All(menu => menu.IsEnabled) && list.Items.OfType<Models.SidebarItem>()
                    .Count(row => row.ItemType == Models.SidebarItemType.SmartFeed) == 3), "Restored smart-feed outline and commands");
            Require(smartFeeds.SequenceEqual(controller.SmartFeeds), "Hiding a smart feed destroyed its underlying identity.");
            return new { ThreePreferences = true, EmptyGroupHidden = true, Commands = true, NewWindow = true, Identities = true };
        }
        finally
        {
            preferences.Close();
            for (int i = 0; i < keys.Length; i++) AppDefaults.Shared.SetValue(keys[i], saved[i]);
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckSidebarTextSizeAsync(MainWindow window)
    {
        const string key = "sidebarTextSize";
        int saved = AppDefaults.Shared.GetValue(key, 0);
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var list = SidebarList(content);
        var preferences = new AppWindows.PreferencesWindow();
        try
        {
            model.BuildSidebar();
            var row = list.Items.OfType<Models.SidebarItem>().First(item => item.ItemType == Models.SidebarItemType.SmartFeed);
            model.SelectedSidebarItem = row;
            preferences.Activate();
            await WaitFor(() => Task.FromResult(((FrameworkElement)preferences.Content).IsLoaded
                && list.ContainerFromItem(row) is ListViewItem { IsLoaded: true }), "Sidebar text-size fixture");
            var choice = Descendants(preferences.Content).OfType<ComboBox>()
                .SingleOrDefault(combo => combo.Name == "SidebarTextSizeCombo");
            Require(choice is not null, "Preferences have no sidebar text-size choice.");
            var container = (ListViewItem)list.ContainerFromItem(row);
            var title = Descendants(container).OfType<TextBlock>().Single(text => text.Text == row.Title);
            var icon = Descendants(container).OfType<FontIcon>().Single(control => Grid.GetColumn(control) == 1);
            double smallHeight = 0;
            foreach (var (value, font, dimension) in new[] { (1, 11.0, 16.0), (5, 20.0, 28.0), (0, 14.0, 16.0) })
            {
                choice!.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(item => Convert.ToInt32(item.Tag) == value);
                await WaitFor(() => Task.FromResult(title.FontSize == font && icon.Width == dimension), "Sidebar text and icon sizing");
                list.UpdateLayout();
                if (value == 1) smallHeight = container.ActualHeight;
                if (value == 5) Require(container.ActualHeight >= 42 && container.ActualHeight > smallHeight,
                    "Larger sidebar text did not increase the native row height.");
                Require(ReferenceEquals(model.SelectedSidebarItem, row) && container.IsSelected,
                    "Sidebar text resizing lost the selection.");
                Require(new AppDefaults().GetValue(key, 0) == value, "Sidebar text size did not persist.");
            }
            return new { Small = true, ExtraExtraLarge = true, System = true, RowHeight = true, Selection = true };
        }
        finally
        {
            preferences.Close();
            AppDefaults.Shared.SetValue(key, saved);
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckSidebarIconsAsync(MainWindow window, string serverUrl)
    {
        const string key = "sidebarShowsIcons";
        bool saved = AppDefaults.Shared.GetValue(key, true);
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var original = window.CaptureState();
        var account = Account.AccountManager.Shared.DefaultAccount;
        var folder = account.AddFolder("Sidebar appearance folder");
        var feed = account.AddFeed(serverUrl + "/sidebar-appearance", "Sidebar appearance feed", folder);
        var list = SidebarList(content);
        var preferences = new AppWindows.PreferencesWindow();
        try
        {
            AppDefaults.Shared.SetValue(key, true);
            model.BuildSidebar();
            var rows = list.Items.OfType<Models.SidebarItem>()
                .Where(item => ReferenceEquals(item.Tag, folder) || ReferenceEquals(item.Tag, feed)
                    || item.ItemType == Models.SidebarItemType.SmartFeed).ToArray();
            Require(rows.Length >= 3, "Sidebar appearance fixtures are missing.");
            await WaitFor(() => Task.FromResult(rows.All(row => list.ContainerFromItem(row) is ListViewItem { IsLoaded: true })),
                "Sidebar appearance rows");
            FrameworkElement Icon(Models.SidebarItem row) => Descendants((DependencyObject)list.ContainerFromItem(row))
                .OfType<FrameworkElement>().Single(element => Grid.GetColumn(element) == 1 && element is Image or FontIcon
                    && (ReferenceEquals(row.Tag, feed) ? element is Image : element is FontIcon));
            Require(rows.All(row => Icon(row).Visibility == Visibility.Visible), "Sidebar icons were not initially visible.");
            preferences.Activate();
            await WaitFor(() => Task.FromResult(((FrameworkElement)preferences.Content).IsLoaded), "Sidebar icon preferences");
            var toggle = Descendants(preferences.Content).OfType<ToggleSwitch>()
                .SingleOrDefault(control => control.Name == "SidebarShowIconsToggle");
            Require(toggle is not null, "Preferences have no sidebar icon visibility control.");
            toggle!.IsOn = false;
            await WaitFor(() => Task.FromResult(rows.All(row => Icon(row).Visibility == Visibility.Collapsed)),
                "Hidden sidebar feed, folder and smart-feed icons");
            Require(!new AppDefaults().GetValue(key, true), "Sidebar icon visibility was not persisted.");
            await Task.Run(() => AppDefaults.Shared.SetValue(key, true));
            await WaitFor(() => Task.FromResult(toggle.IsOn && rows.All(row => Icon(row).Visibility == Visibility.Visible)),
                "Sidebar icons restored from worker preference notification");
            Require(rows.All(row => list.Items.Contains(row)), "Changing icon visibility replaced sidebar identities.");
            return new { Feed = true, Folder = true, SmartFeeds = true, IdentityPreserved = true, Persisted = true };
        }
        finally
        {
            preferences.Close();
            AppDefaults.Shared.SetValue(key, saved);
            account.RemoveFeed(feed);
            account.RemoveFolder(folder);
            model.BuildSidebar();
            await content.RestoreStateAsync(original);
            window.Activate();
        }
    }

    private static async Task<object?> CheckAppearancePaletteAsync(MainWindow window)
    {
        var saved = AppDefaults.Shared.AppearanceMode;
        var preferences = new AppWindows.PreferencesWindow();
        try
        {
            preferences.Activate();
            await WaitFor(() => Task.FromResult(((FrameworkElement)preferences.Content).IsLoaded), "Appearance preferences");
            var choice = Descendants(preferences.Content).OfType<ComboBox>()
                .SingleOrDefault(combo => combo.Name == "AppearanceModeCombo");
            Require(choice is not null, "Appearance preferences have no system/light/dark choice.");
            foreach (var mode in new[] { AppDefaults.Appearance.Light, AppDefaults.Appearance.Dark, AppDefaults.Appearance.System })
            {
                choice!.SelectedItem = choice.Items.OfType<ComboBoxItem>()
                    .Single(item => item.Tag is AppDefaults.Appearance value && value == mode);
                var expected = mode == AppDefaults.Appearance.Light ? ElementTheme.Light
                    : mode == AppDefaults.Appearance.Dark ? ElementTheme.Dark : ElementTheme.Default;
                await WaitFor(() => Task.FromResult(((FrameworkElement)window.Content).RequestedTheme == expected
                    && ((FrameworkElement)preferences.Content).RequestedTheme == expected),
                    "Main and Preferences windows apply appearance choice");
                Require(new AppDefaults().AppearanceMode == mode, "Appearance choice was not persisted.");
            }
            await Task.Run(() => AppDefaults.Shared.AppearanceMode = AppDefaults.Appearance.Dark);
            await WaitFor(() => Task.FromResult(choice!.SelectedItem is ComboBoxItem { Tag: AppDefaults.Appearance.Dark }
                && ((FrameworkElement)window.Content).RequestedTheme == ElementTheme.Dark), "Worker appearance notification");
            var peer = new AppWindows.PreferencesWindow();
            try
            {
                Require(((FrameworkElement)peer.Content).RequestedTheme == ElementTheme.Dark,
                    "A new Preferences window did not initialize from the saved appearance.");
            }
            finally { peer.Close(); }
            return new { Choices = 3, MainAndSecondaryWindows = true, WorkerNotification = true, Persisted = true };
        }
        finally
        {
            AppDefaults.Shared.AppearanceMode = saved;
            preferences.Close();
            window.Activate();
        }
    }
}
#endif
