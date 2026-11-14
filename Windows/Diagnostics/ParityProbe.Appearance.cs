#if PARITY_PROBE
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Core;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
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
