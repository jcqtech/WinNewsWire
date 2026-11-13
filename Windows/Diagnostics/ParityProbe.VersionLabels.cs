#if PARITY_PROBE
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNewsWire.Helpers;

namespace WinNewsWire.Diagnostics;

internal static partial class ParityProbe
{
    private static async Task<object?> CheckVersionLabelsAsync()
    {
        var preferences = new AppWindows.PreferencesWindow();
        try
        {
            preferences.AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 1000));
            preferences.Activate();
            await WaitFor(() => Task.FromResult(preferences.Content is FrameworkElement { IsLoaded: true }),
                "Preferences version label");
            var label = Descendants(preferences.Content).OfType<CopyableLabel>().Single();
            Require(label.Text == $"WinNewsWire {App.DisplayVersion}", "Settings version text lost the application name or full version.");
            await CheckCopyableLabelAsync(label);
            return new { SettingsVersion = true, WholeTextCopy = true };
        }
        finally { preferences.Close(); }
    }

    private static async Task CheckCopyableLabelAsync(CopyableLabel label)
    {
        Windows.ApplicationModel.DataTransfer.DataPackage? copied = null;
        label.ProbeClipboardWriter = package => copied = package;
        var menu = (MenuFlyout)label.ContextFlyout;
        try
        {
            menu.ShowAt(label);
            await WaitFor(() => Task.FromResult(menu.Items.OfType<MenuFlyoutItem>().Any(item => item.IsLoaded)),
                "Version Copy menu");
            InvokeMenuItem(menu.Items.OfType<MenuFlyoutItem>().Single());
            await WaitFor(() => Task.FromResult(copied is not null), "Version Copy command");
            Require(copied is not null && await copied.GetView().GetTextAsync() == label.Text,
                "Version Copy did not preserve the whole label without selecting text.");
        }
        finally
        {
            menu.Hide();
            label.ProbeClipboardWriter = null;
        }
    }
}
#endif
