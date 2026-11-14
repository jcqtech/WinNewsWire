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
    private static Models.FeedItem AppearanceRow(string id, string title, string body = "Preview text.", bool read = true, string? thumbnail = null)
    {
        var article = new Articles.Article("appearance", id, "appearance-feed", id, title, body, null, null,
            "https://appearance.test/" + id, null, null, null, null, null, null, null,
            new Articles.ArticleStatus(id, read, false, DateTime.UnixEpoch), thumbnailURL: thumbnail);
        return new Models.FeedItem { Id = id, AccountID = article.AccountID, SourceArticle = article,
            FeedTitle = "Appearance feed", IsRead = read };
    }

    private static async Task<object?> WithAppearanceTimelineAsync(MainWindow window,
        Func<MainContent, ListView, ObservableCollection<Models.FeedItem>, AppWindows.PreferencesWindow, Task<object?>> check)
    {
        var content = window.ProbeContent;
        var model = content.ViewModel;
        var previousItems = model.ArticleItems;
        var previousSelection = model.SelectedArticles.ToArray();
        bool previousColumns = AppDefaults.Shared.UseColumnLayout, previousUnified = model.IsUnifiedLayout;
        var previousIcons = AppDefaults.Shared.GetTimelineFeedIconsDisplay();
        var previousThumbnails = AppDefaults.Shared.GetTimelineThumbnailsDisplay();
        var list = (ListView)content.FindName("ArticleListView");
        double previousWidth = list.MaxWidth;
        AppWindows.PreferencesWindow? preferences = null;
        try
        {
            model.SelectedArticle = null;
            AppDefaults.Shared.UseColumnLayout = false;
            model.IsUnifiedLayout = false;
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Always);
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(TimelineThumbnailsDisplay.None);
            list.MaxWidth = 340;
            var rows = new ObservableCollection<Models.FeedItem>
                { AppearanceRow("short", "Short title"), AppearanceRow("long", string.Concat(Enumerable.Repeat("Long wrapped title ", 15))) };
            model.ArticleItems = rows;
            preferences = new AppWindows.PreferencesWindow();
            preferences.Activate();
            await WaitFor(() => Task.FromResult(((FrameworkElement)preferences.Content).IsLoaded
                && list.ContainerFromIndex(1) is ListViewItem { IsLoaded: true }), "Timeline appearance fixture");
            return await check(content, list, rows, preferences);
        }
        finally
        {
            preferences?.Close();
            model.SelectedArticle = null;
            list.MaxWidth = previousWidth;
            model.ArticleItems = previousItems;
            model.SetSelectedArticles(previousSelection);
            AppDefaults.Shared.SetTimelineFeedIconsDisplay(previousIcons);
            AppDefaults.Shared.SetTimelineThumbnailsDisplay(previousThumbnails);
            AppDefaults.Shared.UseColumnLayout = previousColumns;
            model.IsUnifiedLayout = previousUnified;
            window.Activate();
        }
    }

    private static async Task<object?> CheckTimelineIconSizeAsync(MainWindow window)
    {
        const string key = "timelineIconSize";
        int saved = AppDefaults.Shared.GetValue(key, 3);
        try
        {
            return await WithAppearanceTimelineAsync(window, async (_, list, _, preferences) =>
            {
                var choice = Descendants(preferences.Content).OfType<ComboBox>().SingleOrDefault(combo => combo.Name == "TimelineIconSizeCombo");
                Require(choice is not null, "Preferences have no timeline icon-size choice.");
                foreach (var (value, dimension) in new[] { (1, 24.0), (2, 36.0), (3, 48.0) })
                {
                    choice!.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(item => Convert.ToInt32(item.Tag) == value);
                    await SettleAppearanceAsync(window, list);
                    var icon = Descendants((DependencyObject)list.ContainerFromIndex(0)).OfType<IconView>().Single();
                    Require(icon.ActualWidth == dimension && icon.ActualHeight == dimension, "The timeline icon did not resize.");
                    Require(new AppDefaults().GetValue(key, 3) == value, "Timeline icon size did not persist.");
                }
                AppDefaults.Shared.UseColumnLayout = true;
                await SettleAppearanceAsync(window, list);
                Require(Descendants((DependencyObject)list.ContainerFromIndex(0)).OfType<IconView>().Single().Width == 16,
                    "The standard icon size enlarged column-layout feed icons.");
                return new { Small = 24, Medium = 36, Large = 48, ColumnSize = 16, Persisted = true };
            });
        }
        finally { AppDefaults.Shared.SetValue(key, saved); }
    }

    private static async Task<object?> CheckTimelineTextSizeAsync(MainWindow window)
    {
        const string key = "timelineTextSize";
        int saved = AppDefaults.Shared.GetValue(key, 2);
        try
        {
            return await WithAppearanceTimelineAsync(window, async (content, list, rows, preferences) =>
            {
                var choice = Descendants(preferences.Content).OfType<ComboBox>().SingleOrDefault(combo => combo.Name == "TimelineTextSizeCombo");
                Require(choice is not null, "Preferences have no timeline text-size choice.");
                content.ViewModel.SelectedArticle = rows[0];
                foreach (var (value, size) in new[] { (1, 12.0), (3, 16.0), (5, 22.0), (2, 14.0) })
                {
                    choice!.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(item => Convert.ToInt32(item.Tag) == value);
                    await SettleAppearanceAsync(window, list);
                    var row = (ListViewItem)list.ContainerFromIndex(0);
                    var textBlocks = Descendants(row).OfType<TextBlock>().Where(text => text.Name is "TimelineTitle" or "TimelineSummary").ToArray();
                    Require(textBlocks.Length == 2 && textBlocks.All(text => text.FontSize == size),
                        "Timeline title and summary did not resize together.");
                    Require(Descendants(row).OfType<TextBlock>().Single(text => text.Text == rows[0].FeedTitle).FontSize == Math.Floor(size * 0.9),
                        "Timeline metadata did not follow the selected text size.");
                    Require(ReferenceEquals(content.ViewModel.SelectedArticle, rows[0]) && row.IsSelected, "Timeline text resizing lost selection.");
                    Require(new AppDefaults().GetValue(key, 2) == value, "Timeline text size did not persist.");
                }
                AppDefaults.Shared.UseColumnLayout = true;
                rows[1].IsRead = false;
                await Task.Run(() => AppDefaults.Shared.SetValue(key, 5));
                await SettleAppearanceAsync(window, list);
                var title = Descendants((DependencyObject)list.ContainerFromIndex(1)).OfType<TextBlock>().Single(text => text.Tag is "Title");
                Require(title.FontSize == 22 && title.FontWeight == Microsoft.UI.Text.FontWeights.Bold,
                    "Column text size lost unread bold styling or ignored the preference.");
                return new { Standard = true, Columns = true, Metadata = true, UnreadBold = true, Selection = true };
            });
        }
        finally { AppDefaults.Shared.SetValue(key, saved); }
    }

    private static async Task<object?> CheckTimelineLinesAsync(MainWindow window)
    {
        const string key = "timelineNumberOfLines";
        int saved = AppDefaults.Shared.GetValue(key, 3);
        try
        {
            return await WithAppearanceTimelineAsync(window, async (_, list, rows, preferences) =>
            {
                var choice = Descendants(preferences.Content).OfType<ComboBox>().SingleOrDefault(combo => combo.Name == "TimelineNumberOfLinesCombo");
                Require(choice is not null, "Preferences have no title and preview line-count choice.");
                TextBlock Text(int index, string name) => Descendants((DependencyObject)list.ContainerFromIndex(index))
                    .OfType<TextBlock>().Single(block => block.Name == name);
                choice!.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(item => Convert.ToInt32(item.Tag) == 1);
                await SettleAppearanceAsync(window, list);
                Require(Text(0, "TimelineTitle").MaxLines == 1 && Text(0, "TimelineSummary").Visibility == Visibility.Collapsed,
                    "A one-line budget also displayed a preview.");
                double oneLineHeight = Text(1, "TimelineTitle").ActualHeight;
                choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(item => Convert.ToInt32(item.Tag) == 3);
                await SettleAppearanceAsync(window, list);
                Require(Text(0, "TimelineSummary").Visibility == Visibility.Visible && Text(0, "TimelineSummary").MaxLines == 2,
                    "The short title did not leave the remaining two lines for its preview.");
                Require(Text(1, "TimelineTitle").MaxLines == 3 && Text(1, "TimelineSummary").Visibility == Visibility.Collapsed
                    && Text(1, "TimelineTitle").ActualHeight > oneLineHeight * 2,
                    "A long title did not consume the shared line budget.");
                rows[1] = AppearanceRow("untitled", "", string.Concat(Enumerable.Repeat("Untitled body text ", 25)));
                choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().Single(item => Convert.ToInt32(item.Tag) == 6);
                await SettleAppearanceAsync(window, list);
                Require(Text(1, "TimelineTitle").Visibility == Visibility.Collapsed && Text(1, "TimelineSummary").MaxLines == 6
                    && Text(1, "TimelineSummary").ActualHeight > oneLineHeight * 4,
                    "A titleless article did not give its full line budget to the preview.");
                Require(new AppDefaults().GetValue(key, 3) == 6, "Timeline line budget did not persist.");
                return new { OneLine = true, SharedBudget = true, WrappedTitle = true, Titleless = true, Persisted = true };
            });
        }
        finally { AppDefaults.Shared.SetValue(key, saved); }
    }

    private static async Task<object?> CheckTimelineFullTitlesAsync(MainWindow window)
    {
        const string key = "timelineShowFullTitles";
        bool saved = AppDefaults.Shared.GetValue(key, false);
        int savedLines = AppDefaults.Shared.GetTimelineNumberOfLines();
        try
        {
            AppDefaults.Shared.SetValue(key, false);
            AppDefaults.Shared.SetTimelineNumberOfLines(1);
            return await WithAppearanceTimelineAsync(window, async (_, list, _, preferences) =>
            {
                var toggle = Descendants(preferences.Content).OfType<ToggleSwitch>().SingleOrDefault(control => control.Name == "TimelineShowFullTitlesToggle");
                Require(toggle is not null, "Preferences have no full-title mode control.");
                var choices = Descendants(preferences.Content).OfType<ComboBox>().Single(control => control.Name == "TimelineNumberOfLinesCombo");
                TextBlock Title() => Descendants((DependencyObject)list.ContainerFromIndex(1)).OfType<TextBlock>()
                    .Single(block => block.Name == "TimelineTitle");
                double oneLineHeight = Title().ActualHeight;
                toggle!.IsOn = true;
                await SettleAppearanceAsync(window, list);
                Require(!choices.IsEnabled && Title().MaxLines == 12 && Title().ActualHeight > oneLineHeight * 3,
                    "Full-title mode did not expand the title or disable the irrelevant line picker.");
                Require(Descendants(list).OfType<TextBlock>().Where(block => block.Name == "TimelineSummary")
                    .All(block => block.Visibility == Visibility.Collapsed), "Full-title mode retained previews.");
                Require(new AppDefaults().GetValue(key, false) && new AppDefaults().GetTimelineNumberOfLines() == 1,
                    "Full-title mode overwrote the saved preview line count.");
                await Task.Run(() => AppDefaults.Shared.SetValue(key, false));
                await SettleAppearanceAsync(window, list);
                Require(choices.IsEnabled && !toggle.IsOn && Title().MaxLines == 1,
                    "Leaving full-title mode did not restore the saved line limit.");
                return new { TwelveLineCap = true, PreviewHidden = true, LineChoicePreserved = true, WorkerNotification = true };
            });
        }
        finally { AppDefaults.Shared.SetValue(key, saved); AppDefaults.Shared.SetTimelineNumberOfLines(savedLines); }
    }

    private static async Task<object?> CheckTimelineReadFadingAsync(MainWindow window)
    {
        const string key = "timelineFadeReadArticles";
        bool saved = AppDefaults.Shared.GetValue(key, false);
        try
        {
            return await WithAppearanceTimelineAsync(window, async (_, list, rows, preferences) =>
            {
                var toggle = Descendants(preferences.Content).OfType<ToggleSwitch>()
                    .SingleOrDefault(control => control.Name == "TimelineFadeReadArticlesToggle");
                Require(toggle is not null, "Preferences have no read-article fading control.");
                Grid Root(int index) => Descendants((DependencyObject)list.ContainerFromIndex(index)).OfType<Grid>()
                    .Single(grid => grid.Name is "TimelineRowContent" or "TimelineColumnRow");
                double ContentOpacity(int index) => Root(index).Children.First(child => child is StackPanel or TextBlock).Opacity;
                rows[1].IsRead = false;
                toggle!.IsOn = true;
                await SettleAppearanceAsync(window, list);
                Require(Math.Abs(ContentOpacity(0) - 0.6) < 0.000001 && ContentOpacity(1) == 1,
                    $"Read fading affected unread articles or did not fade read content: {ContentOpacity(0)}, {ContentOpacity(1)}.");
                Require(Root(0).Opacity == 1 && Root(0).Children.OfType<Border>().Single(border => border.Name == "TimelineRowSeparator").Opacity == 1,
                    "Read fading affected the row background or separator.");
                list.SelectedItem = rows[0];
                await SettleAppearanceAsync(window, list);
                Require(ContentOpacity(0) == 1 && ((ListViewItem)list.ContainerFromIndex(0)).Opacity == 1,
                    "The selected read row or its highlight was faded.");
                list.SelectedItems.Clear();
                rows[0].IsRead = false;
                await SettleAppearanceAsync(window, list);
                Require(ContentOpacity(0) == 1, "Marking an article unread retained its fade.");
                rows[0].IsRead = true;
                AppDefaults.Shared.UseColumnLayout = true;
                await SettleAppearanceAsync(window, list);
                Require(Math.Abs(ContentOpacity(0) - 0.6) < 0.000001 && ContentOpacity(1) == 1, "Column cells ignored read fading.");
                await Task.Run(() => AppDefaults.Shared.SetValue(key, false));
                await SettleAppearanceAsync(window, list);
                Require(!toggle.IsOn && ContentOpacity(0) == 1, "Disabling read fading left visible cells dim.");
                return new { SelectedProtected = true, UnreadProtected = true, SeparatorProtected = true, Columns = true, ReadChanges = true };
            });
        }
        finally { AppDefaults.Shared.SetValue(key, saved); }
    }

    private static async Task<object?> CheckTimelineBylineLayoutAsync(MainWindow window)
    {
        const string key = "timelineFeedNameAndDateAboveTitle";
        bool saved = AppDefaults.Shared.GetValue(key, false);
        try
        {
            return await WithAppearanceTimelineAsync(window, async (_, list, rows, preferences) =>
            {
                var toggle = Descendants(preferences.Content).OfType<ToggleSwitch>()
                    .SingleOrDefault(control => control.Name == "TimelineFeedNameAndDateAboveTitleToggle");
                Require(toggle is not null, "Preferences have no byline placement control.");
                rows[0].PublishedDate = DateTimeOffset.UtcNow.AddHours(-1);
                TextBlock Text(string name) => Descendants((DependencyObject)list.ContainerFromIndex(0)).OfType<TextBlock>()
                    .Single(block => block.Name == name);
                double Top(FrameworkElement element) => element.TransformToVisual(list).TransformPoint(default).Y;
                foreach (bool above in new[] { true, false, true })
                {
                    toggle!.IsOn = above;
                    await WaitFor(() => Task.FromResult(above
                        ? Top(Text("TimelineDate")) + Text("TimelineDate").ActualHeight <= Top(Text("TimelineTitle"))
                        : Top(Text("TimelineDate")) >= Top(Text("TimelineTitle")) + Text("TimelineTitle").ActualHeight),
                        "Byline placement reaches its native layout");
                    Require(Math.Abs(Top(Text("TimelineBylineText")) - Top(Text("TimelineDate"))) < 1,
                        "The byline and date do not share a line.");
                    Require(new AppDefaults().GetValue(key, false) == above, "Byline placement did not persist.");
                }
                return new { Above = true, Below = true, DateAligned = true, Persisted = true };
            });
        }
        finally { AppDefaults.Shared.SetValue(key, saved); }
    }

    private static async Task SettleAppearanceAsync(MainWindow window, ListView list)
    {
        list.UpdateLayout();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Require(window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => completion.TrySetResult()),
            "The appearance dispatcher is unavailable.");
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        list.UpdateLayout();
    }

    private static async Task<object?> CheckTimelineIconPolicyAsync(MainWindow window, string serverUrl, bool recheck = false)
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
            if (recheck)
            {
                AppDefaults.Shared.UseColumnLayout = false;
                AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Automatic);
                foreach (bool avatar in new[] { false, true, false })
                {
                    var author = Articles.Author.Create(null, "Fixture author", null, avatar ? serverUrl + "/avatar" : null, null)!;
                    var article = new Articles.Article(account.AccountID, "policy", feed.FeedID, "policy",
                        "Icon policy", "<p>Policy fixture.</p>", null, null, serverUrl + "/policy", null, null, null,
                        null, null, authors: new System.Collections.Generic.HashSet<Articles.Author> { author },
                        attachments: null, status: new Articles.ArticleStatus("policy", true, false, DateTime.UnixEpoch));
                    model.ArticleItems[0] = new Models.FeedItem { Id = "policy", AccountID = account.AccountID, SourceArticle = article };
                    await WaitFor(() => Task.FromResult(list.ContainerFromIndex(0) is ListViewItem { IsLoaded: true }), "Replaced icon-policy row");
                    await SettleAppearanceAsync(window, list);
                    var icon = Descendants((DependencyObject)list.ContainerFromIndex(0)).OfType<IconView>().Single();
                    Require((icon.Visibility == Visibility.Visible) == avatar,
                        "Automatic icons were not rechecked after the same-size timeline gained or lost an author avatar.");
                }
                return new { SameFeed = true, SameCount = true, AvatarAdded = true, AvatarRemoved = true };
            }
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
            model.SelectedSidebarItem = null;
            await content.GoToSmartFeedAsync(new AppShared.SmartFeeds.SmartFeed(new AppShared.SmartFeeds.TodayFeedDelegate()));
            Require(ReferenceEquals(model.SelectedSidebarItem?.Tag, controller.TodayFeed),
                "Smart-feed visibility and navigation did not recognize a matching stable identity.");
            for (int i = 0; i < toggles.Length; i++)
            {
                toggles[i]!.IsOn = false;
                int hidden = i;
                await WaitFor(() => Task.FromResult(!list.Items.OfType<Models.SidebarItem>()
                    .Any(row => ReferenceEquals(row.Tag, smartFeeds[hidden]))), "Hidden smart feed removed from native outline");
                Require(!new AppDefaults().GetValue(keys[i], true), "Smart-feed visibility did not persist.");
                if (i == 0) Require(model.SelectedSidebarItem is null, "Hiding the selected smart feed did not clear its selection.");
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
