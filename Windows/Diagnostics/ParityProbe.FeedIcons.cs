#if PARITY_PROBE
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.AppShared;
using WinNewsWire.Helpers;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task CheckTimelineFeedIconSettingsAsync(MainWindow window,
        AppWindows.PreferencesWindow preferences, ViewModels.MainViewModel peer)
    {
        var root = (FrameworkElement)preferences.Content;
        var choices = (ComboBox)root.FindName("TimelineFeedIconsCombo");
        var description = (TextBlock)root.FindName("TimelineFeedIconsDescription");
        var status = (TextBlock)root.FindName("TimelineFeedIconsStatus");
        var content = window.ProbeContent;
        var selected = content.ViewModel.SelectedArticle;
        var list = (ListView)content.FindName("ArticleListView");
        var never = choices.Items.OfType<ComboBoxItem>().Single(item => item.Tag is TimelineFeedIconsDisplay.Never);
        Require(choices.SelectedItem is ComboBoxItem { Tag: TimelineFeedIconsDisplay.Always },
            "The feed-icon picker did not reflect the shared preference.");
        using (var locked = new FileStream(Path.Combine(Core.AppConfig.DataDirectory, "AppDefaults.json"),
            FileMode.Open, FileAccess.Read, FileShare.None))
        {
            choices.SelectedItem = never;
            Require(choices.SelectedItem is ComboBoxItem { Tag: TimelineFeedIconsDisplay.Always }
                && status.Text.Contains("Could not save", StringComparison.Ordinal)
                && peer.TimelineFeedIconsDisplay == TimelineFeedIconsDisplay.Always,
                "A failed feed-icon write did not restore the picker and visibly report the error.");
        }
        choices.SelectedItem = never;
        await WaitFor(() => Task.FromResult(peer.TimelineFeedIconsDisplay == TimelineFeedIconsDisplay.Never
            && Descendants(list).OfType<IconView>().Any()
            && Descendants(list).OfType<IconView>().All(icon => icon.Visibility == Visibility.Collapsed)),
            "Never hides standard timeline icons without hiding thumbnails");
        Require(string.IsNullOrEmpty(status.Text)
            && new Core.AppDefaults().GetTimelineFeedIconsDisplay() == TimelineFeedIconsDisplay.Never,
            "The feed-icon preference did not persist or clear its previous save error.");
        Core.AppDefaults.Shared.UseColumnLayout = true;
        await WaitFor(() => Task.FromResult(!Descendants(list).OfType<ThumbnailView>().Any()
            && Descendants(list).OfType<IconView>().Any()
            && Descendants(list).OfType<IconView>().All(icon => icon.Visibility == Visibility.Visible)),
            "Column layout keeps feed icons even when standard icons are hidden");
        Core.AppDefaults.Shared.UseColumnLayout = false;
        await WaitFor(() => Task.FromResult(Descendants(list).OfType<ThumbnailView>().Any()
            && Descendants(list).OfType<IconView>().Any()
            && Descendants(list).OfType<IconView>().All(icon => icon.Visibility == Visibility.Collapsed)),
            "Returning to standard layout preserves the icon preference on new containers");
        choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().Single(item => item.Tag is TimelineFeedIconsDisplay.Automatic);
        Require(description.Text == AppStrings.Get("FeedIconsAutomaticDescription"), "Automatic icon display has no explanation.");
        await Task.Run(() => Core.AppDefaults.Shared.SetTimelineFeedIconsDisplay(TimelineFeedIconsDisplay.Always));
        await WaitFor(() => Task.FromResult(choices.SelectedItem is ComboBoxItem { Tag: TimelineFeedIconsDisplay.Always }
            && string.IsNullOrEmpty(description.Text) && Descendants(list).OfType<IconView>().Any()
            && Descendants(list).OfType<IconView>().All(icon => icon.Visibility == Visibility.Visible)),
            "Worker-originated feed-icon change updates Preferences and realized rows");
        Require(ReferenceEquals(content.ViewModel.SelectedArticle, selected), "An icon preference change lost article selection.");
    }
}
#endif
