#if PARITY_PROBE
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppShared;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
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
